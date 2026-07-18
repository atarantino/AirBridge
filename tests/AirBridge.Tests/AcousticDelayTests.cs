using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class AcousticDelayTests
{
    [Fact]
    public void DetectorIgnoresLocalBleedAndReturnsMedianAcousticDelay()
    {
        const int sampleRate = 16000;
        var emissions = Enumerable.Range(0, 5).Select(index => 500 + index * CalibrationChirp.IntervalMilliseconds).ToArray();
        var expectedDelays = new[] { 2070, 2090, 2110, 2080, 2100 };
        var samples = new short[sampleRate * 7];
        var chirp = CalibrationChirp.Generate(sampleRate);
        for (var index = 0; index < emissions.Length; index++)
        {
            Mix(samples, chirp, (emissions[index] + 60) * sampleRate / 1000, 0.7); // local monitor bleed
            Mix(samples, chirp, (emissions[index] + expectedDelays[index]) * sampleRate / 1000, 0.45);
        }

        var result = AcousticDelayDetector.Detect(samples, sampleRate, emissions);

        Assert.InRange(result.MedianMilliseconds, 2070, 2110);
        Assert.Equal(5, result.DelaysMilliseconds.Count);
    }

    [Fact]
    public void CalibrationSequenceContainsFiveBoundedInMemoryOnsets()
    {
        var sequence = CalibrationChirp.CreateSequence();

        Assert.Equal(5, sequence.OnsetByteOffsets.Count);
        Assert.True(sequence.Pcm.Length < 44100 * 2 * 2 * 4);
        Assert.All(sequence.OnsetByteOffsets.Zip(sequence.OnsetByteOffsets.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    private static void Mix(short[] destination, short[] source, int offset, double gain)
    {
        for (var index = 0; index < source.Length && offset + index < destination.Length; index++)
            destination[offset + index] = (short)Math.Clamp(destination[offset + index] + source[index] * gain, short.MinValue, short.MaxValue);
    }
}
