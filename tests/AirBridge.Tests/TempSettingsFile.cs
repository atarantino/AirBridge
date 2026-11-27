namespace AirBridge.Tests;

/// <summary>
/// A settings.json path in a unique temporary directory, deleted on dispose.
/// Without initial JSON the directory is not created, so SettingsStore.Save
/// still has to create it.
/// </summary>
internal sealed class TempSettingsFile : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"AirBridge.Tests.{Guid.NewGuid():N}");

    public TempSettingsFile(string? json = null)
    {
        FilePath = Path.Combine(_directory, "settings.json");
        if (json is null) return;
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);
    }

    public string FilePath { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
