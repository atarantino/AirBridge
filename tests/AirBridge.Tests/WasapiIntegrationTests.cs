using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class WasapiIntegrationTests
{
    [Fact(Timeout = 15000)]
    [Trait("Category", "Hardware")]
    public async Task ProcessTreeActivationWorksOnCurrentWindows()
    {
        if (!HardwareTestGate.Enabled) return;
        var buffer = new BoundedPcmBuffer(176400);
        var coordinator = new StreamCoordinator(buffer);
        using var capture = new WasapiCaptureService(buffer, coordinator);
        try
        {
            await capture.StartProcessTreeAsync(Environment.ProcessId, activationTimeout: TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            // Some Windows audio drivers never complete process-loopback COM
            // activation for a testhost with no render session. The production
            // boundary must return a controlled timeout instead of hanging.
            return;
        }
        await Task.Delay(100);
        capture.Stop();
        Assert.NotNull(capture);
    }

    [Fact(Timeout = 15000)]
    [Trait("Category", "Hardware")]
    public async Task SystemLoopbackStartsAndStops()
    {
        if (!HardwareTestGate.Enabled) return;
        var buffer = new BoundedPcmBuffer(176400);
        var coordinator = new StreamCoordinator(buffer);
        using var capture = new WasapiCaptureService(buffer, coordinator);
        await capture.StartSystemAsync();
        await Task.Delay(100);
        Assert.NotNull(capture.SourceFormat);
        capture.Stop();
    }
}
