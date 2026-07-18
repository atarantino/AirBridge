namespace AirBridge.Core;

public sealed class StreamCoordinator
{
    private readonly object _gate = new();
    private readonly Func<BufferSnapshot> _snapshot;
    private readonly Action _clear;
    private RouteInfo _route = new(null, CaptureMode.SystemMix, null, null, null, StreamState.Idle, null);
    private string? _lastError;
    private bool? _lastFixVerified;
    private long _captureDiscontinuities;

    public StreamCoordinator(BoundedPcmBuffer buffer) : this(buffer.Snapshot, () => buffer.Clear()) { }

    public StreamCoordinator(Func<BufferSnapshot> snapshot, Action clear)
    {
        _snapshot = snapshot;
        _clear = clear;
    }
    public event EventHandler<RouteInfo>? RouteChanged;

    public RouteInfo Route { get { lock (_gate) return _route; } }

    public void Transition(StreamState state, string? error = null)
    {
        lock (_gate)
        {
            _route = _route with { State = state };
            _lastError = error;
        }
        RouteChanged?.Invoke(this, Route);
    }

    public void Begin(string receiverId, string receiverName, CaptureMode mode, int? processId)
    {
        lock (_gate)
        {
            _route = new($"stream-{Guid.NewGuid():N}", mode, processId, receiverId, receiverName, StreamState.Connecting, DateTimeOffset.UtcNow);
            _lastError = null;
            _lastFixVerified = null;
        }
        _clear();
        RouteChanged?.Invoke(this, Route);
    }

    public void Stop()
    {
        lock (_gate) _route = new(null, CaptureMode.SystemMix, null, null, null, StreamState.Idle, null);
        _clear();
        RouteChanged?.Invoke(this, Route);
    }

    public void UpdateDestinations(IReadOnlyList<ReceiverPlaybackInfo> destinations)
    {
        lock (_gate)
        {
            var aggregate = AggregateState(destinations);
            var names = string.Join(", ", destinations.Select(item => item.Receiver.Name));
            _route = _route with
            {
                ReceiverId = destinations.FirstOrDefault()?.Receiver.Id,
                ReceiverName = destinations.Count switch { 0 => null, 1 => names, _ => $"{destinations[0].Receiver.Name} + {destinations.Count - 1}" },
                State = aggregate,
                Destinations = destinations
            };
            _lastError = destinations.FirstOrDefault(item => item.State == StreamState.Failed)?.LastError;
        }
        RouteChanged?.Invoke(this, Route);
    }

    public void CaptureDiscontinuity() => Interlocked.Increment(ref _captureDiscontinuities);
    public void MarkFixVerified(bool verified) => _lastFixVerified = verified;

    public StreamHealth Health()
    {
        var snapshot = _snapshot();
        var rating = Route.State switch
        {
            StreamState.Streaming when snapshot.StarvedWhileActivePaddingBytes == 0 => "healthy",
            StreamState.Streaming or StreamState.Degraded or StreamState.Reconnecting => "degraded",
            StreamState.Standby => "standby",
            StreamState.Idle => "idle",
            _ => "failed"
        };
        return new(Route.StreamId, Route.State, rating, snapshot, Interlocked.Read(ref _captureDiscontinuities), 0, 0, 0, DateTimeOffset.UtcNow, _lastFixVerified, _lastError);
    }

    private static StreamState AggregateState(IReadOnlyList<ReceiverPlaybackInfo> destinations)
    {
        if (destinations.Count == 0) return StreamState.Idle;
        var streaming = destinations.Count(item => item.State == StreamState.Streaming);
        if (streaming == destinations.Count) return StreamState.Streaming;
        if (streaming > 0) return StreamState.Degraded;
        if (destinations.All(item => item.State == StreamState.Standby)) return StreamState.Standby;
        if (destinations.Any(item => item.State is StreamState.Connecting or StreamState.Negotiating or StreamState.Buffering)) return StreamState.Connecting;
        if (destinations.Any(item => item.State == StreamState.Reconnecting)) return StreamState.Reconnecting;
        return StreamState.Failed;
    }
}
