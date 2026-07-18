namespace AirBridge.Core;

public sealed record ChirpSequence(byte[] Pcm, IReadOnlyList<int> OnsetByteOffsets, int SampleRate, int Channels);

public sealed record AcousticDelayResult(
    int MedianMilliseconds,
    IReadOnlyList<int> DelaysMilliseconds,
    IReadOnlyList<int> DetectedOnsetsMilliseconds,
    string ReceiverId,
    string ReceiverName);

public static class CalibrationChirp
{
    public const int DurationMilliseconds = 90;
    public const int IntervalMilliseconds = 800;
    private static readonly double[] Frequencies = [900, 1400, 2100];

    public static short[] Generate(int sampleRate)
    {
        var segmentSamples = sampleRate * (DurationMilliseconds / Frequencies.Length) / 1000;
        var values = new short[segmentSamples * Frequencies.Length];
        const double amplitude = 0.22 * short.MaxValue;
        var rampSamples = Math.Max(1, sampleRate * 3 / 1000);
        for (var segment = 0; segment < Frequencies.Length; segment++)
        {
            for (var index = 0; index < segmentSamples; index++)
            {
                var edge = Math.Min(index + 1, segmentSamples - index);
                var envelope = Math.Min(1.0, edge / (double)rampSamples);
                var phase = 2 * Math.PI * Frequencies[segment] * index / sampleRate;
                values[segment * segmentSamples + index] = (short)Math.Round(amplitude * envelope * Math.Sin(phase));
            }
        }
        return values;
    }

    public static ChirpSequence CreateSequence(int sampleRate = 44100, int channels = 2, int count = 5)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var chirp = Generate(sampleRate);
        var intervalFrames = sampleRate * IntervalMilliseconds / 1000;
        var totalFrames = (count - 1) * intervalFrames + chirp.Length;
        var pcm = new byte[totalFrames * channels * sizeof(short)];
        var onsets = new int[count];
        for (var chirpIndex = 0; chirpIndex < count; chirpIndex++)
        {
            var frameOffset = chirpIndex * intervalFrames;
            onsets[chirpIndex] = frameOffset * channels * sizeof(short);
            for (var sample = 0; sample < chirp.Length; sample++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var byteOffset = (frameOffset + sample) * channels * sizeof(short) + channel * sizeof(short);
                    BitConverter.TryWriteBytes(pcm.AsSpan(byteOffset, sizeof(short)), chirp[sample]);
                }
            }
        }
        return new(pcm, onsets, sampleRate, channels);
    }
}

public static class AcousticDelayDetector
{
    private static readonly double[] Frequencies = [900, 1400, 2100];

    public static (int MedianMilliseconds, IReadOnlyList<int> DelaysMilliseconds, IReadOnlyList<int> OnsetsMilliseconds) Detect(
        ReadOnlySpan<short> samples,
        int sampleRate,
        IReadOnlyList<int> emissionMilliseconds,
        int minimumDelayMilliseconds = 300,
        int maximumDelayMilliseconds = 5000)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (emissionMilliseconds.Count == 0) throw new ArgumentException("At least one emission timestamp is required.", nameof(emissionMilliseconds));
        var onsets = FindOnsets(samples, sampleRate);
        var hypotheses = emissionMilliseconds
            .SelectMany(emission => onsets.Select(onset => onset - emission))
            .Where(delay => delay >= minimumDelayMilliseconds && delay <= maximumDelayMilliseconds)
            .Distinct()
            .ToArray();
        List<int>? best = null;
        foreach (var hypothesis in hypotheses)
        {
            var candidate = new List<int>();
            var usedOnsets = new HashSet<int>();
            foreach (var emission in emissionMilliseconds)
            {
                var match = onsets
                    .Where(onset => !usedOnsets.Contains(onset))
                    .Select(onset => new { Onset = onset, Delay = onset - emission })
                    .Where(item => item.Delay >= minimumDelayMilliseconds && item.Delay <= maximumDelayMilliseconds && Math.Abs(item.Delay - hypothesis) <= 80)
                    .OrderBy(item => Math.Abs(item.Delay - hypothesis))
                    .FirstOrDefault();
                if (match is null) continue;
                usedOnsets.Add(match.Onset);
                candidate.Add(match.Delay);
            }
            if (best is null || candidate.Count > best.Count ||
                (candidate.Count == best.Count && Spread(candidate) < Spread(best))) best = candidate;
        }
        var delays = best ?? [];
        if (delays.Count < Math.Min(3, emissionMilliseconds.Count))
            throw new InvalidOperationException($"Detected only {delays.Count} usable chirp returns; move the microphone closer to the selected speaker and retry.");
        var ordered = delays.Order().ToArray();
        var median = ordered.Length % 2 == 1 ? ordered[ordered.Length / 2] : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
        return (median, delays, onsets);
    }

    private static int Spread(IReadOnlyList<int> values) => values.Count == 0 ? int.MaxValue : values.Max() - values.Min();

    public static IReadOnlyList<int> FindOnsets(ReadOnlySpan<short> samples, int sampleRate)
    {
        var segmentSamples = sampleRate * (CalibrationChirp.DurationMilliseconds / Frequencies.Length) / 1000;
        var windowSamples = segmentSamples * Frequencies.Length;
        var stepSamples = Math.Max(1, sampleRate * 5 / 1000);
        var candidates = new List<(int Milliseconds, double Score)>();
        for (var start = 0; start + windowSamples <= samples.Length; start += stepSamples)
        {
            var score = 0.0;
            for (var segment = 0; segment < Frequencies.Length; segment++)
                score += ToneFraction(samples.Slice(start + segment * segmentSamples, segmentSamples), sampleRate, Frequencies[segment]);
            score /= Frequencies.Length;
            if (score < 0.32) continue;
            var milliseconds = (int)Math.Round(start * 1000.0 / sampleRate);
            if (candidates.Count > 0 && milliseconds - candidates[^1].Milliseconds < 250)
            {
                if (score > candidates[^1].Score) candidates[^1] = (milliseconds, score);
            }
            else candidates.Add((milliseconds, score));
        }
        return candidates.Select(item => item.Milliseconds).ToArray();
    }

    private static double ToneFraction(ReadOnlySpan<short> samples, int sampleRate, double frequency)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
        double previous = 0, previous2 = 0, energy = 0;
        foreach (var raw in samples)
        {
            var value = raw / (double)short.MaxValue;
            energy += value * value;
            var current = value + coefficient * previous - previous2;
            previous2 = previous;
            previous = current;
        }
        if (energy < 1e-8) return 0;
        var power = previous2 * previous2 + previous * previous - coefficient * previous * previous2;
        return Math.Clamp(2 * power / (samples.Length * energy), 0, 1);
    }
}
