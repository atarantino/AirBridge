using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class AppUpdateTests
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("offline installer fixture; never execute");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Installer));
    private static string Release(string tag = "v1.0.4", bool draft = false, bool prerelease = false,
        string? url = null, string? digest = null, bool hasInstaller = true) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease,
        assets = hasInstaller ? new[] { new
        {
            name = "AirBridge-Setup.exe",
            browser_download_url = url ?? $"https://github.com/atarantino/AirBridge/releases/download/{tag}/AirBridge-Setup.exe",
            size = Installer.Length, digest = digest ?? "sha256:" + Hash
        } } : []
    });

    [Fact]
    public void StableNewerReleaseOffersInstallerButSameOlderAndPrereleaseDoNot()
    {
        Assert.Equal(new Version(1, 0, 4), AppUpdateClient.ParseRelease(Release(), new(1, 0, 3, 0))!.Version);
        Assert.Null(AppUpdateClient.ParseRelease(Release("v1.0.3"), new(1, 0, 3, 0)));
        Assert.Null(AppUpdateClient.ParseRelease(Release("v1.0.2"), new(1, 0, 3, 0)));
        Assert.Null(AppUpdateClient.ParseRelease(Release(draft: true), new(1, 0, 3)));
        Assert.Null(AppUpdateClient.ParseRelease(Release(prerelease: true), new(1, 0, 3)));
    }

    [Theory]
    [InlineData("https://example.com/AirBridge-Setup.exe")]
    [InlineData("https://github.com/other/AirBridge/releases/download/v1.0.4/AirBridge-Setup.exe")]
    [InlineData("http://github.com/atarantino/AirBridge/releases/download/v1.0.4/AirBridge-Setup.exe")]
    public void RejectsUntrustedInstallerUrls(string url) =>
        Assert.Throws<InvalidDataException>(() => AppUpdateClient.ParseRelease(Release(url: url), new(1, 0, 3)));

    [Fact]
    public void IncompleteReleaseAndMissingChecksumFailClearly()
    {
        Assert.Throws<InvalidDataException>(() => AppUpdateClient.ParseRelease(Release(hasInstaller: false), new(1, 0, 3)));
        Assert.Throws<InvalidDataException>(() => AppUpdateClient.ParseRelease(Release(digest: ""), new(1, 0, 3)));
        Assert.Throws<InvalidDataException>(() => AppUpdateClient.ParseRelease(Release("v1.0.4-beta"), new(1, 0, 3)));
    }

    [Fact]
    public async Task CheckUsesPublicFeedWithoutApiKeysAndHandlesNoRelease()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://api.github.com/repos/atarantino/AirBridge/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("AirBridge", request.Headers.UserAgent.ToString());
            return new(HttpStatusCode.NotFound);
        }));
        Assert.Null(await new AppUpdateClient(http).CheckAsync(new(1, 0, 3), CancellationToken.None));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    public async Task OnlyVerifiedCompleteDownloadsBecomeInstallersAndNeverTouchProfile(string scenario)
    {
        var directory = Directory.CreateTempSubdirectory("airbridge-update-");
        try
        {
            var store = new SettingsStore(Path.Combine(directory.FullName, "settings.json"));
            store.Save(new() { ThemeMode = "dark", ReceiverVolumes = new() { ["speaker"] = 42 }, AutomaticallyCheckForUpdates = false });
            var settingsBefore = File.ReadAllBytes(store.Path);
            var pairingPath = Path.Combine(directory.FullName, "pairing.json");
            File.WriteAllText(pairingPath, "pairing fixture");
            var payload = scenario switch
            {
                "corrupt" => Installer.Select(value => (byte)(value ^ 1)).ToArray(),
                "truncated" => Installer[..^1],
                "oversized" => Installer.Concat(new byte[] { 0 }).ToArray(),
                _ => Installer
            };
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
            var update = AppUpdateClient.ParseRelease(Release(), new(1, 0, 3))!;
            if (scenario == "valid")
            {
                var result = await new AppUpdateClient(http).DownloadAsync(update, directory.FullName, null, CancellationToken.None);
                Assert.Equal(Installer, File.ReadAllBytes(result));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateClient(http).DownloadAsync(update, directory.FullName, null, CancellationToken.None));
                Assert.Empty(Directory.GetFiles(directory.FullName, "*.exe", SearchOption.AllDirectories));
            }
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.partial", SearchOption.AllDirectories));
            Assert.Equal(settingsBefore, File.ReadAllBytes(store.Path));
            Assert.Equal("pairing fixture", File.ReadAllText(pairingPath));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task CanceledDownloadLeavesNoExecutable()
    {
        var directory = Directory.CreateTempSubdirectory("airbridge-update-");
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var http = new HttpClient(new Handler(_ =>
            {
                cancellation.Cancel();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
            }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AppUpdateClient(http).DownloadAsync(
                AppUpdateClient.ParseRelease(Release(), new(1, 0, 3))!, directory.FullName, null, cancellation.Token));
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.exe", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.partial", SearchOption.AllDirectories));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void LegacyAndFuturePreferencesSurviveSavingUpdatePreference()
    {
        var directory = Directory.CreateTempSubdirectory("airbridge-update-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, """{"themeMode":"dark","selectedReceiverIds":["desk"],"receiverVolumes":{"desk":42},"futurePreference":{"enabled":true}}""");
            var store = new SettingsStore(path);
            Assert.True(store.Load().AutomaticallyCheckForUpdates);
            store.Save(store.Load() with { AutomaticallyCheckForUpdates = false });
            var settings = new SettingsStore(path).Load();
            Assert.False(settings.AutomaticallyCheckForUpdates);
            Assert.Equal("dark", settings.ThemeMode);
            Assert.Equal("desk", Assert.Single(settings.SelectedReceiverIds));
            Assert.Equal(42, settings.ReceiverVolumes["desk"]);
            Assert.True(settings.AdditionalSettings!["futurePreference"].GetProperty("enabled").GetBoolean());
            Assert.Equal("AirBridge/OpenAI API Key", RuntimeProfile.ResolveCredentialTarget(null));
        }
        finally { directory.Delete(true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
