using AirBridge.Core;

namespace AirBridge.App;

public interface IAudioPipeEndpoint
{
    string PipeName { get; }
    bool IsConnected { get; }
    bool CanAcceptWrite { get; }
    AudioPipeWriteResult Write(byte[] pcm, bool tolerateBackpressure = false);
}

public enum AudioPipeWriteResult { Accepted, Dropped, Unavailable }

public sealed record GroupGateTimeoutEventArgs(IReadOnlyList<string> ReceiverIds);

/// <summary>
/// Owns the one 20 ms clock used by every active RAOP leg. Until the group gate
/// opens, pipes receive silence and their receiver queues are not consumed.
/// </summary>
public sealed class SharedAudioPump : IAsyncDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int BytesPerSample = 2;
    public const int BlockMilliseconds = 20;
    public const int BlockBytes = SampleRate * Channels * BytesPerSample * BlockMilliseconds / 1000;
    public const int CorrectionDebtResyncMilliseconds = 250;

    private sealed class Leg(string receiverId, BoundedPcmBuffer buffer, IAudioPipeEndpoint endpoint)
    {
        public string ReceiverId { get; } = receiverId;
        public BoundedPcmBuffer Buffer { get; } = buffer;
        public IAudioPipeEndpoint Endpoint { get; } = endpoint;
        public bool Ready { get; set; }
        public bool ParticipatesInGate { get; set; }
        public bool Live { get; set; }
        public int ConfiguredAlignmentTrimMs { get; set; }
        public int AppliedAlignmentTrimMs { get; set; }
        public int PendingInsertBytes { get; set; }
        public int PendingDropBytes { get; set; }
        public int CorrectionDebtBytes { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Leg> _legs = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly BoundedPcmBuffer? _monitorBuffer;
    private readonly Action? _advanceCalibration;
    private readonly SemaphoreSlim _iterationGate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _pumpTask;
    private HashSet<string> _gateReceiverIds = new(StringComparer.Ordinal);
    private DateTimeOffset? _gateDeadlineUtc;
    private bool _groupGateOpen = true;
    private bool _standby;
    private bool _calibrationMode;
    private bool _discardBufferedUntilGateOpen;
    private int _groupResyncCount;

    public SharedAudioPump(BoundedPcmBuffer? monitorBuffer = null, Func<DateTimeOffset>? utcNow = null, Action? advanceCalibration = null)
    {
        _monitorBuffer = monitorBuffer;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _advanceCalibration = advanceCalibration;
    }

    public event EventHandler<GroupGateTimeoutEventArgs>? GateTimedOut;
    public event EventHandler<bool>? CaptureActivityObserved;
    internal int GroupResyncCount { get { lock (_gate) return _groupResyncCount; } }

    public void Start()
    {
        if (_pumpTask is not null) return;
        _cancellation = new CancellationTokenSource();
        _pumpTask = RunAsync(_cancellation.Token);
    }

    public void AddLeg(string receiverId, BoundedPcmBuffer buffer, IAudioPipeEndpoint endpoint, int alignmentTrimMs)
    {
        lock (_gate)
        {
            _legs.Add(receiverId, new(receiverId, buffer, endpoint)
            {
                ConfiguredAlignmentTrimMs = Math.Clamp(alignmentTrimMs, ReceiverAlignmentPlan.MinimumTrimMilliseconds, ReceiverAlignmentPlan.MaximumTrimMilliseconds)
            });
            ReapplyEffectiveAlignmentTrims();
        }
    }

    public void RemoveLeg(string receiverId)
    {
        lock (_gate)
        {
            _legs.Remove(receiverId);
            _gateReceiverIds.Remove(receiverId);
            ReapplyEffectiveAlignmentTrims();
        }
    }

    public void SetStandby(bool standby)
    {
        lock (_gate) _standby = standby;
    }

    public void SetCalibrationMode(bool active)
    {
        lock (_gate)
        {
            if (_calibrationMode == active) return;
            _calibrationMode = active;
            ResyncGroupCore();
        }
    }

    public void ResyncGroup()
    {
        lock (_gate) ResyncGroupCore();
    }

    public void BeginGroup(IEnumerable<string> receiverIds, TimeSpan? timeout = null, bool discardBufferedUntilGateOpen = true)
    {
        lock (_gate)
        {
            _standby = false;
            _discardBufferedUntilGateOpen = discardBufferedUntilGateOpen;
            _gateReceiverIds = receiverIds.Where(_legs.ContainsKey).ToHashSet(StringComparer.Ordinal);
            _groupGateOpen = _gateReceiverIds.Count == 0;
            _gateDeadlineUtc = _groupGateOpen ? null : _utcNow() + (timeout ?? TimeSpan.FromSeconds(10));
            foreach (var leg in _legs.Values)
            {
                leg.Ready = false;
                leg.Live = false;
                leg.ParticipatesInGate = _gateReceiverIds.Contains(leg.ReceiverId);
                leg.PendingInsertBytes = 0;
                leg.PendingDropBytes = 0;
                leg.CorrectionDebtBytes = 0;
            }
        }
    }

    public void MarkReady(string receiverId, bool resyncGroup = false)
    {
        lock (_gate)
        {
            if (!_legs.TryGetValue(receiverId, out var leg)) return;
            leg.Ready = true;
            ReapplyEffectiveAlignmentTrims();
            if (_groupGateOpen && !leg.Live)
            {
                // A late join starts at the live edge, never from buffered history.
                leg.Buffer.DiscardToLiveEdge(BlockBytes);
                MakeLive(leg, EffectiveAlignmentTrim(leg));
            }
            if (resyncGroup) ResyncGroupCore();
        }
    }

    public void MarkNotReady(string receiverId)
    {
        lock (_gate)
        {
            if (!_legs.TryGetValue(receiverId, out var leg)) return;
            leg.Ready = false;
            leg.Live = false;
            leg.PendingInsertBytes = 0;
            leg.PendingDropBytes = 0;
            leg.CorrectionDebtBytes = 0;
            ReapplyEffectiveAlignmentTrims();
        }
    }

    public int GetAlignmentTrim(string receiverId)
    {
        lock (_gate) return _legs.TryGetValue(receiverId, out var leg) ? leg.ConfiguredAlignmentTrimMs : 0;
    }

    public void SetAlignmentTrim(string receiverId, int milliseconds)
    {
        milliseconds = Math.Clamp(milliseconds, ReceiverAlignmentPlan.MinimumTrimMilliseconds, ReceiverAlignmentPlan.MaximumTrimMilliseconds);
        lock (_gate)
        {
            if (!_legs.TryGetValue(receiverId, out var leg)) return;
            leg.ConfiguredAlignmentTrimMs = milliseconds;
            ApplyEffectiveAlignmentTrim(leg, EffectiveAlignmentTrim(leg));
        }
    }

    /// <summary>Runs one shared-clock iteration. Public to allow deterministic timing tests.</summary>
    public async Task PumpOnceAsync(CancellationToken cancellationToken = default)
    {
        await _iterationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        _advanceCalibration?.Invoke();
        bool? containsSignal = null;
        if (_monitorBuffer is not null)
        {
            Span<byte> monitorBlock = stackalloc byte[BlockBytes];
            var read = _monitorBuffer.Read(monitorBlock, padWithSilence: false);
            containsSignal = read > 0 && PcmSignalDetector.ContainsSignal(monitorBlock[..read]);
        }
        List<(Leg Leg, byte[] Block)> writes;
        GroupGateTimeoutEventArgs? timedOut = null;
        var drainingReadyGate = false;
        lock (_gate)
        {
            if (!_groupGateOpen)
            {
                var allReady = _gateReceiverIds.All(id => _legs.TryGetValue(id, out var leg) && leg.Ready);
                var allWritable = allReady && _gateReceiverIds.All(id =>
                    _legs.TryGetValue(id, out var leg) && leg.Endpoint.IsConnected && leg.Endpoint.CanAcceptWrite);
                var timeout = _gateDeadlineUtc is { } deadline && _utcNow() >= deadline;
                drainingReadyGate = allReady && !allWritable && !timeout;
                if (allWritable || timeout)
                {
                    if (timeout && !allReady)
                        timedOut = new(_gateReceiverIds.Where(id => !_legs.TryGetValue(id, out var leg) || !leg.Ready).ToArray());
                    else if (timeout && !allWritable)
                        timedOut = new(_gateReceiverIds.Where(id => !_legs.TryGetValue(id, out var leg) || !leg.Endpoint.CanAcceptWrite).ToArray());
                    _groupGateOpen = true;
                    _gateDeadlineUtc = null;
                    foreach (var id in _gateReceiverIds)
                        if (_legs.TryGetValue(id, out var leg) && leg.Ready && leg.Endpoint.CanAcceptWrite)
                        {
                            if (_discardBufferedUntilGateOpen) leg.Buffer.DiscardToLiveEdge(BlockBytes);
                            MakeLive(leg, EffectiveAlignmentTrim(leg));
                        }
                    foreach (var leg in _legs.Values.Where(item => item.Ready && !item.Live))
                    {
                        leg.Buffer.DiscardToLiveEdge(BlockBytes);
                        MakeLive(leg, EffectiveAlignmentTrim(leg));
                    }
                    _discardBufferedUntilGateOpen = false;
                }
            }

            if (_groupGateOpen)
                foreach (var leg in _legs.Values.Where(item => item.Ready && !item.Live && item.Endpoint.CanAcceptWrite))
                {
                    leg.Buffer.DiscardToLiveEdge(BlockBytes);
                    MakeLive(leg, EffectiveAlignmentTrim(leg));
                }

            writes = [];
            foreach (var leg in _legs.Values)
            {
                if (_standby)
                {
                    leg.Buffer.Clear(advanceEpoch: false);
                    continue;
                }
                if (drainingReadyGate) continue;
                if (!leg.Endpoint.IsConnected) continue;
                var block = new byte[BlockBytes];
                if (leg.Live) FillLiveBlock(leg, block);
                writes.Add((leg, block));
            }

            if (NeedsGroupResync())
            {
                ResyncGroupCore();
                foreach (var (_, block) in writes) block.AsSpan().Clear();
            }
        }

        if (containsSignal is bool activity) CaptureActivityObserved?.Invoke(this, activity);
        if (timedOut is not null) GateTimedOut?.Invoke(this, timedOut);
        foreach (var (leg, block) in writes)
        {
            var result = leg.Endpoint.Write(block, tolerateBackpressure: !leg.Ready);
            if (result != AudioPipeWriteResult.Dropped) continue;
            lock (_gate)
            {
                if (_calibrationMode || !leg.Live || !_legs.TryGetValue(leg.ReceiverId, out var current) || !ReferenceEquals(current, leg)) continue;
                AddCorrectionInsert(leg, block.Length);
                if (NeedsGroupResync()) ResyncGroupCore();
            }
        }
        }
        finally { _iterationGate.Release(); }
    }

    private int EffectiveAlignmentTrim(Leg leg) =>
        !_calibrationMode && _legs.Values.Count(item => item.Ready) > 1 ? leg.ConfiguredAlignmentTrimMs : 0;

    private void ReapplyEffectiveAlignmentTrims()
    {
        foreach (var leg in _legs.Values)
            ApplyEffectiveAlignmentTrim(leg, EffectiveAlignmentTrim(leg));
    }

    private static void ApplyEffectiveAlignmentTrim(Leg leg, int milliseconds)
    {
        var deltaMilliseconds = milliseconds - leg.AppliedAlignmentTrimMs;
        leg.AppliedAlignmentTrimMs = milliseconds;
        if (!leg.Live || deltaMilliseconds == 0) return;
        var deltaBytes = ReceiverAlignmentPlan.ToPcmByteCount(Math.Abs(deltaMilliseconds));
        if (deltaMilliseconds > 0)
            AddPendingInsert(leg, deltaBytes);
        else
            AddPendingDrop(leg, deltaBytes);
        ClampCorrectionDebtToPending(leg);
    }

    private void MakeLive(Leg leg, int effectiveAlignmentTrimMs)
    {
        leg.Live = true;
        leg.AppliedAlignmentTrimMs = effectiveAlignmentTrimMs;
        var prefillMilliseconds = _calibrationMode ? 0 : leg.Buffer.TargetMilliseconds;
        leg.PendingInsertBytes = ReceiverAlignmentPlan.ToPcmByteCount(prefillMilliseconds + effectiveAlignmentTrimMs);
        leg.PendingDropBytes = 0;
        leg.CorrectionDebtBytes = 0;
    }

    private void FillLiveBlock(Leg leg, Span<byte> block)
    {
        if (_calibrationMode)
        {
            leg.Buffer.Read(block, padWithSilence: true);
            return;
        }

        Span<byte> discard = stackalloc byte[BlockBytes];
        while (leg.PendingDropBytes > 0)
        {
            var discardLength = Math.Min(discard.Length, leg.PendingDropBytes);
            var nonCorrectionBytes = leg.PendingDropBytes - Math.Max(0, -leg.CorrectionDebtBytes);
            var discarded = leg.Buffer.Read(discard[..discardLength], padWithSilence: false);
            leg.PendingDropBytes -= discarded;
            var correctionBytes = Math.Max(0, discarded - nonCorrectionBytes);
            if (leg.CorrectionDebtBytes < 0) leg.CorrectionDebtBytes = Math.Min(0, leg.CorrectionDebtBytes + correctionBytes);
            if (discarded < discardLength) break;
        }

        var silence = Math.Min(block.Length, leg.PendingInsertBytes);
        var nonCorrectionSilence = leg.PendingInsertBytes - Math.Max(0, leg.CorrectionDebtBytes);
        leg.PendingInsertBytes -= silence;
        var correctionSilence = Math.Max(0, silence - nonCorrectionSilence);
        if (leg.CorrectionDebtBytes > 0) leg.CorrectionDebtBytes = Math.Max(0, leg.CorrectionDebtBytes - correctionSilence);
        if (silence < block.Length)
        {
            leg.Buffer.Read(block[silence..], padWithSilence: true, out var paddedBytes, out var producerActive);
            if (producerActive && paddedBytes > 0) AddCorrectionDrop(leg, paddedBytes);
        }
    }

    private static void AddPendingInsert(Leg leg, int bytes)
    {
        var cancellation = Math.Min(bytes, leg.PendingDropBytes);
        leg.PendingDropBytes -= cancellation;
        leg.PendingInsertBytes += bytes - cancellation;
    }

    private static void AddPendingDrop(Leg leg, int bytes)
    {
        var cancellation = Math.Min(bytes, leg.PendingInsertBytes);
        leg.PendingInsertBytes -= cancellation;
        leg.PendingDropBytes += bytes - cancellation;
    }

    private static void AddCorrectionInsert(Leg leg, int bytes)
    {
        leg.CorrectionDebtBytes += bytes;
        AddPendingInsert(leg, bytes);
        ClampCorrectionDebtToPending(leg);
    }

    private static void AddCorrectionDrop(Leg leg, int bytes)
    {
        leg.CorrectionDebtBytes -= bytes;
        AddPendingDrop(leg, bytes);
        ClampCorrectionDebtToPending(leg);
    }

    private static void ClampCorrectionDebtToPending(Leg leg)
    {
        if (leg.CorrectionDebtBytes > 0)
            leg.CorrectionDebtBytes = Math.Min(leg.CorrectionDebtBytes, leg.PendingInsertBytes);
        else if (leg.CorrectionDebtBytes < 0)
            leg.CorrectionDebtBytes = -Math.Min(-leg.CorrectionDebtBytes, leg.PendingDropBytes);
    }

    private bool NeedsGroupResync()
    {
        var thresholdBytes = ReceiverAlignmentPlan.ToPcmByteCount(CorrectionDebtResyncMilliseconds);
        return _legs.Values.Any(leg => Math.Abs((long)leg.CorrectionDebtBytes) > thresholdBytes);
    }

    private void ResyncGroupCore()
    {
        var liveLegs = _legs.Values.Where(item => item.Live).ToArray();
        if (liveLegs.Length == 0) return;
        _groupResyncCount++;
        foreach (var leg in liveLegs)
        {
            leg.Buffer.DiscardToLiveEdge(BlockBytes);
            MakeLive(leg, EffectiveAlignmentTrim(leg));
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(BlockMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await PumpOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null) return;
        _cancellation.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
        _cancellation = null;
        _pumpTask = null;
        _iterationGate.Dispose();
    }
}
