using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void SettingsRoundTripSpeakerGroupsAndVolumes()
    {
        using var file = new TempSettingsFile();
        var store = new SettingsStore(file.FilePath);
        store.Save(new AirBridgeSettings
        {
            SelectedReceiverIds = ["speakerA", "office"],
            ReceiverVolumes = new() { ["speakerA"] = 30, ["office"] = 42 },
            ReceiverAlignmentTrimMs = new() { ["speakerA"] = 120, ["office"] = 0 },
            SilenceStandbyEnabled = true,
            SilenceStandbySeconds = 120,
            SpeakerGroups = [new("downstairs", "Downstairs", ["speakerA", "office"])],
            CalibrationMicrophoneName = "Desk microphone",
            ThemeMode = "dark"
        });

        var restored = store.Load();
        Assert.Equal(["speakerA", "office"], restored.SelectedReceiverIds);
        Assert.Equal(42, restored.ReceiverVolumes["office"]);
        Assert.Equal(120, restored.ReceiverAlignmentTrimMs["speakerA"]);
        Assert.Equal(0, restored.ReceiverAlignmentTrimMs["office"]);
        Assert.True(restored.SilenceStandbyEnabled);
        Assert.Equal(120, restored.SilenceStandbySeconds);
        Assert.Equal("Downstairs", Assert.Single(restored.SpeakerGroups).Name);
        Assert.Equal("Desk microphone", restored.CalibrationMicrophoneName);
        Assert.Equal("dark", restored.ThemeMode);
    }

    [Fact]
    public void OlderSettingsLoadWithAlignmentAndStandbyDefaultsWithoutLosingExistingValues()
    {
        using var file = new TempSettingsFile("""
            {
              "defaultReceiverName": "Family Room",
              "selectedReceiverIds": ["beam"],
              "receiverVolumes": { "beam": 17 },
              "themeMode": "dark"
            }
            """);
        var store = new SettingsStore(file.FilePath);

        store.Save(store.Load());
        var reloaded = store.Load();

        Assert.Equal("Family Room", reloaded.DefaultReceiverName);
        Assert.Equal(["beam"], reloaded.SelectedReceiverIds);
        Assert.Equal(17, reloaded.ReceiverVolumes["beam"]);
        Assert.Empty(reloaded.ReceiverAlignmentTrimMs);
        Assert.True(reloaded.SilenceStandbyEnabled);
        Assert.Equal(60, reloaded.SilenceStandbySeconds);
        Assert.Equal("dark", reloaded.ThemeMode);
    }

    [Fact]
    public void SettingsLoadNormalizesExplicitNullCollectionsAndUnsafeBounds()
    {
        using var file = new TempSettingsFile("""
            {
              "selectedReceiverIds": null,
              "receiverVolumes": { "beam": 140 },
              "receiverAlignmentTrimMs": null,
              "speakerGroups": null,
              "silenceStandbySeconds": 999
            }
            """);

        var restored = new SettingsStore(file.FilePath).Load();

        Assert.Empty(restored.SelectedReceiverIds);
        Assert.Equal(100, restored.ReceiverVolumes["beam"]);
        Assert.Empty(restored.ReceiverAlignmentTrimMs);
        Assert.Empty(restored.SpeakerGroups);
        Assert.Equal(600, restored.SilenceStandbySeconds);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(5000, 1000)]
    public void LoadClampsHoldThreshold(int persisted, int expected)
    {
        using var file = new TempSettingsFile($$"""{"pushToTalkHoldThresholdMs":{{persisted}}}""");
        Assert.Equal(expected, new SettingsStore(file.FilePath).Load().PushToTalkHoldThresholdMs);
    }

    [Fact]
    public void LegacySettingsUsePushToTalkDefaults()
    {
        using var file = new TempSettingsFile("{}");
        var settings = new SettingsStore(file.FilePath).Load();
        Assert.Equal("Ctrl+Alt+Space", settings.PushToTalkShortcut);
        Assert.Equal(250, settings.PushToTalkHoldThresholdMs);
        Assert.Null(settings.VoiceHudX);
        Assert.Null(settings.VoiceHudY);
    }
}
