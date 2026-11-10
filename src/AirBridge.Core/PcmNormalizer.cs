namespace AirBridge.Core;

/// <summary>Stateful streaming conversion to 44.1 kHz, signed 16-bit little-endian stereo PCM.</summary>
public sealed class PcmNormalizer
{
    private readonly int _inputRate;
    private readonly int _channels;
    private double _sourcePosition;
    private bool _hasPreviousFrame;
    private float _previousLeft;
    private float _previousRight;
    // Capture uses one normalizer serially, so every buffer, including the
    // returned PCM, is reused across packets instead of allocated per packet.
    private float[] _inputSamples = [];
    private float[] _stereoSamples = [];
    private byte[] _output = [];

    public PcmNormalizer(int inputRate, int channels)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _inputRate = inputRate;
        _channels = channels;
    }

    /// <summary>Converts one packet. The result is valid only until the next conversion on this instance.</summary>
    public ReadOnlySpan<byte> ConvertFloat32(ReadOnlySpan<byte> input) => ConvertSamples(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(input));

    /// <summary>Converts one packet. The result is valid only until the next conversion on this instance.</summary>
    public ReadOnlySpan<byte> ConvertInt16(ReadOnlySpan<byte> input)
    {
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(input);
        var samples = Scratch(ref _inputSamples, values.Length);
        for (var i = 0; i < values.Length; i++) samples[i] = values[i] / 32768f;
        return ConvertSamples(samples);
    }

    private ReadOnlySpan<byte> ConvertSamples(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        if (frames == 0) return [];
        var withPrevious = Scratch(ref _stereoSamples, (frames + (_hasPreviousFrame ? 1 : 0)) * 2);
        var offset = 0;
        if (_hasPreviousFrame)
        {
            withPrevious[0] = _previousLeft;
            withPrevious[1] = _previousRight;
            offset = 1;
        }
        for (var frame = 0; frame < frames; frame++)
        {
            (withPrevious[(frame + offset) * 2], withPrevious[(frame + offset) * 2 + 1]) = Downmix(samples, frame);
        }
        _previousLeft = withPrevious[^2];
        _previousRight = withPrevious[^1];
        _hasPreviousFrame = true;

        var totalFrames = frames + offset;
        var step = _inputRate / 44100d;
        var outputBytes = Scratch(ref _output, (int)(frames / step + 2) * 2 * sizeof(short));
        var output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(outputBytes);
        var outputCount = 0;
        while (_sourcePosition + 1 < totalFrames)
        {
            var index = (int)_sourcePosition;
            var fraction = (float)(_sourcePosition - index);
            for (var channel = 0; channel < 2; channel++)
            {
                var a = withPrevious[index * 2 + channel];
                var b = withPrevious[(index + 1) * 2 + channel];
                output[outputCount++] = ToInt16(a + ((b - a) * fraction));
            }
            _sourcePosition += step;
        }
        _sourcePosition -= totalFrames - 1;
        return outputBytes[..(outputCount * sizeof(short))];
    }

    private static Span<T> Scratch<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new T[length];
        return buffer.AsSpan(0, length);
    }

    private (float Left, float Right) Downmix(ReadOnlySpan<float> samples, int frame)
    {
        var start = frame * _channels;
        if (_channels == 1) return (samples[start], samples[start]);
        if (_channels == 2) return (samples[start], samples[start + 1]);
        // ITU-style center/surround attenuation with conservative normalization.
        var left = samples[start];
        var right = samples[start + 1];
        if (_channels > 2) { left += samples[start + 2] * .707f; right += samples[start + 2] * .707f; }
        if (_channels > 4) left += samples[start + 4] * .5f;
        if (_channels > 5) right += samples[start + 5] * .5f;
        return (left / 2f, right / 2f);
    }

    private static short ToInt16(float value) => (short)Math.Round(Math.Clamp(value, -1f, 1f) * (value < 0 ? 32768f : 32767f));
}
