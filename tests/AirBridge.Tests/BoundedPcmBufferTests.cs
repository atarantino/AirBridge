using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class BoundedPcmBufferTests
{
    [Fact]
    public void WraparoundPreservesOrder()
    {
        var buffer = new BoundedPcmBuffer(8);
        buffer.Write([1, 2, 3, 4, 5, 6]);
        var first = new byte[4];
        Assert.Equal(4, buffer.Read(first, false));
        buffer.Write([7, 8, 9, 10]);
        var rest = new byte[6];
        Assert.Equal(6, buffer.Read(rest, false));
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10 }, rest);
    }

    [Fact]
    public void OverrunDropsOldestAndAdvancesEpoch()
    {
        var buffer = new BoundedPcmBuffer(8);
        buffer.Write([1, 2, 3, 4, 5, 6]);
        buffer.Write([7, 8, 9, 10]);
        var result = new byte[8];
        buffer.Read(result, false);
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7, 8, 9, 10 }, result);
        Assert.Equal(1, buffer.Snapshot().Overruns);
        Assert.Equal(1, buffer.Snapshot().Epoch);
    }

    [Fact]
    public void UnderrunPadsWithSilence()
    {
        var buffer = new BoundedPcmBuffer(8);
        buffer.Write([1, 2]);
        var result = Enumerable.Repeat((byte)9, 4).ToArray();
        Assert.Equal(4, buffer.Read(result, true));
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, result);
        Assert.Equal(1, buffer.Snapshot().Underruns);
    }

    [Fact]
    public void SilencePaddingDistinguishesProducerIdleFromActiveStarvation()
    {
        var buffer = new BoundedPcmBuffer(16);
        var destination = new byte[8];

        buffer.Write([1, 2], producerActive: false);
        buffer.Read(destination, padWithSilence: true);
        var idle = buffer.Snapshot();
        Assert.Equal(6, idle.ProducerIdlePaddingBytes);
        Assert.Equal(0, idle.StarvedWhileActivePaddingBytes);

        buffer.Write([3, 4], producerActive: true);
        buffer.Read(destination, padWithSilence: true);
        var active = buffer.Snapshot();
        Assert.Equal(6, active.ProducerIdlePaddingBytes);
        Assert.Equal(6, active.StarvedWhileActivePaddingBytes);
        Assert.Equal(2, active.Underruns);
    }

    [Fact]
    public void SnapshotReportsFillPercent()
    {
        var buffer = new BoundedPcmBuffer(100);
        buffer.Write(new byte[25]);
        Assert.Equal(25, buffer.Snapshot().FillPercent);
    }

    [Fact]
    public void DiscardToLiveEdgeRetainsNewestBytesAcrossWraparound()
    {
        var buffer = new BoundedPcmBuffer(12);
        buffer.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        var consumed = new byte[4];
        buffer.Read(consumed, false);
        buffer.Write([11, 12, 13, 14]);

        buffer.DiscardToLiveEdge(4);

        var live = new byte[4];
        Assert.Equal(4, buffer.Read(live, false));
        Assert.Equal(new byte[] { 11, 12, 13, 14 }, live);
    }
}
