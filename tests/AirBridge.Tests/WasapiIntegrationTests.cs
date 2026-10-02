using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class WasapiIntegrationTests
{
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task ProcessTreeActivationWorksOnCurrentWindows()
    {
        var buffer = new BoundedPcmBuffer(176400);
        var coordinator = new StreamCoordinator(buffer);
        using var capture = new WasapiCaptureService(buffer, coordinator);
        await capture.StartProcessTreeAsync(Environment.ProcessId, activationTimeout: TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        Assert.NotNull(capture.SourceFormat);
        capture.Stop();
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task SystemLoopbackStartsAndStops()
    {
        var buffer = new BoundedPcmBuffer(176400);
        var coordinator = new StreamCoordinator(buffer);
        using var capture = new WasapiCaptureService(buffer, coordinator);
        await capture.StartSystemAsync();
        await Task.Delay(100);
        Assert.NotNull(capture.SourceFormat);
        capture.Stop();
    }
}
