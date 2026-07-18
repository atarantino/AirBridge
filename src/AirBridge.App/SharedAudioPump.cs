using AirBridge.Core;

namespace AirBridge.App;

public interface IAudioPipeEndpoint
{
    string PipeName { get; }
    bool IsConnected { get; }
    bool CanAcceptWrite { get; }
    bool TryWrite(byte[] pcm, bool tolerateBackpressure = false);
}

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

    private sealed class Leg(string receiverId, BoundedPcmBuffer buffer, IAudioPipeEndpoint endpoint)
    {
        public string ReceiverId { get; } = receiverId;
        public BoundedPcmBuffer Buffer { get; } = buffer;
        public IAudioPipeEndpoint Endpoint { get; } = endpoint;
        public bool Ready { get; set; }
        public bool ParticipatesInGate { get; set; }
        public bool Live { get; set; }
        public int AlignmentTrimMs { get; set; }
        public int PendingInsertBytes { get; set; }
        public int PendingDropBytes { get; set; }
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
    private bool _discardBufferedUntilGateOpen;

    public SharedAudioPump(BoundedPcmBuffer? monitorBuffer = null, Func<DateTimeOffset>? utcNow = null, Action? advanceCalibration = null)
    {
        _monitorBuffer = monitorBuffer;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _advanceCalibration = advanceCalibration;
    }

    public event EventHandler<GroupGateTimeoutEventArgs>? GateTimedOut;
    public event EventHandler<bool>? CaptureActivityObserved;

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
                AlignmentTrimMs = Math.Clamp(alignmentTrimMs, 0, 500)
            });
        }
    }

    public void RemoveLeg(string receiverId)
    {
        lock (_gate)
        {
            _legs.Remove(receiverId);
            _gateReceiverIds.Remove(receiverId);
        }
    }

    public void SetStandby(bool standby)
    {
        lock (_gate) _standby = standby;
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
            }
        }
    }

    public void MarkReady(string receiverId)
    {
        lock (_gate)
        {
            if (!_legs.TryGetValue(receiverId, out var leg)) return;
            leg.Ready = true;
            if (_groupGateOpen && !leg.Live)
            {
                // A late join starts at the live edge, never from buffered history.
                leg.Buffer.DiscardToLiveEdge(BlockBytes);
                MakeLive(leg);
            }
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
        }
    }

    public int GetAlignmentTrim(string receiverId)
    {
        lock (_gate) return _legs.TryGetValue(receiverId, out var leg) ? leg.AlignmentTrimMs : 0;
    }

    public void SetAlignmentTrim(string receiverId, int milliseconds)
    {
        milliseconds = Math.Clamp(milliseconds, 0, 500);
        lock (_gate)
        {
            if (!_legs.TryGetValue(receiverId, out var leg)) return;
            var deltaMilliseconds = milliseconds - leg.AlignmentTrimMs;
            var deltaBytes = ReceiverAlignmentPlan.ToPcmByteCount(Math.Abs(deltaMilliseconds));
            leg.AlignmentTrimMs = milliseconds;
            if (!leg.Live) return;
            if (deltaMilliseconds > 0)
            {
                var cancellation = Math.Min(deltaBytes, leg.PendingDropBytes);
                leg.PendingDropBytes -= cancellation;
                leg.PendingInsertBytes += deltaBytes - cancellation;
            }
            else if (deltaMilliseconds < 0)
            {
                var cancellation = Math.Min(deltaBytes, leg.PendingInsertBytes);
                leg.PendingInsertBytes -= cancellation;
                leg.PendingDropBytes += deltaBytes - cancellation;
            }
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
                            MakeLive(leg);
                        }
                    foreach (var leg in _legs.Values.Where(item => item.Ready && !item.Live))
                    {
                        leg.Buffer.DiscardToLiveEdge(BlockBytes);
                        MakeLive(leg);
                    }
                    _discardBufferedUntilGateOpen = false;
                }
            }

            if (_groupGateOpen)
                foreach (var leg in _legs.Values.Where(item => item.Ready && !item.Live && item.Endpoint.CanAcceptWrite))
                {
                    leg.Buffer.DiscardToLiveEdge(BlockBytes);
                    MakeLive(leg);
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
        }

        if (containsSignal is bool activity) CaptureActivityObserved?.Invoke(this, activity);
        if (timedOut is not null) GateTimedOut?.Invoke(this, timedOut);
        foreach (var (leg, block) in writes) leg.Endpoint.TryWrite(block, tolerateBackpressure: !leg.Ready);
        }
        finally { _iterationGate.Release(); }
    }

    private static void MakeLive(Leg leg)
    {
        leg.Live = true;
        leg.PendingInsertBytes = ReceiverAlignmentPlan.ToPcmByteCount(leg.AlignmentTrimMs);
        leg.PendingDropBytes = 0;
    }

    private static void FillLiveBlock(Leg leg, Span<byte> block)
    {
        Span<byte> discard = stackalloc byte[BlockBytes];
        while (leg.PendingDropBytes > 0)
        {
            var discardLength = Math.Min(discard.Length, leg.PendingDropBytes);
            var discarded = leg.Buffer.Read(discard[..discardLength], padWithSilence: false);
            leg.PendingDropBytes -= discarded;
            if (discarded < discardLength) break;
        }

        var silence = Math.Min(block.Length, leg.PendingInsertBytes);
        leg.PendingInsertBytes -= silence;
        if (silence < block.Length) leg.Buffer.Read(block[silence..], padWithSilence: true);
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
