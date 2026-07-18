using System.Diagnostics;
using AirBridge.Core;

namespace AirBridge.App;

/// <summary>
/// Broadcasts each normalized PCM write to independent bounded receiver queues.
/// A stalled receiver can overrun only its own queue and never backpressures capture.
/// </summary>
public sealed class PcmBroadcastHub : IPcmSink
{
    private readonly object _gate = new();
    private readonly object _calibrationGate = new();
    private readonly Dictionary<string, BoundedPcmBuffer> _subscriptions = new(StringComparer.Ordinal);
    private readonly BoundedPcmBuffer _idle = new(176400 * 5, 500);
    private readonly BoundedPcmBuffer _monitor = new(176400 * 5, 500);
    private CalibrationRun? _calibration;

    private sealed class CalibrationRun(ChirpSequence sequence)
    {
        public ChirpSequence Sequence { get; } = sequence;
        public int Offset { get; set; }
        public int NextOnset { get; set; }
        public List<long> EmissionTicks { get; } = [];
        public TaskCompletionSource<IReadOnlyList<long>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public BoundedPcmBuffer MonitorBuffer => _monitor;

    public BoundedPcmBuffer Subscribe(string receiverId, int targetMilliseconds = 500)
    {
        lock (_gate)
        {
            if (_subscriptions.ContainsKey(receiverId)) throw new InvalidOperationException("Receiver already has a PCM subscription.");
            var buffer = new BoundedPcmBuffer(176400 * 5, targetMilliseconds);
            _subscriptions.Add(receiverId, buffer);
            return buffer;
        }
    }

    public void Unsubscribe(string receiverId)
    {
        lock (_gate) _subscriptions.Remove(receiverId);
    }

    public void Write(ReadOnlySpan<byte> source, bool producerActive = true)
    {
        lock (_calibrationGate)
        {
            // While calibrating, the shared pump supplies exactly one synthetic
            // capture block per clock tick. Dropping concurrent capture packets
            // prevents double-rate fanout and keeps chirp timing deterministic.
            if (_calibration is not null) return;
            WriteToTargets(source, producerActive);
        }
    }

    /// <summary>
    /// Mixes one in-memory chirp sequence into the normalized capture fanout.
    /// Emission timestamps are taken as samples enter the same rings and shared
    /// pump used by ordinary program audio.
    /// </summary>
    public async Task<IReadOnlyList<long>> PlayCalibrationAsync(ChirpSequence sequence, CancellationToken cancellationToken = default)
    {
        var run = new CalibrationRun(sequence);
        lock (_calibrationGate)
        {
            if (_calibration is not null) throw new InvalidOperationException("A delay measurement is already running.");
            _calibration = run;
        }
        try
        {
            return await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_calibrationGate) if (ReferenceEquals(_calibration, run)) _calibration = null;
            throw;
        }
    }

    public void AdvanceCalibration(int blockBytes)
    {
        if (blockBytes <= 0) throw new ArgumentOutOfRangeException(nameof(blockBytes));
        lock (_calibrationGate)
        {
            var run = _calibration;
            if (run is null) return;
            var block = new byte[blockBytes];
            var available = Math.Min(block.Length, run.Sequence.Pcm.Length - run.Offset);
            run.Sequence.Pcm.AsSpan(run.Offset, available).CopyTo(block);
            var blockStartTicks = Stopwatch.GetTimestamp();
            var blockEnd = run.Offset + available;
            while (run.NextOnset < run.Sequence.OnsetByteOffsets.Count && run.Sequence.OnsetByteOffsets[run.NextOnset] < blockEnd)
            {
                var onset = run.Sequence.OnsetByteOffsets[run.NextOnset];
                if (onset >= run.Offset)
                {
                    var byteOffset = onset - run.Offset;
                    var ticksFromBlockStart = (long)Math.Round(
                        byteOffset / (double)(SharedAudioPump.SampleRate * SharedAudioPump.Channels * SharedAudioPump.BytesPerSample) * Stopwatch.Frequency,
                        MidpointRounding.AwayFromZero);
                    run.EmissionTicks.Add(blockStartTicks + ticksFromBlockStart);
                }
                run.NextOnset++;
            }
            WriteToTargets(block, producerActive: true);
            run.Offset += available;
            if (run.Offset < run.Sequence.Pcm.Length) return;
            _calibration = null;
            run.Completion.TrySetResult(run.EmissionTicks);
        }
    }

    private void WriteToTargets(ReadOnlySpan<byte> source, bool producerActive)
    {
        BoundedPcmBuffer[] targets;
        lock (_gate) targets = _subscriptions.Values.ToArray();
        _monitor.Write(source, producerActive);
        foreach (var target in targets) target.Write(source, producerActive);
    }

    public BufferSnapshot Snapshot()
    {
        BufferSnapshot[] values;
        lock (_gate) values = _subscriptions.Values.Select(item => item.Snapshot()).ToArray();
        if (values.Length == 0) return _idle.Snapshot();
        return new(
            values.Min(item => item.CapacityBytes),
            values.Min(item => item.FillBytes),
            values.Max(item => item.TargetMilliseconds),
            values.Min(item => item.BytesWritten),
            values.Min(item => item.BytesRead),
            values.Sum(item => item.Overruns),
            values.Sum(item => item.Underruns),
            values.Max(item => item.Epoch),
            values.Sum(item => item.ProducerIdlePaddingBytes),
            values.Sum(item => item.StarvedWhileActivePaddingBytes));
    }

    public void Clear()
    {
        BoundedPcmBuffer[] targets;
        lock (_gate) targets = _subscriptions.Values.ToArray();
        foreach (var target in targets) target.Clear();
        _monitor.Clear();
    }

    public void SetTarget(int milliseconds)
    {
        BoundedPcmBuffer[] targets;
        lock (_gate) targets = _subscriptions.Values.ToArray();
        foreach (var target in targets) target.SetTarget(milliseconds);
    }

    public IReadOnlyDictionary<string, BufferSnapshot> ReceiverSnapshots()
    {
        lock (_gate) return _subscriptions.ToDictionary(item => item.Key, item => item.Value.Snapshot(), StringComparer.Ordinal);
    }
}
