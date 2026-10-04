using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AirBridge.Core;

public sealed record AppUpdate(Version Version, Uri InstallerUrl, long Bytes, string Sha256);

/// <summary>Stable releases from the project's public GitHub feed. No credentials are sent.</summary>
public sealed class AppUpdateClient(HttpClient http)
{
    public const string ReleasesUrl = "https://github.com/atarantino/AirBridge/releases/latest";
    private const string Repository = "https://github.com/atarantino/AirBridge";
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;

    public async Task<AppUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/atarantino/AirBridge/releases/latest");
        request.Headers.UserAgent.ParseAdd("AirBridge-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(cancellationToken), currentVersion);
    }

    public static AppUpdate? ParseRelease(string json, Version currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("The latest release has an unsupported version. Open the releases page for details.");
        // Assembly versions have a fourth zero component; release tags and MSI use three.
        if (version <= new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build))) return null;
        var assets = release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == "AirBridge-Setup.exe").ToArray();
        if (assets.Length != 1) throw new InvalidDataException("The latest release's installer is not ready. Try again later or open the releases page.");
        var asset = assets[0];
        var url = asset.GetProperty("browser_download_url").GetString();
        var expectedUrl = $"{Repository}/releases/download/{Uri.EscapeDataString(tag)}/AirBridge-Setup.exe";
        if (url != expectedUrl) throw new InvalidDataException("The release installer URL is not from AirBridge's repository.");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumInstallerBytes) throw new InvalidDataException("The release installer has an invalid size.");
        var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("The latest installer has no SHA-256 checksum. Open the releases page to update manually.");
        return new(version, new Uri(expectedUrl), size, digest[7..]);
    }

    public async Task<string> DownloadAsync(AppUpdate update, string dataDirectory, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        // Validate even callers that did not obtain the descriptor through CheckAsync.
        if (update.InstallerUrl.Scheme != "https" || update.InstallerUrl.Host != "github.com" ||
            !update.InstallerUrl.AbsolutePath.StartsWith("/atarantino/AirBridge/releases/download/", StringComparison.Ordinal) ||
            !update.InstallerUrl.AbsolutePath.EndsWith("/AirBridge-Setup.exe", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(update.InstallerUrl.Query) || !string.IsNullOrEmpty(update.InstallerUrl.UserInfo) ||
            update.InstallerUrl.Port != 443 || update.Bytes <= 0 || update.Bytes > MaximumInstallerBytes ||
            update.Sha256.Length != 64 || !update.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid update descriptor.");
        var directory = Path.Combine(dataDirectory, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "AirBridge-Setup.exe.partial");
        var installer = Path.Combine(directory, "AirBridge-Setup.exe");
        try
        {
            using var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Bytes)
                throw new InvalidDataException("The installer size changed. Check for updates again.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += count;
                    if (received > update.Bytes) throw new InvalidDataException("The installer exceeded its expected size.");
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report((int)(received * 100 / update.Bytes));
                }
                if (received != update.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installer verification failed. Nothing was installed; check for updates and try again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, installer);
            return installer;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }
}
