using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class ReceiverVolumePlanTests
{
    [Fact]
    public void ResolvesSavedPerReceiverVolumeBeforeSessionStart()
    {
        var values = new Dictionary<string, int> { ["speakerA"] = 14, ["living"] = 64 };

        Assert.Equal(14, ReceiverVolumePlan.Resolve("speakerA", values));
        Assert.Equal(64, ReceiverVolumePlan.Resolve("living", values));
        Assert.Equal(ReceiverVolumePlan.SafeDefault, ReceiverVolumePlan.Resolve("office", values));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(120, 100)]
    public void ClampsSavedVolume(int stored, int expected)
    {
        Assert.Equal(expected, ReceiverVolumePlan.Resolve("speakerA", new Dictionary<string, int> { ["speakerA"] = stored }));
    }
}
