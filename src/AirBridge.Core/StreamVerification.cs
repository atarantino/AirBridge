using System.Text.Json.Serialization;

namespace AirBridge.Core;

[JsonConverter(typeof(JsonStringEnumConverter<StreamVerificationStatus>))]
public enum StreamVerificationStatus { Observing, Verified, Failed, Inconclusive }

public sealed record ReceiverVerificationObservation(StreamState State, BufferSnapshot Buffer);

public sealed record StreamVerificationObservation(
    string? StreamId,
    StreamState State,
    IReadOnlyDictionary<string, ReceiverVerificationObservation> Receivers);

public sealed record ReceiverVerificationMetrics(
    long ActiveBytesWritten,
    long BytesRead,
    long ActiveStarvationBytes,
    long Overruns,
    bool EpochStable);

public sealed record StreamVerificationResult(
    StreamVerificationStatus Status,
    int WindowMilliseconds,
    int ObservedMilliseconds,
    int ActiveMilliseconds,
    int RequiredActiveMilliseconds,
    int Samples,
    bool RouteStable,
    bool ContinuouslyStreaming,
    bool ObservationContinuous,
    bool ActivePcmAdvanced,
    bool NoActiveStarvation,
    bool NoOverruns,
    IReadOnlyDictionary<string, ReceiverVerificationMetrics> Receivers,
    string Note)
{
    public bool? Verified => Status switch
    {
        StreamVerificationStatus.Verified => true,
        StreamVerificationStatus.Failed => false,
        _ => null
    };
}

