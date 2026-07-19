using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class SharedAudioPumpTests
{
    private sealed class FakeEndpoint(string name) : IAudioPipeEndpoint
    {
        public string PipeName { get; } = name;
        public bool IsConnected { get; set; } = true;
        public bool CanAcceptWrite { get; set; } = true;
        public List<byte[]> Writes { get; } = [];
        public bool AcceptWrites { get; set; } = true;
        public bool TryWrite(byte[] pcm, bool tolerateBackpressure = false)
        {
            if (!AcceptWrites) return false;
            Writes.Add(pcm.ToArray());
            return true;
        }
    }

    [Fact]
    public async Task GroupGateKeepsQueuesIntactAndReleasesSameFirstLiveBlock()
    {
        var pump = new SharedAudioPump();
        var firstBuffer = BufferWithBlocks(0x31, 2);
        var secondBuffer = BufferWithBlocks(0x31, 2);
        var first = new FakeEndpoint("first");
        var second = new FakeEndpoint("second");
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
        var firstBuffer = BufferWithBlocks(0x41, 1);
        var secondBuffer = BufferWithBlocks(0x41, 1);
        var first = new FakeEndpoint("first");
        var second = new FakeEndpoint("second") { CanAcceptWrite = false };
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
        var buffer = BufferWithBlocks(0x55, 2);
        var endpoint = new FakeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 10);
        pump.AddLeg("sibling", BufferWithBlocks(0x66, 2), new FakeEndpoint("sibling"), 0);
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
        var buffer = BufferWithBlocks(0x6A, 1);
        var endpoint = new FakeEndpoint("solo");
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
        var buffer = BufferWithBlocks(0x5A, 3);
        var endpoint = new FakeEndpoint("first");
        pump.AddLeg("first", buffer, endpoint, 10);
        pump.BeginGroup(["first"]);
        pump.MarkReady("first");
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[0], value => Assert.Equal(0x5A, value));

        pump.AddLeg("second", BufferWithBlocks(0x33, 3), new FakeEndpoint("second"), 0);
        buffer.Write(Enumerable.Repeat((byte)0x5A, SharedAudioPump.BlockBytes).ToArray());
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x5A, value));

        buffer.Write(Enumerable.Repeat((byte)0x5A, SharedAudioPump.BlockBytes).ToArray());
        pump.MarkReady("second");
        await pump.PumpOnceAsync();
        var silenceBytes = ReceiverAlignmentPlan.ToPcmByteCount(10);
        Assert.All(endpoint.Writes[2][..silenceBytes], value => Assert.Equal(0, value));
        Assert.All(endpoint.Writes[2][silenceBytes..], value => Assert.Equal(0x5A, value));

        pump.RemoveLeg("second");
        buffer.Clear();
        buffer.Write(Enumerable.Repeat((byte)0x7C, SharedAudioPump.BlockBytes * 2).ToArray());
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[3], value => Assert.Equal(0x7C, value));
        Assert.Equal(10, pump.GetAlignmentTrim("first"));
    }

    [Fact]
    public async Task TimeoutReleasesReadyLegAndLateLegJoinsAtLiveEdge()
    {
        var now = DateTimeOffset.UtcNow;
        var pump = new SharedAudioPump(utcNow: () => now);
        var readyBuffer = BufferWithBlocks(0x11, 2);
        var lateBuffer = BufferWithBlocks(0x22, 2);
        var ready = new FakeEndpoint("ready");
        var late = new FakeEndpoint("late");
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

        lateBuffer.Write(Enumerable.Repeat((byte)0x44, SharedAudioPump.BlockBytes).ToArray());
        pump.MarkReady("late");
        lateBuffer.Write(Enumerable.Repeat((byte)0x66, SharedAudioPump.BlockBytes).ToArray());
        await pump.PumpOnceAsync();
        Assert.All(late.Writes[1], value => Assert.Equal(0x44, value));
    }

    [Fact]
    public async Task LiveTenMillisecondNudgeInsertsThenDropsExactly441StereoFrames()
    {
        var pump = new SharedAudioPump();
        var buffer = new BoundedPcmBuffer(SharedAudioPump.BlockBytes * 8);
        var endpoint = new FakeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.AddLeg("sibling", BufferWithBlocks(0x30, 8), new FakeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        buffer.Write(Enumerable.Repeat((byte)0x10, SharedAudioPump.BlockBytes * 2).ToArray());
        await pump.PumpOnceAsync();

        buffer.Write(Enumerable.Repeat((byte)0x10, SharedAudioPump.BlockBytes).ToArray());
        pump.SetAlignmentTrim("receiver", 10);
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[1][..1764], value => Assert.Equal(0, value));
        Assert.All(endpoint.Writes[1][1764..], value => Assert.Equal(0x10, value));

        buffer.Write(Enumerable.Repeat((byte)0x20, SharedAudioPump.BlockBytes).ToArray());
        pump.SetAlignmentTrim("receiver", 0);
        await pump.PumpOnceAsync();
        Assert.All(endpoint.Writes[2], value => Assert.Equal(0x20, value));
    }

    [Fact]
    public async Task StandbyDiscardsHistoryAndResumeReappliesGateAndTrimAtLiveEdge()
    {
        var pump = new SharedAudioPump();
        var buffer = new BoundedPcmBuffer(SharedAudioPump.BlockBytes * 8);
        var endpoint = new FakeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 10);
        pump.AddLeg("sibling", BufferWithBlocks(0x20, 8), new FakeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        buffer.Write(Enumerable.Repeat((byte)0x10, SharedAudioPump.BlockBytes).ToArray());
        await pump.PumpOnceAsync();

        pump.SetStandby(true);
        buffer.Write(Enumerable.Repeat((byte)0x33, SharedAudioPump.BlockBytes * 2).ToArray());
        await pump.PumpOnceAsync();
        Assert.Equal(0, buffer.Snapshot().FillBytes);
        Assert.Single(endpoint.Writes);

        pump.BeginGroup(["receiver"], discardBufferedUntilGateOpen: true);
        buffer.Write(Enumerable.Repeat((byte)0x44, SharedAudioPump.BlockBytes).ToArray());
        buffer.Write(Enumerable.Repeat((byte)0x77, SharedAudioPump.BlockBytes).ToArray());
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
        var stalled = new FakeEndpoint("stalled") { AcceptWrites = false };
        var healthy = new FakeEndpoint("healthy");
        pump.AddLeg("stalled", BufferWithBlocks(0x21, 1), stalled, 0);
        pump.AddLeg("healthy", BufferWithBlocks(0x42, 1), healthy, 0);
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
        var first = new FakeEndpoint("first");
        var added = new FakeEndpoint("added");
        pump.AddLeg("first", BufferWithBlocks(0x12, 1), first, 0);
        pump.BeginGroup(["first"]);
        pump.AddLeg("added", BufferWithBlocks(0x34, 1), added, 0);
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
        var buffer = BufferWithBlocks(0x10, 1);
        var endpoint = new FakeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.AddLeg("sibling", BufferWithBlocks(0x20, 4), new FakeEndpoint("sibling"), 0);
        pump.BeginGroup(["receiver", "sibling"]);
        pump.MarkReady("receiver");
        pump.MarkReady("sibling");
        await pump.PumpOnceAsync();

        pump.MarkNotReady("receiver");
        buffer.Write(Enumerable.Repeat((byte)0x22, SharedAudioPump.BlockBytes * 2).ToArray());
        buffer.Write(Enumerable.Repeat((byte)0x77, SharedAudioPump.BlockBytes).ToArray());
        pump.MarkReady("receiver");
        await pump.PumpOnceAsync();

        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x77, value));
    }

    [Fact]
    public async Task OppositePendingNudgesCoalesceToNoAudioMutation()
    {
        var pump = new SharedAudioPump();
        var buffer = BufferWithBlocks(0x51, 2);
        var endpoint = new FakeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.BeginGroup(["receiver"]);
        pump.MarkReady("receiver");
        await pump.PumpOnceAsync();

        buffer.Write(Enumerable.Repeat((byte)0x51, SharedAudioPump.BlockBytes).ToArray());
        pump.SetAlignmentTrim("receiver", 500);
        pump.SetAlignmentTrim("receiver", 0);
        await pump.PumpOnceAsync();

        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x51, value));
    }

    [Fact]
    public async Task ConcurrentPumpRequestsCannotReorderBlocks()
    {
        var pump = new SharedAudioPump();
        var buffer = BufferWithBlocks(0x01, 1);
        var endpoint = new FakeEndpoint("receiver");
        pump.AddLeg("receiver", buffer, endpoint, 0);
        pump.BeginGroup(["receiver"]);
        pump.MarkReady("receiver");
        await pump.PumpOnceAsync();
        buffer.Write(Enumerable.Repeat((byte)0x31, SharedAudioPump.BlockBytes).ToArray());
        buffer.Write(Enumerable.Repeat((byte)0x62, SharedAudioPump.BlockBytes).ToArray());

        await Task.WhenAll(pump.PumpOnceAsync(), pump.PumpOnceAsync());

        Assert.All(endpoint.Writes[1], value => Assert.Equal(0x31, value));
        Assert.All(endpoint.Writes[2], value => Assert.Equal(0x62, value));
    }

    private static BoundedPcmBuffer BufferWithBlocks(byte value, int count)
    {
        var buffer = new BoundedPcmBuffer(SharedAudioPump.BlockBytes * 8);
        buffer.Write(Enumerable.Repeat(value, SharedAudioPump.BlockBytes * count).ToArray());
        return buffer;
    }
}
