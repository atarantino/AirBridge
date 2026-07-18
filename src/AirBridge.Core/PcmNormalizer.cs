namespace AirBridge.Core;

/// <summary>Stateful streaming conversion to 44.1 kHz, signed 16-bit little-endian stereo PCM.</summary>
public sealed class PcmNormalizer
{
    private readonly int _inputRate;
    private readonly int _channels;
    private double _sourcePosition;
    private float[]? _previousFrame;

    public PcmNormalizer(int inputRate, int channels)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _inputRate = inputRate;
        _channels = channels;
    }

    public byte[] ConvertFloat32(ReadOnlySpan<byte> input) => ConvertSamples(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(input));

    public byte[] ConvertInt16(ReadOnlySpan<byte> input)
    {
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(input);
        var samples = new float[values.Length];
        for (var i = 0; i < values.Length; i++) samples[i] = values[i] / 32768f;
        return ConvertSamples(samples);
    }

    private byte[] ConvertSamples(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / _channels;
        if (frames == 0) return [];
        var withPrevious = new float[(frames + (_previousFrame is null ? 0 : 1)) * 2];
        var offset = 0;
        if (_previousFrame is not null)
        {
            withPrevious[0] = _previousFrame[0];
            withPrevious[1] = _previousFrame[1];
            offset = 1;
        }
        for (var frame = 0; frame < frames; frame++)
        {
            (withPrevious[(frame + offset) * 2], withPrevious[(frame + offset) * 2 + 1]) = Downmix(samples, frame);
        }
        _previousFrame = [withPrevious[^2], withPrevious[^1]];

        var totalFrames = frames + offset;
        var step = _inputRate / 44100d;
        var output = new List<short>((int)(frames / step + 2) * 2);
        while (_sourcePosition + 1 < totalFrames)
        {
            var index = (int)_sourcePosition;
            var fraction = (float)(_sourcePosition - index);
            for (var channel = 0; channel < 2; channel++)
            {
                var a = withPrevious[index * 2 + channel];
                var b = withPrevious[(index + 1) * 2 + channel];
                output.Add(ToInt16(a + ((b - a) * fraction)));
            }
            _sourcePosition += step;
        }
        _sourcePosition -= totalFrames - 1;
        var bytes = new byte[output.Count * sizeof(short)];
        output.CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes));
        return bytes;
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

