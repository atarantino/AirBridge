namespace AirBridge.Core;

/// <summary>A fixed-size, thread-safe byte ring. Writes never allocate and overrun drops the oldest live audio.</summary>
public sealed class BoundedPcmBuffer : IPcmSink
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _read;
    private int _write;
    private int _count;
    private long _written;
    private long _readTotal;
    private long _overruns;
    private long _underruns;
    private long _epoch;
    private long _producerIdlePaddingBytes;
    private long _starvedWhileActivePaddingBytes;
    private bool _producerActive;

    public BoundedPcmBuffer(int capacityBytes, int targetMilliseconds = 500)
    {
        if (capacityBytes < 4) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        _buffer = new byte[capacityBytes];
        TargetMilliseconds = targetMilliseconds;
    }

    public int Capacity => _buffer.Length;
    public int TargetMilliseconds { get; private set; }

    public void SetTarget(int milliseconds)
    {
        if (milliseconds is < 100 or > 5000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        TargetMilliseconds = milliseconds;
    }

    public void Write(ReadOnlySpan<byte> source, bool producerActive = true)
    {
        lock (_gate)
        {
            _producerActive = producerActive;
            if (source.Length >= _buffer.Length)
            {
                source = source[^_buffer.Length..];
                _read = 0;
                _write = 0;
                _count = 0;
                _overruns++;
                _epoch++;
            }

            var overflow = Math.Max(0, _count + source.Length - _buffer.Length);
            if (overflow > 0)
            {
                _read = (_read + overflow) % _buffer.Length;
                _count -= overflow;
                _overruns++;
                _epoch++;
            }

            var first = Math.Min(source.Length, _buffer.Length - _write);
            source[..first].CopyTo(_buffer.AsSpan(_write));
            source[first..].CopyTo(_buffer);
            _write = (_write + source.Length) % _buffer.Length;
            _count += source.Length;
            _written += source.Length;
        }
    }

    public int Read(Span<byte> destination, bool padWithSilence) =>
        Read(destination, padWithSilence, out _, out _);

    public int Read(Span<byte> destination, bool padWithSilence, out int paddedBytes, out bool producerActive)
    {
        lock (_gate)
        {
            paddedBytes = 0;
            producerActive = _producerActive;
            var available = Math.Min(destination.Length, _count);
            var first = Math.Min(available, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(destination);
            _buffer.AsSpan(0, available - first).CopyTo(destination[first..]);
            _read = (_read + available) % _buffer.Length;
            _count -= available;
            _readTotal += available;
            if (available < destination.Length && padWithSilence)
            {
                destination[available..].Clear();
                _underruns++;
                var padding = destination.Length - available;
                paddedBytes = padding;
                if (_producerActive) _starvedWhileActivePaddingBytes += padding;
                else _producerIdlePaddingBytes += padding;
                return destination.Length;
            }
            return available;
        }
    }

    public void Clear(bool advanceEpoch = true)
    {
        lock (_gate)
        {
            _read = _write = _count = 0;
            _producerActive = false;
            if (advanceEpoch) _epoch++;
        }
    }

    /// <summary>Drops queued history while retaining at most the newest requested bytes.</summary>
    public void DiscardToLiveEdge(int maximumBytesToKeep)
    {
        if (maximumBytesToKeep < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytesToKeep));
        lock (_gate)
        {
            if (_count <= maximumBytesToKeep) return;
            var discard = _count - maximumBytesToKeep;
            _read = (_read + discard) % _buffer.Length;
            _count = maximumBytesToKeep;
            _epoch++;
        }
    }

    public BufferSnapshot Snapshot()
    {
        lock (_gate) return new(_buffer.Length, _count, TargetMilliseconds, _written, _readTotal, _overruns, _underruns, _epoch, _producerIdlePaddingBytes, _starvedWhileActivePaddingBytes);
    }
}

public interface IPcmSink
{
    void Write(ReadOnlySpan<byte> source, bool producerActive = true);
}
