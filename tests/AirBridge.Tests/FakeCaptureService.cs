using AirBridge.App;

namespace AirBridge.Tests;

internal sealed class FakeCaptureService : IAudioCaptureService
{
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public Task StartSystemAsync(CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) { StartCount++; return Task.CompletedTask; }
    public Task StartProcessTreeAsync(int processId, bool exclude = false, CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) { StartCount++; return Task.CompletedTask; }
    public void Stop() => StopCount++;
    public void Dispose() { }
}
