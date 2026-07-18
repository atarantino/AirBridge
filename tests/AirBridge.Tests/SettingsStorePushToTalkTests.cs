using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class SettingsStorePushToTalkTests
{
    [Theory]
    [InlineData(1, 100)]
    [InlineData(5000, 1000)]
    public void LoadClampsHoldThreshold(int persisted, int expected)
    {
        var directory = Directory.CreateTempSubdirectory("airbridge-hotkey-");
        try
        {
            var path = System.IO.Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, $$"""{"pushToTalkHoldThresholdMs":{{persisted}}}""");
            Assert.Equal(expected, new SettingsStore(path).Load().PushToTalkHoldThresholdMs);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void LegacySettingsUsePushToTalkDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("airbridge-hotkey-");
        try
        {
            var path = System.IO.Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, "{}");
            var settings = new SettingsStore(path).Load();
            Assert.Equal("Ctrl+Alt+Space", settings.PushToTalkShortcut);
            Assert.Equal(250, settings.PushToTalkHoldThresholdMs);
            Assert.Null(settings.VoiceHudX);
            Assert.Null(settings.VoiceHudY);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
