using System.Text.Json;
using AirBridge.Core;
using AirBridge.App;

namespace AirBridge.Tests;

public sealed class SilenceStandbyTests
{
    private sealed class FakeRaopClient : IRaopClient
    {
        private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
        public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged;
        public List<(string ReceiverId, int Volume)> Starts { get; } = [];
        public int StopAllCount { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReceiverInfo>>([]);
        public Task<JsonElement> StartStreamAsync(ReceiverInfo receiver, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default)
        {
            Starts.Add((receiver.Id, initialVolume));
            StateChanged?.Invoke(this, (receiver.Id, StreamState.Streaming, null));
            return Task.FromResult(Empty);
        }
        public Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default) { StopAllCount++; return Task.FromResult(Empty); }
        public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task<JsonElement> PlayDiagnosticToneAsync(string receiverName, double seconds, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void ForceTerminate() { }
    }

    private sealed class FakeCaptureService : IAudioCaptureService
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public Task StartSystemAsync(CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) { StartCount++; return Task.CompletedTask; }
        public Task StartProcessTreeAsync(int processId, bool exclude = false, CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) { StartCount++; return Task.CompletedTask; }
        public void Stop() => StopCount++;
        public void Dispose() { }
    }

    [Fact]
    public void PeakBelowTenIsSilentAndBoundaryTenIsActive()
    {
        var below = new byte[4];
        BitConverter.TryWriteBytes(below.AsSpan(0, 2), (short)9);
        var boundary = new byte[4];
        BitConverter.TryWriteBytes(boundary.AsSpan(0, 2), (short)-10);
        Assert.False(PcmSignalDetector.ContainsSignal(below));
        Assert.True(PcmSignalDetector.ContainsSignal(boundary));
    }

    [Fact]
    public void SustainedSilenceTriggersOnceAtConfiguredBoundary()
    {
        var tracker = new SilenceStandbyTracker();
        tracker.Configure(true, 10);
        for (var tick = 0; tick < 499; tick++)
            Assert.Equal(SilenceStandbyAction.None, tracker.Observe(false, true, false));
        Assert.Equal(SilenceStandbyAction.EnterStandby, tracker.Observe(false, true, false));
        Assert.Equal(SilenceStandbyAction.None, tracker.Observe(false, true, false));
    }

    [Fact]
    public void RealAudioResetsTimerAndResumesStandbyAtLiveEdge()
    {
        var tracker = new SilenceStandbyTracker();
        tracker.Configure(true, 10);
        for (var tick = 0; tick < 300; tick++) tracker.Observe(false, true, false);
        tracker.Observe(true, true, false);
        for (var tick = 0; tick < 499; tick++) Assert.Equal(SilenceStandbyAction.None, tracker.Observe(false, true, false));
        Assert.Equal(SilenceStandbyAction.EnterStandby, tracker.Observe(false, true, false));
        Assert.Equal(SilenceStandbyAction.Resume, tracker.Observe(true, true, true));
    }

    [Fact]
    public void MeasurementSuppressionPreventsAndResetsStandbyCountdown()
    {
        var tracker = new SilenceStandbyTracker();
        tracker.Configure(true, 10);
        for (var tick = 0; tick < 499; tick++) tracker.Observe(false, true, false);
        Assert.Equal(SilenceStandbyAction.None, tracker.Observe(false, true, false, suppressed: true));
        for (var tick = 0; tick < 499; tick++) Assert.Equal(SilenceStandbyAction.None, tracker.Observe(false, true, false));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    public void RejectsInvalidTimeout(int seconds)
    {
        var tracker = new SilenceStandbyTracker();
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Configure(true, seconds));
    }

    [Fact]
    public void ResumePlanCarriesLastVolumeAndAlignmentTrim()
    {
        var receiver = new ReceiverInfo("speakerA", "Speaker A", "local", false, DateTimeOffset.UtcNow);
        var plan = ReceiverResumePlan.Create([new(receiver, StreamState.Standby, 18, DateTimeOffset.UtcNow, null, 60)]);

        var setting = Assert.Single(plan);
        Assert.Equal("speakerA", setting.ReceiverId);
        Assert.Equal(18, setting.Volume);
        Assert.Equal(60, setting.AlignmentTrimMilliseconds);
    }

    [Fact]
    public async Task RouteTransitionStopsBeforeReleaseThenRestoresTrimsGateAndVolumesInOrder()
    {
        List<string> events = [];
        await StandbyRouteTransition.EnterAsync(
            _ => { events.Add("stop-all"); return Task.CompletedTask; },
            () => events.Add("released"));

        ReceiverResumeSetting[] plan =
        [
            new("speakerA", 18, 60),
            new("beam", 27, 0)
        ];
        await StandbyRouteTransition.ResumeAsync(
            plan,
            (id, trim) => events.Add($"trim:{id}:{trim}"),
            ids => events.Add($"gate:{string.Join(',', ids)}"),
            (id, volume, _) => { events.Add($"start:{id}:{volume}"); return Task.CompletedTask; });

        Assert.Equal(
        [
            "stop-all",
            "released",
            "trim:speakerA:60",
            "trim:beam:0",
            "gate:speakerA,beam",
            "start:speakerA:18",
            "start:beam:27"
        ], events);
    }

    [Fact]
    public async Task FailedRaopStopNeverMarksRouteReleased()
    {
        var released = false;
        await Assert.ThrowsAsync<IOException>(() => StandbyRouteTransition.EnterAsync(
            _ => Task.FromException(new IOException("receiver did not release")),
            () => released = true));

        Assert.False(released);
    }

    [Fact]
    public async Task ControllerReleasesThenFirstActiveBlockRestartsSameVolumeAndTrimWithoutRestartingCapture()
    {
        var raop = new FakeRaopClient();
        var capture = new FakeCaptureService();
        await using var controller = new AirBridgeController(raop, capture);
        var receiver = new ReceiverInfo("private-speakerA-id", "Speaker A", "local", false, DateTimeOffset.UtcNow);
        controller.ConfigureSettings(new AirBridgeSettings
        {
            ReceiverAlignmentTrimMs = new(StringComparer.Ordinal) { [receiver.Id] = 60 },
            SilenceStandbyEnabled = true,
            SilenceStandbySeconds = 10
        });
        await controller.StartSystemAsync([receiver], new Dictionary<string, int> { [receiver.Id] = 18 });
        Assert.Equal(StreamState.Streaming, controller.Coordinator.Route.State);
        var captureStopsBeforeStandby = capture.StopCount;

        for (var tick = 0; tick < 500; tick++) controller.ObserveCaptureActivityForTest(false);
        Assert.True(SpinWait.SpinUntil(
            () => controller.Coordinator.Route.State == StreamState.Standby,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(captureStopsBeforeStandby, capture.StopCount);
        Assert.Equal(1, capture.StartCount);

        controller.ObserveCaptureActivityForTest(true);
        Assert.True(SpinWait.SpinUntil(
            () => controller.Coordinator.Route.State == StreamState.Streaming && raop.Starts.Count == 2,
            TimeSpan.FromSeconds(2)));

        Assert.Equal([(receiver.Id, 18), (receiver.Id, 18)], raop.Starts);
        Assert.Equal(60, controller.GetReceiverAlignmentTrim(receiver.Id));
        Assert.Equal(1, capture.StartCount);
        Assert.True(raop.StopAllCount >= 2); // initial route cleanup plus standby release
    }
}
