using System.Runtime.InteropServices;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class PcmNormalizerTests
{
    [Fact]
    public void FloatClipsAndConvertsToStereoInt16()
    {
        var normalizer = new PcmNormalizer(44100, 2);
        float[] input = [-2f, 2f, 0.5f, -0.5f];
        var bytes = MemoryMarshal.AsBytes(input.AsSpan()).ToArray();
        var output = normalizer.ConvertFloat32(bytes);
        var samples = MemoryMarshal.Cast<byte, short>(output);
        Assert.True(samples.Length >= 2);
        Assert.Equal(short.MinValue, samples[0]);
        Assert.Equal(short.MaxValue, samples[1]);
    }

    [Fact]
    public void MonoIsDuplicated()
    {
        var normalizer = new PcmNormalizer(44100, 1);
        short[] input = [1000, 2000, 3000];
        var output = normalizer.ConvertInt16(MemoryMarshal.AsBytes(input.AsSpan()));
        var samples = MemoryMarshal.Cast<byte, short>(output);
        Assert.Equal(samples[0], samples[1]);
    }

    [Fact]
    public void ResamplingPreservesStateAcrossPackets()
    {
        var normalizer = new PcmNormalizer(48000, 2);
        var packet = new float[480 * 2];
        Array.Fill(packet, 0.25f);
        var one = normalizer.ConvertFloat32(MemoryMarshal.AsBytes(packet.AsSpan()));
        var two = normalizer.ConvertFloat32(MemoryMarshal.AsBytes(packet.AsSpan()));
        Assert.InRange(one.Length + two.Length, 440 * 4 * 2 - 8, 442 * 4 * 2 + 8);
    }

    [Fact]
    public async Task WindowsLoopbackAcceptsItsNativeExtensibleFormatWhenAudioArrives()
    {
        if (!HardwareTestGate.Enabled) return;
        var buffer = new BoundedPcmBuffer(176400);
        var coordinator = new StreamCoordinator(buffer);
        using var capture = new AirBridge.App.WasapiCaptureService(buffer, coordinator);
        try
        {
            await capture.StartSystemAsync(activationTimeout: TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            return;
        }
        // This assertion documents the common Windows mix format behind the regression.
        Assert.NotNull(capture.SourceFormat);
        Assert.True(capture.SourceFormat!.BitsPerSample is 16 or 32);
        capture.Stop();
    }
}
