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
        var one = normalizer.ConvertFloat32(MemoryMarshal.AsBytes(packet.AsSpan())).Length;
        var two = normalizer.ConvertFloat32(MemoryMarshal.AsBytes(packet.AsSpan())).Length;
        Assert.InRange(one + two, 440 * 4 * 2 - 8, 442 * 4 * 2 + 8);
    }

    [Fact]
    public void UpsamplingInterpolatesAcrossEmptyAndSmallerPackets()
    {
        var normalizer = new PcmNormalizer(22050, 1);
        float[] first = [0f, 1f, 0f];
        var output = MemoryMarshal.Cast<byte, short>(normalizer.ConvertFloat32(MemoryMarshal.AsBytes(first.AsSpan()))).ToArray();
        Assert.Equal(new short[] { 0, 0, 16384, 16384, 32767, 32767, 16384, 16384 }, output);

        Assert.True(normalizer.ConvertFloat32([]).IsEmpty);
        float[] second = [-.5f];
        var next = MemoryMarshal.Cast<byte, short>(normalizer.ConvertFloat32(MemoryMarshal.AsBytes(second.AsSpan()))).ToArray();
        Assert.Equal(new short[] { 0, 0, -8192, -8192 }, next);
    }

    [Theory]
    [InlineData(22050, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 6)]
    [InlineData(96000, 8)]
    public void Int16AndFloatAgreeAcrossChangingPacketSizes(int rate, int channels)
    {
        var integers = new PcmNormalizer(rate, channels);
        var floats = new PcmNormalizer(rate, channels);
        var random = new Random(6127);
        foreach (var frames in new[] { 1, 960, 0, 13, 2048, 1, 480, 0, 127 })
        {
            var input = new short[frames * channels];
            for (var i = 0; i < input.Length; i++) input[i] = (short)random.Next(-32768, 32768);
            var floatInput = input.Select(value => value / 32768f).ToArray();
            Assert.Equal(
                floats.ConvertFloat32(MemoryMarshal.AsBytes(floatInput.AsSpan())).ToArray(),
                integers.ConvertInt16(MemoryMarshal.AsBytes(input.AsSpan())).ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyStateConversionDoesNotAllocate(bool int16)
    {
        long outputBytes = 0;
        long allocated = 0;
        Exception? failure = null;
        // Keep the test runner's worker-thread activity outside this synchronous measurement.
        var worker = new Thread(() =>
        {
            try
            {
                var normalizer = new PcmNormalizer(48000, 2);
                var input = new byte[960 * 2 * (int16 ? sizeof(short) : sizeof(float))];
                for (var i = 0; i < 100; i++) Convert();

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 100; i++) outputBytes += Convert().Length;
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                ReadOnlySpan<byte> Convert() => int16 ? normalizer.ConvertInt16(input) : normalizer.ConvertFloat32(input);
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "PCM allocation measurement did not finish.");
        Assert.Null(failure);

        Assert.InRange(outputBytes, 100 * 3520, 100 * 3536);
        // Each packet used to allocate its 3.5 KB result. Leave slack for runtime
        // bookkeeping on this thread, far below one packet per conversion.
        Assert.InRange(allocated, 0, 1024);
    }

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task WindowsLoopbackExposesSupportedNativeFormat()
    {
        var buffer = new BoundedPcmBuffer(176400);
        var coordinator = new StreamCoordinator(buffer);
        using var capture = new AirBridge.App.WasapiCaptureService(buffer, coordinator);
        await capture.StartSystemAsync(activationTimeout: TimeSpan.FromSeconds(3));
        // This assertion documents the common Windows mix format behind the regression.
        Assert.NotNull(capture.SourceFormat);
        Assert.True(capture.SourceFormat!.BitsPerSample is 16 or 32);
        capture.Stop();
    }
}
