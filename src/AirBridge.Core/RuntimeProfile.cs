using System.Security.Cryptography;
using System.Text;

namespace AirBridge.Core;

/// <summary>One profile boundary shared by settings, diagnostics, credentials and the RAOP host.</summary>
public static class RuntimeProfile
{
    public const string DataDirectoryVariable = "AIRBRIDGE_DATA_DIR";
    public const string DefaultCredentialTarget = "AirBridge/OpenAI API Key";
    public static string RunId { get; } = Environment.GetEnvironmentVariable("AIRBRIDGE_RUN_ID") ?? Guid.NewGuid().ToString("N");
    public static bool IsIsolated => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirectoryVariable));
    public static string DataDirectory => ResolveDataDirectory(Environment.GetEnvironmentVariable(DataDirectoryVariable));
    public static string CredentialTarget => ResolveCredentialTarget(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    public static string ResolveDataDirectory(string? configuredDirectory, string? localApplicationData = null) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirBridge")
            : configuredDirectory);

    public static string ResolveCredentialTarget(string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory)) return DefaultCredentialTarget;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredDirectory));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        return $"AirBridge/Profiles/{hash}/OpenAI API Key";
    }

    public static void Configure(string? directory, bool requireIsolatedProfile = false)
    {
        var configured = directory ?? Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (string.IsNullOrWhiteSpace(configured) && requireIsolatedProfile)
            configured = Path.Combine(Path.GetTempPath(), "AirBridge", "qa", Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(configured))
            Environment.SetEnvironmentVariable(DataDirectoryVariable, ResolveDataDirectory(configured));
    }
}
