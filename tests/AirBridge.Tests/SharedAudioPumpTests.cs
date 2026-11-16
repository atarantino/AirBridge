using AirBridge.App;
using AirBridge.Core;
using static AirBridge.Tests.PumpPcm;

namespace AirBridge.Tests;

public sealed class SharedAudioPumpTests
{
    [Fact]
    public async Task GroupGateKeepsQueuesIntactAndReleasesSameFirstLiveBlock()
    {
        var pump = new SharedAudioPump();
        var firstBuffer = PumpBuffer(0x31, 2);
        var secondBuffer = PumpBuffer(0x31, 2);
        var first = new FakePipeEndpoint("first");
        var second = new FakePipeEndpoint("second");
        pump.AddLeg("first", firstBuffer, first, 0);
        pump.AddLeg("second", secondBuffer, second, 0);
        pump.BeginGroup(["first", "second"]);

        pump.MarkReady("first");
        await pump.PumpOnceAsync();
        Assert.All(first.Writes[0], value => Assert.Equal(0, value));
        Assert.All(second.Writes[0], value => Assert.Equal(0, value));
        Assert.Equal(SharedAudioPump.BlockBytes * 2, firstBuffer.Snapshot().FillBytes);
        Assert.Equal(SharedAudioPump.BlockBytes * 2, secondBuffer.Snapshot().FillBytes);

        pump.MarkReady("second");
        await pump.PumpOnceAsync();
        Assert.Equal(first.Writes[1], second.Writes[1]);
        Assert.All(first.Writes[1], value => Assert.Equal(0x31, value));
        Assert.Equal(0, firstBuffer.Snapshot().FillBytes);
        Assert.Equal(0, secondBuffer.Snapshot().FillBytes);
    }

    [Fact]
    public async Task GateDoesNotConsumeFirstLiveBlockUntilEveryReadyPipeCanEnqueueIt()
    {
        var pump = new SharedAudioPump();
        var firstBuffer = PumpBuffer(0x41);
        var secondBuffer = PumpBuffer(0x41);
        var first = new FakePipeEndpoint("first");
        var second = new FakePipeEndpoint("second") { CanAcceptWrite = false };
        pump.AddLeg("first", firstBuffer, first, 0);
        pump.AddLeg("second", secondBuffer, second, 0);
        pump.BeginGroup(["first", "second"]);
        pump.MarkReady("first");
        pump.MarkReady("second");

        await pump.PumpOnceAsync();
        Assert.Equal(SharedAudioPump.BlockBytes, firstBuffer.Snapshot().FillBytes);
        Assert.Equal(SharedAudioPump.BlockBytes, secondBuffer.Snapshot().FillBytes);
        Assert.Empty(first.Writes);
        Assert.Empty(second.Writes);

        second.CanAcceptWrite = true;
        await pump.PumpOnceAsync();
        Assert.Equal(first.Writes[0], second.Writes[0]);
        Assert.All(first.Writes[0], value => Assert.Equal(0x41, value));
    }

    [Fact]
    public async Task InitialTrimWritesExactFrameAlignedSilencePreludeAtGateOpen()
    {
        var pump = new SharedAudioPump();
        var buffer = PumpBuffer(0x55, 2);
        var endpoint = new FakePipeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 10);
        pump.AddLeg("sibling", PumpBuffer(0x66, 2), new FakePipeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");

        await pump.PumpOnceAsync();

        var silenceBytes = ReceiverAlignmentPlan.ToPcmByteCount(10);
        Assert.Equal(1764, silenceBytes);
        Assert.All(endpoint.Writes[0][..silenceBytes], value => Assert.Equal(0, value));
        Assert.All(endpoint.Writes[0][silenceBytes..], value => Assert.Equal(0x55, value));
        Assert.Equal(silenceBytes, buffer.Snapshot().FillBytes);
    }

