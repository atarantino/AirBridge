using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class StreamVerificationTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    private static StreamVerificationObservation Observation(long progress = 0, bool active = true,
        long starvation = 0, long idlePadding = 0, long epoch = 0, StreamState state = StreamState.Streaming,
        string streamId = "stream-1", string receiverId = "receiver-1", long overruns = 0) =>
        new(streamId, state, new Dictionary<string, ReceiverVerificationObservation>
        {
            [receiverId] = new(state, new(882000, 88200, 500, progress, progress,
                overruns, idlePadding > 0 ? 1 : 0, epoch, idlePadding, starvation, active ? progress : 0))
        });

    private static StreamVerificationResult Finish(ManualClock clock, StreamVerificationWindow window,
        Func<int, StreamVerificationObservation>? observation = null)
    {
        for (var index = 1; index <= 40; index++)
        {
            clock.Advance(250);
            window.Observe(observation?.Invoke(index) ?? Observation(index * 44100));
        }
        return window.Result();
    }

    [Fact]
    public void StableActivePcmVerifiesUsingFreshPerReceiverDeltas()
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(), clock);
        var result = Finish(clock, window);
        Assert.Equal(StreamVerificationStatus.Verified, result.Status);
        Assert.True(result.Verified);
        Assert.Equal(10000, result.ObservedMilliseconds);
        Assert.Equal(1764000, result.Receivers["receiver-1"].ActiveBytesWritten);
        Assert.True(result.ObservationContinuous);
        Assert.Contains("Acoustic output was not measured", result.Note);
    }

    [Fact]
    public void SilentPcmAndBenignIdlePaddingCannotVerifyAnAudioFix()
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(active: false), clock);
        var result = Finish(clock, window, index => Observation(index * 44100, active: false, idlePadding: index * 400));
        Assert.Equal(StreamVerificationStatus.Inconclusive, result.Status);
        Assert.Null(result.Verified);
        Assert.True(result.NoActiveStarvation);
        Assert.False(result.ActivePcmAdvanced);
    }

    [Fact]
    public void IdlePaddingDoesNotInvalidateOtherwiseObservedActiveAudio()
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(), clock);
        var result = Finish(clock, window, index => Observation(index * 44100, idlePadding: index * 400));
        Assert.Equal(StreamVerificationStatus.Verified, result.Status);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("route")]
    [InlineData("receiver")]
    [InlineData("epoch")]
    [InlineData("starvation")]
    [InlineData("overrun")]
    public void AnInterruptedWindowCannotPassAfterTheFinalStreamingSnapshotRecovers(string interruption)
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(), clock);
        var result = Finish(clock, window, index => index == 20 ? interruption switch
        {
            "state" => Observation(index * 44100, state: StreamState.Reconnecting),
            "route" => Observation(index * 44100, streamId: "stream-2"),
            "receiver" => Observation(index * 44100, receiverId: "replacement"),
            "epoch" => Observation(index * 44100, epoch: 1),
            "starvation" => Observation(index * 44100, starvation: 400),
            _ => Observation(index * 44100, overruns: 1)
        } : Observation(index * 44100));
        Assert.Equal(StreamVerificationStatus.Failed, result.Status);
        Assert.False(result.Verified);
    }

    [Fact]
    public void MissingIntermediateEvidenceIsInconclusiveEvenWithAPerfectFinalSnapshot()
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(), clock);
        clock.Advance(10000);
        window.Observe(Observation(1764000));
        Assert.Equal(StreamVerificationStatus.Inconclusive, window.Result().Status);
        Assert.False(window.Result().ObservationContinuous);
        Assert.Null(window.Result().Verified);
    }

    [Fact]
    public void AnUnfinishedWindowNeverReportsVerified()
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(), clock);
        clock.Advance(250);
        window.Observe(Observation(44100));
        Assert.Equal(StreamVerificationStatus.Observing, window.Result().Status);
        Assert.Null(window.Result().Verified);
    }

    [Fact]
    public void EveryReceiverMustAdvanceActiveAudioAndConsumption()
    {
        var clock = new ManualClock();
        StreamVerificationObservation Group(int progress)
        {
            var sample = Observation(progress);
            return sample with { Receivers = new Dictionary<string, ReceiverVerificationObservation>(sample.Receivers)
                { ["idle-sibling"] = new(StreamState.Streaming, Observation(active: false).Receivers["receiver-1"].Buffer) } };
        }
        var window = new StreamVerificationWindow(Group(0), clock);
        var result = Finish(clock, window, index => Group(index * 44100));
        Assert.Equal(StreamVerificationStatus.Inconclusive, result.Status);
    }

    [Fact]
    public void TokenByteProgressCannotPretendToBeAFullActiveAudioObservation()
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(), clock);
        var result = Finish(clock, window, index => Observation(index * 4));
        Assert.Equal(StreamVerificationStatus.Inconclusive, result.Status);
        Assert.False(result.ActivePcmAdvanced);
    }

    [Fact]
    public void LateFinalObservationCannotFillAnUnobservedGap()
    {
        var clock = new ManualClock();
        var window = new StreamVerificationWindow(Observation(), clock);
        for (var index = 1; index <= 4; index++)
        {
            clock.Advance(250);
            window.Observe(Observation(index * 44100));
        }
        clock.Advance(9000);
        Assert.Equal(StreamVerificationStatus.Inconclusive, window.Result().Status);
        window.Observe(Observation(1764000));
        Assert.Equal(StreamVerificationStatus.Inconclusive, window.Result().Status);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("route")]
    [InlineData("receiver")]
    [InlineData("epoch")]
    [InlineData("starvation")]
    [InlineData("overrun")]
    public void ACompletedFixDoesNotApplyAfterReceiverHealthOrTheRouteChanges(string change)
    {
        var completed = Observation(1764000);
        var current = change switch
        {
            "state" => Observation(1768000, state: StreamState.Reconnecting),
            "route" => Observation(1768000, streamId: "stream-2"),
            "receiver" => Observation(1768000, receiverId: "replacement"),
            "epoch" => Observation(1768000, epoch: 1),
            "starvation" => Observation(1768000, starvation: 400),
            _ => Observation(1768000, overruns: 1)
        };
        Assert.False(StreamVerificationWindow.AppliesToCurrentStream(completed, current));
        Assert.True(StreamVerificationWindow.AppliesToCurrentStream(completed, Observation(1768000)));
    }

    private sealed class PingRaop(bool acknowledged = true, bool fail = false) : IRaopClient
    {
        public int PingCount { get; private set; }
        public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<JsonElement> PingAsync(CancellationToken cancellationToken = default)
        {
            PingCount++;
            return fail ? Task.FromException<JsonElement>(new IOException("Host exited")) : Task.FromResult(JsonSerializer.SerializeToElement(new { ok = acknowledged }));
        }
        public Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReceiverInfo>>([]);
        public Task<JsonElement> StartStreamAsync(ReceiverInfo receiver, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void ForceTerminate() { }
    }

    private sealed class NoCapture : IAudioCaptureService
    {
        public Task StartSystemAsync(CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) => Task.CompletedTask;
        public Task StartProcessTreeAsync(int processId, bool exclude = false, CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) => Task.CompletedTask;
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task ConnectivityInvokesTheHostInsteadOfReportingACannedRunningState()
    {
        var raop = new PingRaop();
        await using var controller = new AirBridgeController(raop, new NoCapture());
        var result = await controller.ExecuteAsync("run_connectivity_test", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Equal(1, raop.PingCount);
        Assert.Contains("responsive", JsonSerializer.Serialize(result));
        Assert.Contains("\"acoustic_output_verified\":false", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectivityCannotSucceedWithoutAnAcknowledgedHostResponse(bool fail)
    {
        await using var controller = new AirBridgeController(new PingRaop(acknowledged: false, fail), new NoCapture());
        await Assert.ThrowsAnyAsync<Exception>(() => controller.ExecuteAsync("run_connectivity_test", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
    }

    [Fact]
    public async Task DisposalCancelsTheObservationAndNeverLeavesAVerifiedClaim()
    {
        var controller = new AirBridgeController(new PingRaop(), new NoCapture(), new ManualClock());
        await controller.ExecuteAsync("set_buffer_target", JsonSerializer.SerializeToElement(new { milliseconds = 600 }), CancellationToken.None);
        Assert.Equal(StreamVerificationStatus.Observing, controller.LastBufferVerification!.Status);
        await controller.DisposeAsync();
        Assert.Equal(StreamVerificationStatus.Inconclusive, controller.LastBufferVerification!.Status);
        Assert.Null(controller.Coordinator.Health().LastFixVerified);
        Assert.Contains("cancelled", controller.LastBufferVerification.Note);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.ExecuteAsync("set_buffer_target",
            JsonSerializer.SerializeToElement(new { milliseconds = 700 }), CancellationToken.None));
    }

    [Fact]
    public async Task ReplacingABufferChangeStartsANewObservationInsteadOfUsingPriorEvidence()
    {
        await using var controller = new AirBridgeController(new PingRaop(), new NoCapture(), new ManualClock());
        await controller.ExecuteAsync("set_buffer_target", JsonSerializer.SerializeToElement(new { milliseconds = 600 }), CancellationToken.None);
        var first = controller.LastBufferVerification;
        await controller.ExecuteAsync("set_buffer_target", JsonSerializer.SerializeToElement(new { milliseconds = 700 }), CancellationToken.None);
        Assert.NotSame(first, controller.LastBufferVerification);
        Assert.Equal(StreamVerificationStatus.Observing, controller.LastBufferVerification!.Status);
        Assert.Equal(1, controller.LastBufferVerification.Samples);
        Assert.Null(controller.Coordinator.Health().LastFixVerified);
    }

    [Fact]
    public void ActiveByteCounterDistinguishesSilenceWrites()
    {
        var buffer = new BoundedPcmBuffer(4096);
        buffer.Write(new byte[400], producerActive: false);
        buffer.Write(new byte[800], producerActive: true);
        Assert.Equal(1200, buffer.Snapshot().BytesWritten);
        Assert.Equal(800, buffer.Snapshot().ActiveBytesWritten);
    }
}
