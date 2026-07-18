using System.Text.Json;

namespace AirBridge.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Path { get; }

    public SettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirBridge", "settings.json");
    }

    public AirBridgeSettings Load()
    {
        try
        {
            var settings = File.Exists(Path) ? JsonSerializer.Deserialize<AirBridgeSettings>(File.ReadAllText(Path), Options) ?? new() : new();
            return settings with
            {
                SelectedReceiverIds = settings.SelectedReceiverIds ?? [],
                ReceiverVolumes = settings.ReceiverVolumes is null
                    ? new(StringComparer.Ordinal)
                    : settings.ReceiverVolumes.ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, 100), StringComparer.Ordinal),
                ReceiverAlignmentTrimMs = settings.ReceiverAlignmentTrimMs is null
                    ? new(StringComparer.Ordinal)
                    : settings.ReceiverAlignmentTrimMs.ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, 500), StringComparer.Ordinal),
                SpeakerGroups = settings.SpeakerGroups ?? [],
                SilenceStandbySeconds = Math.Clamp(settings.SilenceStandbySeconds, 10, 600),
                PushToTalkHoldThresholdMs = Math.Clamp(settings.PushToTalkHoldThresholdMs, 100, 1000)
            };
        }
        catch (JsonException) { return new(); }
    }

    public void Save(AirBridgeSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, Path, true);
    }
}
