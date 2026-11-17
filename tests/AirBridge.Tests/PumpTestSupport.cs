using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

internal sealed class FakePipeEndpoint(string name) : IAudioPipeEndpoint
{
    public string PipeName { get; } = name;
    public bool IsConnected => true;
    public bool CanAcceptWrite { get; set; } = true;
    public bool AcceptWrites { get; set; } = true;
    public int DropsRemaining { get; set; }
    public Action<byte[]>? BeforeWrite { get; set; }
    public List<byte[]> Writes { get; } = [];

    public AudioPipeWriteResult Write(ReadOnlySpan<byte> pcm)
    {
        if (!AcceptWrites) return AudioPipeWriteResult.Unavailable;
        if (DropsRemaining > 0)
        {
            DropsRemaining--;
            return AudioPipeWriteResult.Dropped;
        }
        var copy = pcm.ToArray();
        BeforeWrite?.Invoke(copy);
        Writes.Add(copy);
        return AudioPipeWriteResult.Accepted;
    }
}

internal static class PumpPcm
{
    public static byte[] Block(byte value, int count = 1) =>
        Enumerable.Repeat(value, SharedAudioPump.BlockBytes * count).ToArray();

    public static BoundedPcmBuffer PumpBuffer(byte value, int blocks = 1, int targetMilliseconds = 0, int capacityBlocks = 8)
    {
        var buffer = new BoundedPcmBuffer(SharedAudioPump.BlockBytes * capacityBlocks, targetMilliseconds);
        buffer.Write(Block(value, blocks));
        return buffer;
    }
}