    [Fact]
    public async Task OneLegGateOpensWithoutItsSavedAlignmentDelay()
    {
        var pump = new SharedAudioPump();
        var buffer = PumpBuffer(0x6A);
        var endpoint = new FakePipeEndpoint("solo");
        pump.AddLeg("solo", buffer, endpoint, 60);
        pump.BeginGroup(["solo"]);
        pump.MarkReady("solo");

        await pump.PumpOnceAsync();

        Assert.Single(endpoint.Writes);
        Assert.All(endpoint.Writes[0], value => Assert.Equal(0x6A, value));
    }

    [Fact]
    public async Task SavedAlignmentDelayActivatesOnJoinAndIsRemovedWhenRouteReturnsToOneLeg()
    {
        var pump = new SharedAudioPump();
        var buffer = PumpBuffer(0x5A, 3);
        var endpoint = new FakePipeEndpoint("first");
        pump.AddLeg("first", buffer, endpoint, 10);
        pump.BeginGroup(["first"]);
        pump.MarkReady("first");
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[0], value => Assert.Equal(0x5A, value));

        pump.AddLeg("second", PumpBuffer(0x33, 3), new FakePipeEndpoint("second"), 0);
        buffer.Write(Block(0x5A));
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x5A, value));

        buffer.Write(Block(0x5A));
        pump.MarkReady("second");
        await pump.PumpOnceAsync();
        var silenceBytes = ReceiverAlignmentPlan.ToPcmByteCount(10);
        Assert.All(endpoint.Writes[2][..silenceBytes], value => Assert.Equal(0, value));
        Assert.All(endpoint.Writes[2][silenceBytes..], value => Assert.Equal(0x5A, value));

        pump.RemoveLeg("second");
        buffer.Clear();
        buffer.Write(Block(0x7C, 2));
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[3], value => Assert.Equal(0x7C, value));
        Assert.Equal(10, pump.GetAlignmentTrim("first"));
    }

    [Fact]
    public async Task TimeoutReleasesReadyLegAndLateLegJoinsAtLiveEdge()
    {
        var now = DateTimeOffset.UtcNow;
        var pump = new SharedAudioPump(utcNow: () => now);
        var readyBuffer = PumpBuffer(0x11, 2);
        var lateBuffer = PumpBuffer(0x22, 2);
        var ready = new FakePipeEndpoint("ready");
        var late = new FakePipeEndpoint("late");
        pump.AddLeg("ready", readyBuffer, ready, 0);
        pump.AddLeg("late", lateBuffer, late, 0);
        pump.BeginGroup(["ready", "late"], TimeSpan.FromSeconds(1));
        pump.MarkReady("ready");
        GroupGateTimeoutEventArgs? timeout = null;
        pump.GateTimedOut += (_, args) => timeout = args;
        now = now.AddSeconds(2);

        await pump.PumpOnceAsync();

        Assert.Equal(["late"], timeout!.ReceiverIds);
        Assert.All(ready.Writes[0], value => Assert.Equal(0x11, value));
        Assert.All(late.Writes[0], value => Assert.Equal(0, value));

        lateBuffer.Write(Block(0x44));
        pump.MarkReady("late");
        lateBuffer.Write(Block(0x66));
        await pump.PumpOnceAsync();
        Assert.All(late.Writes[1], value => Assert.Equal(0x44, value));
    }

    [Fact]
    public async Task LiveTenMillisecondNudgeInsertsThenDropsExactly441StereoFrames()
    {
        var pump = new SharedAudioPump();
        var buffer = new BoundedPcmBuffer(SharedAudioPump.BlockBytes * 8, 0);
        var endpoint = new FakePipeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.AddLeg("sibling", PumpBuffer(0x30, 8), new FakePipeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        buffer.Write(Block(0x10, 2));
        await pump.PumpOnceAsync();

        buffer.Write(Block(0x10));
        pump.SetAlignmentTrim("receiver", 10);
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[1][..1764], value => Assert.Equal(0, value));
        Assert.All(endpoint.Writes[1][1764..], value => Assert.Equal(0x10, value));

        buffer.Write(Block(0x20));
        pump.SetAlignmentTrim("receiver", 0);
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[2], value => Assert.Equal(0x20, value));
    }

    [Fact]
    public async Task CalibrationModeSuppressesEffectiveTrimAndRestoresConfiguredTrim()
    {
        var pump = new SharedAudioPump();
        var buffer = PumpBuffer(0x52, 6);
        var endpoint = new FakePipeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 10);
        pump.AddLeg("sibling", PumpBuffer(0x63, 6), new FakePipeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        buffer.Write(Block(0x52));
        pump.SetCalibrationMode(true);
        await pump.PumpOnceAsync();

        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x52, value));
        Assert.Equal(10, pump.GetAlignmentTrim("receiver"));

        buffer.Write(Block(0x52));
        pump.SetCalibrationMode(false);
        await pump.PumpOnceAsync();

        Assert.All(endpoint.Writes[2][..1764], value => Assert.Equal(0, value));
        Assert.All(endpoint.Writes[2][1764..], value => Assert.Equal(0x52, value));
        Assert.Equal(10, pump.GetAlignmentTrim("receiver"));
    }

    [Fact]
    public async Task StandbyDiscardsHistoryAndResumeReappliesGateAndTrimAtLiveEdge()
    {
        var pump = new SharedAudioPump();
        var buffer = new BoundedPcmBuffer(SharedAudioPump.BlockBytes * 8, 0);
        var endpoint = new FakePipeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 10);
        pump.AddLeg("sibling", PumpBuffer(0x20, 8), new FakePipeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        buffer.Write(Block(0x10));
        await pump.PumpOnceAsync();

        pump.SetStandby(true);
        buffer.Write(Block(0x33, 2));
        await pump.PumpOnceAsync();
        Assert.Equal(0, buffer.Snapshot().FillBytes);
        Assert.Single(endpoint.Writes);

        pump.BeginGroup(["receiver"]);
        buffer.Write(Block(0x44));
        buffer.Write(Block(0x77));
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        Assert.Equal(2, endpoint.Writes.Count);
        Assert.All(endpoint.Writes[1][..1764], value => Assert.Equal(0, value));
        Assert.All(endpoint.Writes[1][1764..], value => Assert.Equal(0x77, value));
    }

    [Fact]
    public async Task RejectedStalledLegCannotPreventHealthySiblingWrite()
    {
        var pump = new SharedAudioPump();
        var stalled = new FakePipeEndpoint("stalled") { AcceptWrites = false };
        var healthy = new FakePipeEndpoint("healthy");
        pump.AddLeg("stalled", PumpBuffer(0x21), stalled, 0);
        pump.AddLeg("healthy", PumpBuffer(0x42), healthy, 0);
        pump.BeginGroup(["stalled", "healthy"]);
        pump.MarkReady("stalled");
        pump.MarkReady("healthy");

        await pump.PumpOnceAsync();

        Assert.Empty(stalled.Writes);
        Assert.Single(healthy.Writes);
        Assert.All(healthy.Writes[0], value => Assert.Equal(0x42, value));
    }

    [Fact]
    public async Task LegAddedWhileInitialGateIsClosedJoinsWhenOriginalGroupOpens()
    {
        var pump = new SharedAudioPump();
        var first = new FakePipeEndpoint("first");
        var added = new FakePipeEndpoint("added");
        pump.AddLeg("first", PumpBuffer(0x12), first, 0);
        pump.BeginGroup(["first"]);
        pump.AddLeg("added", PumpBuffer(0x34), added, 0);
        pump.MarkReady("added");
        pump.MarkReady("first");

        await pump.PumpOnceAsync();

        Assert.All(first.Writes[0], value => Assert.Equal(0x12, value));
        Assert.All(added.Writes[0], value => Assert.Equal(0x34, value));
    }

    [Fact]
    public async Task ReconnectingLegDiscardsHistoryBeforeRejoiningLiveEdge()
    {
        var pump = new SharedAudioPump();
        var buffer = PumpBuffer(0x10);
        var endpoint = new FakePipeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.AddLeg("sibling", PumpBuffer(0x20, 4), new FakePipeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        pump.MarkNotReady("receiver");
        buffer.Write(Block(0x22, 2));
        buffer.Write(Block(0x77));
        pump.MarkReady("receiver");
        await pump.PumpOnceAsync();

        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x77, value));
    }

    [Fact]
    public async Task OppositePendingNudgesCoalesceToNoAudioMutation()
    {
        var pump = new SharedAudioPump();
        var buffer = PumpBuffer(0x51);
        var endpoint = new FakePipeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.AddLeg("sibling", PumpBuffer(0x20), new FakePipeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        buffer.Write(Block(0x62));
        buffer.Write(Block(0x73));
        pump.SetAlignmentTrim("receiver", 500);
        pump.SetAlignmentTrim("receiver", 0);
        await pump.PumpOnceAsync();
        await pump.PumpOnceAsync();

        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x62, value));
        Assert.All(endpoint.Writes[2], value => Assert.Equal(0x73, value));
        Assert.Equal(0, buffer.Snapshot().FillBytes);
    }

    [Fact]
    public async Task ConcurrentPumpRequestsCannotReorderBlocks()
    {
        var pump = new SharedAudioPump();
        var buffer = PumpBuffer(0x01);
        var endpoint = new FakePipeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.BeginGroup(["receiver"]);
        pump.MarkReady("receiver");
        await pump.PumpOnceAsync();
        buffer.Write(Block(0x31));
        buffer.Write(Block(0x62));

        var timeout = TimeSpan.FromSeconds(5);
        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInvoked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstWrite = new ManualResetEventSlim();
        endpoint.BeforeWrite = pcm =>
        {
            if (pcm[0] != 0x31) return;
            firstWriteEntered.SetResult();
            if (!releaseFirstWrite.Wait(timeout)) throw new TimeoutException("First pump write was not released.");
        };

        var first = Task.Run(() => pump.PumpOnceAsync());
        var second = Task.CompletedTask;
        try
        {
            await firstWriteEntered.Task.WaitAsync(timeout);
            second = Task.Run(() =>
            {
                var iteration = pump.PumpOnceAsync();
                secondInvoked.SetResult(iteration);
                return iteration;
            });
            var secondIteration = await secondInvoked.Task.WaitAsync(timeout);

            Assert.False(secondIteration.IsCompleted, "The second iteration must wait until the first write finishes.");
        }
        finally
        {
            releaseFirstWrite.Set();
            await Task.WhenAll(first, second).WaitAsync(timeout);
        }

        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x31, value));
        Assert.All(endpoint.Writes[2], value => Assert.Equal(0x62, value));
    }

    [Fact]
    public async Task ClockSleepsWithoutLegsAndWakesForLegsOrCalibration()
    {
        var ticks = 0;
        await using var pump = new SharedAudioPump(advanceCalibration: () => Interlocked.Increment(ref ticks));
        pump.Start();
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref ticks));

        var endpoint = new FakePipeEndpoint("leg");
        pump.AddLeg("leg", PumpBuffer(0x10), endpoint, 0);
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 3);

        pump.RemoveLeg("leg");
        await Task.Delay(100);
        var afterRemoval = Volatile.Read(ref ticks);
        await Task.Delay(200);
        Assert.Equal(afterRemoval, Volatile.Read(ref ticks));
        Assert.False(pump.HasWork);

        pump.SetCalibrationMode(true);
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= afterRemoval + 3);
        pump.SetCalibrationMode(false);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the shared pump clock.");
            await Task.Delay(10);
        }
    }
}