/// <summary>
/// Evaluates fresh, per-receiver evidence. A final Streaming state alone cannot
/// erase an interruption, receiver replacement, starvation, or missing samples.
/// Acoustic output is outside this local PCM verification boundary.
/// </summary>
public sealed class StreamVerificationWindow
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly TimeSpan _maximumGap;
    private readonly long _started;
    private long _lastObserved;
    private readonly StreamVerificationObservation _baseline;
    private StreamVerificationObservation _previous;
    private StreamVerificationObservation _latest;
    private bool _routeStable = true;
    private bool _streaming;
    private bool _continuous = true;
    private bool _noStarvation = true;
    private bool _noOverruns = true;
    private double _activeMilliseconds;
    private int _samples = 1;
    private readonly Dictionary<string, bool> _epochs;

    public StreamVerificationWindow(StreamVerificationObservation baseline, TimeProvider? timeProvider = null,
        TimeSpan? window = null, TimeSpan? maximumObservationGap = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _window = window ?? TimeSpan.FromSeconds(10);
        _maximumGap = maximumObservationGap ?? TimeSpan.FromSeconds(1);
        if (_window <= TimeSpan.Zero || _maximumGap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _started = _lastObserved = _time.GetTimestamp();
        _baseline = _previous = _latest = baseline;
        _streaming = IsStreaming(baseline);
        _epochs = baseline.Receivers.Keys.ToDictionary(id => id, _ => true, StringComparer.Ordinal);
    }

    public bool Complete => _time.GetElapsedTime(_started) >= _window;

    public void Observe(StreamVerificationObservation observation)
    {
        var now = _time.GetTimestamp();
        var interval = _time.GetElapsedTime(_lastObserved, now);
        if (interval > _maximumGap) _continuous = false;
        _routeStable &= observation.StreamId == _baseline.StreamId &&
            observation.Receivers.Count == _baseline.Receivers.Count &&
            _baseline.Receivers.Keys.All(observation.Receivers.ContainsKey);
        _streaming &= IsStreaming(observation);
        var activeProgress = observation.Receivers.Count > 0;
        var advancedMilliseconds = interval.TotalMilliseconds;
        foreach (var (id, original) in _baseline.Receivers)
        {
            if (!observation.Receivers.TryGetValue(id, out var current))
            {
                activeProgress = false;
                continue;
            }
            _epochs[id] &= current.Buffer.Epoch == original.Buffer.Epoch;
            _noStarvation &= current.Buffer.StarvedWhileActivePaddingBytes == original.Buffer.StarvedWhileActivePaddingBytes;
            _noOverruns &= current.Buffer.Overruns == original.Buffer.Overruns;
            activeProgress &= _previous.Receivers.TryGetValue(id, out var previous) &&
                current.Buffer.ActiveBytesWritten > previous.Buffer.ActiveBytesWritten &&
                current.Buffer.BytesRead > previous.Buffer.BytesRead;
            if (previous is not null)
                advancedMilliseconds = Math.Min(advancedMilliseconds,
                    Math.Min(current.Buffer.ActiveBytesWritten - previous.Buffer.ActiveBytesWritten,
                        current.Buffer.BytesRead - previous.Buffer.BytesRead) / 176.4);
        }
        if (activeProgress && interval <= _maximumGap) _activeMilliseconds += Math.Max(0, advancedMilliseconds);
        _latest = _previous = observation;
        _lastObserved = now;
        _samples++;
    }

    public StreamVerificationResult Result()
    {
        var elapsed = _time.GetElapsedTime(_started);
        var requiredActive = Math.Min(1000, _window.TotalMilliseconds);
        var routeStable = _routeStable && _epochs.Values.All(stable => stable);
        var active = _activeMilliseconds >= requiredActive;
        var continuous = _continuous && _time.GetElapsedTime(_lastObserved) <= _maximumGap;
        var status = !Complete ? StreamVerificationStatus.Observing :
            _baseline.StreamId is null || _baseline.Receivers.Count == 0 ? StreamVerificationStatus.Inconclusive :
            !routeStable || !_streaming || !_noStarvation || !_noOverruns ? StreamVerificationStatus.Failed :
            !continuous || !active ? StreamVerificationStatus.Inconclusive : StreamVerificationStatus.Verified;
        var metrics = _baseline.Receivers.ToDictionary(pair => pair.Key, pair =>
        {
            var initial = pair.Value.Buffer;
            var current = _latest.Receivers.TryGetValue(pair.Key, out var receiver) ? receiver.Buffer : initial;
            return new ReceiverVerificationMetrics(
                Math.Max(0, current.ActiveBytesWritten - initial.ActiveBytesWritten),
                Math.Max(0, current.BytesRead - initial.BytesRead),
                Math.Max(0, current.StarvedWhileActivePaddingBytes - initial.StarvedWhileActivePaddingBytes),
                Math.Max(0, current.Overruns - initial.Overruns), _epochs[pair.Key]);
        }, StringComparer.Ordinal);
        var note = status switch
        {
            StreamVerificationStatus.Observing => "Collecting fresh per-receiver PCM evidence.",
            StreamVerificationStatus.Verified => "Local PCM progressed without active starvation or overruns throughout an observed stable stream. Acoustic output was not measured.",
            StreamVerificationStatus.Failed => "The observed stream was interrupted, its route or buffer epoch changed, or a receiver had active starvation or overruns.",
            _ => "Insufficient continuous active PCM evidence; idle audio or missing observations cannot verify a fix. Acoustic output was not measured."
        };
        return new(status, (int)_window.TotalMilliseconds, (int)elapsed.TotalMilliseconds,
            (int)_activeMilliseconds, (int)requiredActive, _samples, routeStable, _streaming,
            continuous, active, _noStarvation, _noOverruns, metrics, note);
    }

    private static bool IsStreaming(StreamVerificationObservation observation) =>
        observation.StreamId is not null && observation.State == StreamState.Streaming &&
        observation.Receivers.Count > 0 && observation.Receivers.Values.All(receiver => receiver.State == StreamState.Streaming);

    public static bool AppliesToCurrentStream(StreamVerificationObservation completed, StreamVerificationObservation current) =>
        IsStreaming(current) && current.StreamId == completed.StreamId &&
        completed.Receivers.Count == current.Receivers.Count &&
        completed.Receivers.All(item => current.Receivers.TryGetValue(item.Key, out var receiver) &&
            receiver.Buffer.Epoch == item.Value.Buffer.Epoch &&
            receiver.Buffer.StarvedWhileActivePaddingBytes == item.Value.Buffer.StarvedWhileActivePaddingBytes &&
            receiver.Buffer.Overruns == item.Value.Buffer.Overruns);
}
