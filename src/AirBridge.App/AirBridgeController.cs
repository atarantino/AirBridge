using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.App;

public sealed class AirBridgeController : IAgentToolRuntime, IAsyncDisposable
{
    private sealed class ReceiverLeg(ReceiverInfo receiver, BoundedPcmBuffer buffer, PipeAudioServer pipe, int volume, int alignmentTrimMs)
    {
        public ReceiverInfo Receiver { get; } = receiver;
        public BoundedPcmBuffer Buffer { get; } = buffer;
        public PipeAudioServer Pipe { get; } = pipe;
        public int Volume { get; set; } = volume;
        public int AlignmentTrimMs { get; set; } = alignmentTrimMs;
        public StreamState State { get; set; } = StreamState.Connecting;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public string? LastError { get; set; }

        public ReceiverPlaybackInfo Snapshot() => new(Receiver, State, Volume, StartedUtc, LastError, AlignmentTrimMs);
    }

    private readonly PcmBroadcastHub _hub = new();
    private readonly IRaopClient _raop;
    private readonly IAudioCaptureService _capture;
    private readonly AcousticDelayMeasurer _delayMeasurer = new();
    private readonly SharedAudioPump _pump;
    private readonly SilenceStandbyTracker _standbyTracker = new();
    private readonly Dictionary<string, ReceiverLeg> _legs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _alignmentTrims = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _routeGate = new(1, 1);
    private readonly object _legsGate = new();
    private readonly object _toolAliasGate = new();
    private readonly Dictionary<string, string> _toolAliasesByReceiverId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _receiverIdsByToolAlias = new(StringComparer.Ordinal);
    private IReadOnlyList<ReceiverInfo> _receivers = [];
    private (DateTimeOffset ChangedUtc, long BaselineUnderruns)? _pendingBufferVerification;
    private bool _captureRunning;
    private bool _isStandby;
    private volatile bool _measurementInProgress;
    private volatile bool _latestCaptureActive;
    private volatile bool _standbyWakeRequested;
    private int _standbySuppressionCount;
    private int _standbyTransitionInProgress;

    public AirBridgeController() : this(null, null) { }

    internal AirBridgeController(IRaopClient? raop, IAudioCaptureService? capture)
    {
        Coordinator = new(_hub.Snapshot, _hub.Clear);
        _raop = raop ?? new PythonRaopClient();
        _capture = capture ?? new WasapiCaptureService(_hub, Coordinator);
        _pump = new(_hub.MonitorBuffer, advanceCalibration: () => _hub.AdvanceCalibration(SharedAudioPump.BlockBytes));
        _raop.StateChanged += OnReceiverStateChanged;
        _pump.GateTimedOut += (_, args) => OnGateTimedOut(args);
        _pump.CaptureActivityObserved += (_, active) => OnCaptureActivity(active);
        _pump.Start();
    }

    public event EventHandler? PlaybackChanged;
    public event EventHandler<ReceiverAlignmentSettingChangedEventArgs>? ReceiverAlignmentChanged;
    public event EventHandler<SilenceStandbySettingChangedEventArgs>? SilenceStandbyChanged;
    public event EventHandler<string>? TelemetryEvent;
    public StreamCoordinator Coordinator { get; }
    public IReadOnlyList<ReceiverInfo> Receivers => _receivers;
    public IReadOnlyList<ReceiverPlaybackInfo> ReceiverPlayback
    {
        get { lock (_legsGate) return _legs.Values.Select(item => item.Snapshot()).OrderBy(item => item.Receiver.Name).ToArray(); }
    }
    public bool BrowserSyncActive { get; private set; }
    public int BrowserSyncOffsetMs { get; private set; } = 2000;
    public AcousticDelayResult? LastAcousticDelay { get; private set; }
    public GroupAlignmentResult? LastGroupAlignment { get; private set; }
    public bool IsResumingFromStandby { get; private set; }

    public void ConfigureSettings(AirBridgeSettings settings)
    {
        lock (_legsGate)
        {
            _alignmentTrims.Clear();
            foreach (var pair in settings.ReceiverAlignmentTrimMs)
                _alignmentTrims[pair.Key] = ReceiverAlignmentPlan.Resolve(pair.Key, settings.ReceiverAlignmentTrimMs);
        }
        _standbyTracker.Configure(settings.SilenceStandbyEnabled, Math.Clamp(settings.SilenceStandbySeconds, 10, 600));
    }

    public (bool Enabled, int Seconds) GetSilenceStandbySettings() => (_standbyTracker.Enabled, _standbyTracker.Seconds);

    public void SetSilenceStandbySettings(bool enabled, int seconds)
    {
        _standbyTracker.Configure(enabled, seconds);
        SilenceStandbyChanged?.Invoke(this, new(enabled, seconds));
    }

    public int GetReceiverAlignmentTrim(string receiverId)
    {
        lock (_legsGate) return _alignmentTrims.TryGetValue(receiverId, out var value) ? value : 0;
    }

    public void SetReceiverAlignmentTrim(string receiverId, int milliseconds)
    {
        if (milliseconds is < ReceiverAlignmentPlan.MinimumTrimMilliseconds or > ReceiverAlignmentPlan.MaximumTrimMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        lock (_legsGate)
        {
            if (_measurementInProgress) throw new InvalidOperationException("Wait for acoustic alignment to finish before changing receiver trim.");
            _alignmentTrims[receiverId] = milliseconds;
            if (_legs.TryGetValue(receiverId, out var leg)) leg.AlignmentTrimMs = milliseconds;
        }
        _pump.SetAlignmentTrim(receiverId, milliseconds);
        ReceiverAlignmentChanged?.Invoke(this, new(receiverId, milliseconds));
        PublishDestinations();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) => await _raop.StartAsync(cancellationToken);

    public async Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var previousState = Coordinator.Route.State;
        var hadActiveRoute = Coordinator.Route.StreamId is not null;
        Coordinator.Transition(StreamState.Discovering);
        try
        {
            _receivers = await _raop.DiscoverAsync(cancellationToken);
            foreach (var receiver in _receivers) GetToolReceiverAlias(receiver.Id);
            Coordinator.Transition(hadActiveRoute ? previousState : StreamState.Idle);
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
            return _receivers;
        }
        catch (Exception ex)
        {
            AppLog.Error("discovery", "Receiver discovery failed.", ex);
            Coordinator.Transition(StreamState.Failed, ex.Message);
            throw;
        }
    }

    public async Task PairReceiverAsync(string receiverId, string pin, CancellationToken cancellationToken = default)
    {
        await _raop.FinishPairingAsync(receiverId, pin, cancellationToken);
        _receivers = await _raop.DiscoverAsync(cancellationToken);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<JsonElement> BeginPairingAsync(string receiverId, bool controlPairing = false, CancellationToken cancellationToken = default) =>
        _raop.BeginPairingAsync(receiverId, controlPairing, cancellationToken);

    public async Task CancelPairingAsync(string receiverId, CancellationToken cancellationToken = default) =>
        await _raop.CancelPairingAsync(receiverId, cancellationToken);

    public async Task SleepReceiverAsync(string receiverId, CancellationToken cancellationToken = default) =>
        await _raop.SleepAsync(receiverId, cancellationToken);

    public Task StartSystemAsync(ReceiverInfo receiver, CancellationToken cancellationToken = default) =>
        StartSystemAsync([receiver], cancellationToken);

    public Task StartSystemAsync(IReadOnlyCollection<ReceiverInfo> receivers, CancellationToken cancellationToken = default) =>
        StartSystemAsync(receivers, null, cancellationToken);

    public async Task StartSystemAsync(IReadOnlyCollection<ReceiverInfo> receivers, IReadOnlyDictionary<string, int>? volumes, CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken);
        try
        {
            await StopAllInternalAsync(cancellationToken);
            await StartRouteInternalAsync(receivers, CaptureMode.SystemMix, null, volumes, cancellationToken);
        }
        finally { _routeGate.Release(); }
    }

    public Task StartApplicationAsync(int processId, ReceiverInfo receiver, CancellationToken cancellationToken = default) =>
        StartApplicationAsync(processId, [receiver], cancellationToken);

    public Task StartApplicationAsync(int processId, IReadOnlyCollection<ReceiverInfo> receivers, CancellationToken cancellationToken = default) =>
        StartApplicationAsync(processId, receivers, null, cancellationToken);

    public async Task StartApplicationAsync(int processId, IReadOnlyCollection<ReceiverInfo> receivers, IReadOnlyDictionary<string, int>? volumes, CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken);
        try
        {
            await StopAllInternalAsync(cancellationToken);
            await StartRouteInternalAsync(receivers, CaptureMode.ProcessTreeInclude, processId, volumes, cancellationToken);
        }
        finally { _routeGate.Release(); }
    }

    public async Task AddReceiverAsync(ReceiverInfo receiver, int volume = 30, CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_legsGate) if (_legs.ContainsKey(receiver.Id)) return;
            var route = Coordinator.Route;
            if (route.StreamId is null)
            {
                await StartRouteInternalAsync([receiver], CaptureMode.SystemMix, null, new Dictionary<string, int> { [receiver.Id] = volume }, cancellationToken);
                return;
            }
            if (!_captureRunning) await StartCaptureAsync(route.Mode, route.ProcessId);
            await AddLegInternalAsync(receiver, volume, cancellationToken);
            PublishDestinations();
        }
        finally { _routeGate.Release(); }
    }

    public async Task StopReceiverAsync(string receiverId, CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken);
        try
        {
            ReceiverLeg? leg;
            lock (_legsGate) _legs.TryGetValue(receiverId, out leg);
            if (leg is null) return;
            try { await _raop.StopStreamAsync(receiverId, cancellationToken); } catch { }
            await leg.Pipe.DisposeAsync();
            _pump.RemoveLeg(receiverId);
            _hub.Unsubscribe(receiverId);
            lock (_legsGate) _legs.Remove(receiverId);
            if (ReceiverPlayback.Count == 0)
            {
                _capture.Stop();
                _captureRunning = false;
                Coordinator.Stop();
            }
            else PublishDestinations();
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _routeGate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken);
        try { await StopAllInternalAsync(cancellationToken); }
        finally { _routeGate.Release(); }
    }

    public async Task SetReceiverVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SetReceiverVolumeCoreAsync(receiverId, percent, cancellationToken).ConfigureAwait(false); }
        finally { _routeGate.Release(); }
    }

    private async Task SetReceiverVolumeCoreAsync(string receiverId, int percent, CancellationToken cancellationToken)
    {
        percent = Math.Clamp(percent, 0, 100);
        ReceiverLeg leg;
        lock (_legsGate) leg = _legs.TryGetValue(receiverId, out var value) ? value : throw new InvalidOperationException("Receiver is not active.");
        var previous = leg.Volume;
        leg.Volume = percent;
        PublishDestinations();
        if (_isStandby) return;
        try { await _raop.SetVolumeAsync(receiverId, percent, cancellationToken); }
        catch
        {
            leg.Volume = previous;
            PublishDestinations();
            throw;
        }
    }

    public async Task ReconnectReceiverAsync(string receiverId, CancellationToken cancellationToken = default)
    {
        ReceiverLeg leg;
        lock (_legsGate) leg = _legs.TryGetValue(receiverId, out var value) ? value : throw new InvalidOperationException("Receiver is not active.");
        leg.State = StreamState.Reconnecting;
        _pump.MarkNotReady(receiverId);
        PublishDestinations();
        await _raop.StartStreamAsync(leg.Receiver, leg.Pipe.PipeName, leg.Volume, cancellationToken);
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var ids = ReceiverPlayback.Select(item => item.Receiver.Id).ToArray();
        foreach (var id in ids) await ReconnectReceiverAsync(id, cancellationToken);
    }

    public async Task<AcousticDelayResult> MeasureAcousticDelayAsync(string receiverId, CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await MeasureAcousticDelayCoreAsync(receiverId, cancellationToken).ConfigureAwait(false); }
        finally { _routeGate.Release(); }
    }

    public async Task RunDiagnosticToneAsync(string receiverName, double seconds, CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Coordinator.Route.StreamId is not null)
                throw new InvalidOperationException("Stop the active route before playing a diagnostic tone; the diagnostic host command takes exclusive control of RAOP receivers.");
            using var suppression = SuppressStandby();
            await _raop.PlayDiagnosticToneAsync(receiverName, seconds, cancellationToken).ConfigureAwait(false);
        }
        finally { _routeGate.Release(); }
    }

    private async Task<AcousticDelayResult> MeasureAcousticDelayCoreAsync(string receiverId, CancellationToken cancellationToken)
    {
        ReceiverLeg leg;
        ReceiverLeg[] allActiveLegs;
        Dictionary<string, int> originalVolumes;
        lock (_legsGate)
        {
            if (_measurementInProgress) throw new InvalidOperationException("Another acoustic measurement is already running.");
            leg = _legs.TryGetValue(receiverId, out var value) ? value : throw new InvalidOperationException("The selected receiver is not streaming.");
            if (leg.State is not StreamState.Streaming and not StreamState.Degraded)
                throw new InvalidOperationException("Wait for the selected receiver to reach Streaming before measuring delay.");
            allActiveLegs = _legs.Values.ToArray();
            originalVolumes = allActiveLegs.ToDictionary(item => item.Receiver.Id, item => item.Volume, StringComparer.Ordinal);
            _measurementInProgress = true;
        }
        try
        {
            AcousticDelayResult? measured = null;
            await ReceiverMeasurementSequence.RunAsync(
                [receiverId],
                originalVolumes,
                (id, volume, token) => _raop.SetVolumeAsync(id, volume, token),
                async (_, token) =>
                {
                    measured = await _delayMeasurer.MeasureAsync(_hub, receiverId, leg.Receiver.Name, token).ConfigureAwait(false);
                    return new ReceiverDelayMeasurement(
                        measured.ReceiverId,
                        measured.ReceiverName,
                        measured.MedianMilliseconds,
                        measured.DelaysMilliseconds);
                },
                cancellationToken).ConfigureAwait(false);
            LastAcousticDelay = measured ?? throw new InvalidOperationException("The acoustic measurement did not produce a result.");
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
            return LastAcousticDelay;
        }
        finally
        {
            lock (_legsGate) _measurementInProgress = false;
            _standbyTracker.Reset();
        }
    }

    public async Task<GroupAlignmentResult> AlignGroupAsync(
        IReadOnlyCollection<string> receiverIds,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await AlignGroupCoreAsync(receiverIds, apply, cancellationToken).ConfigureAwait(false); }
        finally { _routeGate.Release(); }
    }

    private async Task<GroupAlignmentResult> AlignGroupCoreAsync(
        IReadOnlyCollection<string> receiverIds,
        bool apply,
        CancellationToken cancellationToken)
    {
        if (receiverIds.Count < 2) throw new InvalidOperationException("Select at least two streaming receivers to align.");
        ReceiverLeg[] legs;
        ReceiverLeg[] allActiveLegs;
        string? routeStreamId;
        lock (_legsGate)
        {
            if (_measurementInProgress) throw new InvalidOperationException("Another acoustic measurement is already running.");
            allActiveLegs = _legs.Values.ToArray();
            legs = receiverIds.Distinct(StringComparer.Ordinal)
                .Select(id => _legs.TryGetValue(id, out var leg) ? leg : throw new InvalidOperationException("Every selected receiver must be active."))
                .ToArray();
            if (legs.Any(leg => leg.State is not StreamState.Streaming and not StreamState.Degraded))
                throw new InvalidOperationException("Wait for every selected receiver to reach Streaming before aligning the group.");
            _measurementInProgress = true;
            routeStreamId = Coordinator.Route.StreamId;
        }

        var originalVolumes = allActiveLegs.ToDictionary(leg => leg.Receiver.Id, leg => leg.Volume, StringComparer.Ordinal);
        var targetLegs = legs.ToDictionary(leg => leg.Receiver.Id, StringComparer.Ordinal);
        var currentTrims = legs.ToDictionary(item => item.Receiver.Id, item => item.AlignmentTrimMs, StringComparer.Ordinal);
        IReadOnlyList<ReceiverDelayMeasurement> measurements;
        try
        {
            measurements = await ReceiverMeasurementSequence.RunAsync(
                legs.Select(leg => leg.Receiver.Id).ToArray(),
                originalVolumes,
                async (receiverId, volume, token) => { await _raop.SetVolumeAsync(receiverId, volume, token).ConfigureAwait(false); },
                async (receiverId, token) =>
                {
                    var target = targetLegs[receiverId];
                    var result = await _delayMeasurer.MeasureAsync(_hub, target.Receiver.Id, target.Receiver.Name, token).ConfigureAwait(false);
                    return new ReceiverDelayMeasurement(result.ReceiverId, result.ReceiverName, result.MedianMilliseconds, result.DelaysMilliseconds);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_legsGate) _measurementInProgress = false;
            _standbyTracker.Reset();
        }

        var measuredMedians = measurements.ToDictionary(item => item.ReceiverId, item => item.MedianMilliseconds, StringComparer.Ordinal);
        var untrimmedMedians = ReceiverAlignmentPlan.RemoveAppliedTrims(measuredMedians, currentTrims);
        var proposed = ReceiverAlignmentPlan.ProposeTrims(untrimmedMedians);
        LastGroupAlignment = new(measurements, ReceiverAlignmentPlan.PairwiseSkews(measuredMedians), proposed, false)
        {
            RouteStreamId = routeStreamId,
            RouteReceiverIds = allActiveLegs.Select(item => item.Receiver.Id).Order(StringComparer.Ordinal).ToArray(),
            BaselineTrimMilliseconds = new Dictionary<string, int>(currentTrims, StringComparer.Ordinal)
        };
        if (apply) ApplyGroupAlignment(LastGroupAlignment);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
        return LastGroupAlignment;
    }

    public void ApplyGroupAlignment(GroupAlignmentResult result)
    {
        lock (_legsGate)
        {
            if (_measurementInProgress) throw new InvalidOperationException("Wait for acoustic alignment to finish before applying its result.");
            var currentIds = _legs.Keys.Order(StringComparer.Ordinal).ToArray();
            GroupAlignmentApplicability.Validate(result, Coordinator.Route.StreamId, currentIds, _alignmentTrims);
            foreach (var pair in result.ProposedTrimMilliseconds)
            {
                _alignmentTrims[pair.Key] = pair.Value;
                if (_legs.TryGetValue(pair.Key, out var leg)) leg.AlignmentTrimMs = pair.Value;
            }
        }
        foreach (var pair in result.ProposedTrimMilliseconds)
        {
            _pump.SetAlignmentTrim(pair.Key, pair.Value);
            ReceiverAlignmentChanged?.Invoke(this, new(pair.Key, pair.Value));
        }
        LastGroupAlignment = result with { Applied = true };
        PublishDestinations();
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        return name switch
        {
            "list_airplay_devices" => Receivers.Select(item => new { id = GetToolReceiverAlias(item.Id), item.Name, item.RequiresPassword }),
            "list_audio_sessions" => WasapiCaptureService.ListSessions(),
            "get_current_routes" => GetToolRoute(),
            "get_stream_health" => await GetVerifiedHealthAsync(cancellationToken),
            "get_buffer_metrics" => new
            {
                aggregate = _hub.Snapshot(),
                receivers = _hub.ReceiverSnapshots().ToDictionary(item => GetToolReceiverAlias(item.Key), item => item.Value, StringComparer.Ordinal)
            },
            "get_network_metrics" => new { available = false, note = "Per-receiver transport counters are exposed when supported by pyatv." },
            "get_sync_status" => new { active = BrowserSyncActive, offset_ms = BrowserSyncOffsetMs },
            "get_alignment" => GetAlignmentTrimTool(arguments),
            "get_standby" => GetSilenceStandbyTool(),
            "run_connectivity_test" => await ConnectivityTestAsync(cancellationToken),
            "start_system_stream" => await StartSystemToolAsync(arguments, cancellationToken),
            "start_application_stream" => await StartApplicationToolAsync(arguments, cancellationToken),
            "stop_stream" => await StopToolAsync(arguments, cancellationToken),
            "move_stream" => await MoveToolAsync(arguments, cancellationToken),
            "set_receiver_volume" => await SetVolumeToolAsync(arguments, cancellationToken),
            "set_alignment_trim" => SetAlignmentTrimTool(arguments),
            "set_standby" => SetSilenceStandbyTool(arguments),
            "set_buffer_target" => SetBufferTarget(arguments),
            "reconnect_stream" => await ReconnectToolAsync(arguments, cancellationToken),
            "measure_acoustic_delay" => await MeasureAcousticDelayToolAsync(arguments, cancellationToken),
            "align_group" => await AlignGroupToolAsync(arguments, cancellationToken),
            "enable_browser_sync" => EnableBrowserSync(arguments.GetProperty("offset_ms").GetInt32()),
            "disable_browser_sync" => DisableBrowserSync(),
            "apply_sync_offset" => EnableBrowserSync(arguments.GetProperty("offset_ms").GetInt32()),
            _ => throw new InvalidOperationException("Tool handler is not implemented.")
        };
    }

    private async Task StartRouteInternalAsync(IReadOnlyCollection<ReceiverInfo> receivers, CaptureMode mode, int? processId, IReadOnlyDictionary<string, int>? volumes, CancellationToken cancellationToken)
    {
        if (receivers.Count == 0) throw new InvalidOperationException("Select at least one receiver.");
        _isStandby = false;
        IsResumingFromStandby = false;
        _pump.SetStandby(false);
        _standbyTracker.Reset();
        _standbyWakeRequested = false;
        _latestCaptureActive = false;
        var first = receivers.First();
        AppLog.Info("route", $"Starting {mode} route to {receivers.Count} receiver(s): {string.Join(", ", receivers.Select(item => item.Name))}.");
        Coordinator.Begin(first.Id, first.Name, mode, processId);
        foreach (var receiver in receivers) CreateLeg(receiver, ReceiverVolumePlan.Resolve(receiver.Id, volumes));
        _pump.BeginGroup(receivers.Select(item => item.Id));
        PublishDestinations();
        await StartCaptureAsync(mode, processId);
        var starts = receivers.Select(receiver => StartExistingLegAsync(receiver.Id, cancellationToken)).ToArray();
        await Task.WhenAll(starts);
        if (ReceiverPlayback.All(item => item.State == StreamState.Failed))
        {
            _capture.Stop();
            _captureRunning = false;
        }
    }

    private async Task StartCaptureAsync(CaptureMode mode, int? processId)
    {
        if (_captureRunning) return;
        if (mode == CaptureMode.SystemMix) await _capture.StartSystemAsync();
        else if (processId is int pid) await _capture.StartProcessTreeAsync(pid);
        else throw new InvalidOperationException("Application capture requires a process identifier.");
        _captureRunning = true;
    }

    private ReceiverLeg CreateLeg(ReceiverInfo receiver, int volume)
    {
        var buffer = _hub.Subscribe(receiver.Id, Coordinator.Health().Buffer.TargetMilliseconds);
        var pipe = new PipeAudioServer();
        pipe.Start();
        var trim = GetReceiverAlignmentTrim(receiver.Id);
        var leg = new ReceiverLeg(receiver, buffer, pipe, Math.Clamp(volume, 0, 100), trim);
        lock (_legsGate) _legs.Add(receiver.Id, leg);
        _pump.AddLeg(receiver.Id, buffer, pipe, trim);
        return leg;
    }

    private async Task AddLegInternalAsync(ReceiverInfo receiver, int volume, CancellationToken cancellationToken)
    {
        CreateLeg(receiver, volume);
        await StartExistingLegAsync(receiver.Id, cancellationToken);
    }

    private async Task StartExistingLegAsync(string receiverId, CancellationToken cancellationToken)
    {
        ReceiverLeg leg;
        lock (_legsGate) leg = _legs[receiverId];
        try
        {
            await _raop.StartStreamAsync(leg.Receiver, leg.Pipe.PipeName, leg.Volume, cancellationToken);
        }
        catch (Exception ex)
        {
            AppLog.Error("route", $"Could not start receiver {leg.Receiver.Name}.", ex);
            leg.State = StreamState.Failed;
            leg.LastError = ex.Message;
            PublishDestinations();
        }
    }

    private async Task StopAllInternalAsync(CancellationToken cancellationToken)
    {
        _isStandby = false;
        IsResumingFromStandby = false;
        _pump.SetStandby(false);
        _standbyTracker.Reset();
        _standbyWakeRequested = false;
        _latestCaptureActive = false;
        _capture.Stop();
        _captureRunning = false;
        try { await _raop.StopAllStreamsAsync(cancellationToken); }
        catch (Exception ex) { AppLog.Error("route", "RAOP host failed while stopping all streams.", ex); }
        ReceiverLeg[] legs;
        lock (_legsGate) { legs = _legs.Values.ToArray(); _legs.Clear(); }
        foreach (var leg in legs)
        {
            _hub.Unsubscribe(leg.Receiver.Id);
            _pump.RemoveLeg(leg.Receiver.Id);
            await leg.Pipe.DisposeAsync();
        }
        Coordinator.Stop();
        AppLog.Info("route", "All receiver routes stopped.");
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnReceiverStateChanged(object? sender, (string? ReceiverId, StreamState State, string? Error) value)
    {
        if (value.ReceiverId is null) return;
        lock (_legsGate)
        {
            if (!_legs.TryGetValue(value.ReceiverId, out var leg)) return;
            leg.State = _isStandby && value.State == StreamState.Idle ? StreamState.Standby : value.State;
            leg.LastError = value.Error;
        }
        AppLog.Info("receiver", $"{ReceiverPlayback.FirstOrDefault(item => item.Receiver.Id == value.ReceiverId)?.Receiver.Name ?? "Unknown receiver"}: {value.State}{(value.Error is null ? "." : $"; {value.Error}")}");
        if (value.State == StreamState.Streaming) _pump.MarkReady(value.ReceiverId);
        else if (value.State is StreamState.Reconnecting or StreamState.Failed or StreamState.Idle) _pump.MarkNotReady(value.ReceiverId);
        var playback = ReceiverPlayback;
        if (IsResumingFromStandby && playback.Count > 0 && playback.All(item => item.State is StreamState.Streaming or StreamState.Failed))
            IsResumingFromStandby = false;
        PublishDestinations();
    }

    private void OnGateTimedOut(GroupGateTimeoutEventArgs args)
    {
        IsResumingFromStandby = false;
        string[] names;
        lock (_legsGate)
        {
            names = args.ReceiverIds.Select(id => _legs.TryGetValue(id, out var found) ? found.Receiver.Name : id).ToArray();
            foreach (var receiverId in args.ReceiverIds)
                if (_legs.TryGetValue(receiverId, out var leg))
                    leg.LastError = "Timed out waiting for synchronized group start; ready receivers continued.";
        }
        TelemetryEvent?.Invoke(this, $"Group gate timed out waiting for: {string.Join(", ", names)}. Ready receivers continued at the shared live edge.");
        PublishDestinations();
    }

    private void OnCaptureActivity(bool containsSignal)
    {
        _latestCaptureActive = containsSignal;
        if (containsSignal && (_isStandby || Volatile.Read(ref _standbyTransitionInProgress) != 0)) _standbyWakeRequested = true;
        var route = Coordinator.Route;
        var action = _standbyTracker.Observe(
            containsSignal,
            route.StreamId is not null,
            _isStandby,
            _measurementInProgress || Volatile.Read(ref _standbySuppressionCount) > 0);
        if (action == SilenceStandbyAction.None || Interlocked.CompareExchange(ref _standbyTransitionInProgress, 1, 0) != 0) return;
        if (action == SilenceStandbyAction.EnterStandby) _standbyWakeRequested = false;
        var expectedStreamId = route.StreamId;
        _ = Task.Run(async () =>
        {
            try
            {
                if (action == SilenceStandbyAction.EnterStandby) await EnterStandbyAsync(expectedStreamId).ConfigureAwait(false);
                else await ResumeFromStandbyAsync(expectedStreamId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("standby", "Standby transition failed.", ex);
                Coordinator.Transition(StreamState.Failed, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _standbyTransitionInProgress, 0);
                if (_isStandby && _standbyWakeRequested) OnCaptureActivity(true);
            }
        });
    }

    internal void ObserveCaptureActivityForTest(bool containsSignal) => OnCaptureActivity(containsSignal);

    private async Task EnterStandbyAsync(string? expectedStreamId)
    {
        await _routeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isStandby || Coordinator.Route.StreamId is null || Coordinator.Route.StreamId != expectedStreamId || _standbyWakeRequested || _latestCaptureActive) return;
            await StandbyRouteTransition.EnterAsync(
                token => _raop.StopAllStreamsAsync(token),
                () =>
                {
                    _isStandby = true;
                    _pump.SetStandby(true);
                    lock (_legsGate)
                        foreach (var leg in _legs.Values)
                        {
                            leg.Pipe.DisconnectClient();
                            leg.State = StreamState.Standby;
                        }
                    PublishDestinations();
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally { _routeGate.Release(); }
    }

    private async Task ResumeFromStandbyAsync(string? expectedStreamId)
    {
        await _routeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isStandby || Coordinator.Route.StreamId is null || Coordinator.Route.StreamId != expectedStreamId) return;
            _standbyWakeRequested = false;
            ReceiverLeg[] legs;
            lock (_legsGate)
            {
                legs = _legs.Values.ToArray();
                foreach (var leg in legs)
                {
                    leg.Buffer.Clear();
                    leg.State = StreamState.Connecting;
                    leg.LastError = null;
                }
            }
            var resumePlan = ReceiverResumePlan.Create(legs.Select(leg => leg.Snapshot()));
            _isStandby = false;
            IsResumingFromStandby = true;
            PublishDestinations();
            await StandbyRouteTransition.ResumeAsync(
                resumePlan,
                (receiverId, trim) => _pump.SetAlignmentTrim(receiverId, trim),
                receiverIds => _pump.BeginGroup(receiverIds, discardBufferedUntilGateOpen: true),
                (receiverId, volume, token) =>
                {
                    lock (_legsGate)
                        if (_legs.TryGetValue(receiverId, out var leg)) leg.Volume = volume;
                    return StartExistingLegAsync(receiverId, token);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally { _routeGate.Release(); }
    }

    private void PublishDestinations()
    {
        var snapshot = ReceiverPlayback;
        Coordinator.UpdateDestinations(snapshot);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    private IDisposable SuppressStandby()
    {
        Interlocked.Increment(ref _standbySuppressionCount);
        _standbyTracker.Reset();
        return new StandbySuppression(this);
    }

    private sealed class StandbySuppression(AirBridgeController owner) : IDisposable
    {
        private AirBridgeController? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is null) return;
            Interlocked.Decrement(ref value._standbySuppressionCount);
            value._standbyTracker.Reset();
        }
    }

    private Task<object> ConnectivityTestAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object>(new { raop_host = "running", receivers = Receivers.Count, active_receivers = ReceiverPlayback.Count });

    private IReadOnlyCollection<ReceiverInfo> ResolveReceivers(JsonElement arguments)
    {
        if (arguments.TryGetProperty("receiver_ids", out var ids)) return ids.EnumerateArray().Select(item => ResolveToolReceiver(item.GetString()!)).DistinctBy(item => item.Id).ToArray();
        return [ResolveToolReceiver(arguments.GetProperty("receiver_id").GetString()!)];
    }

    private async Task<object> StartSystemToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        await StartSystemAsync(ResolveReceivers(arguments), cancellationToken);
        return GetToolRoute();
    }

    private async Task<object> StartApplicationToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        await StartApplicationAsync(arguments.GetProperty("process_id").GetInt32(), ResolveReceivers(arguments), cancellationToken);
        return GetToolRoute();
    }

    private async Task<object> StopToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var receiverId = arguments.GetProperty("receiver_id");
        if (!arguments.GetProperty("all").GetBoolean() && receiverId.ValueKind == JsonValueKind.String)
            await StopReceiverAsync(ResolveToolReceiverId(receiverId.GetString()!), cancellationToken);
        else await StopAsync(cancellationToken);
        return new { stopped = true };
    }

    private async Task<object> MoveToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var route = Coordinator.Route;
        var receivers = ResolveReceivers(arguments);
        if (route.Mode == CaptureMode.SystemMix) await StartSystemAsync(receivers, cancellationToken);
        else if (route.ProcessId is int pid) await StartApplicationAsync(pid, receivers, cancellationToken);
        return GetToolRoute();
    }

    private async Task<object> SetVolumeToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var alias = arguments.GetProperty("receiver_id").GetString()!;
        var receiverId = ResolveToolReceiverId(alias);
        var percent = arguments.GetProperty("percent").GetInt32();
        await SetReceiverVolumeAsync(receiverId, percent, cancellationToken);
        return new { receiver_id = alias, volume = percent };
    }

    private object GetAlignmentTrimTool(JsonElement arguments)
    {
        Dictionary<string, string> known;
        lock (_legsGate)
        {
            known = _receivers.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
            foreach (var leg in _legs.Values) known[leg.Receiver.Id] = leg.Receiver.Name;
            foreach (var receiverId in _alignmentTrims.Keys) known.TryAdd(receiverId, "Saved receiver");
        }
        return new
        {
            receivers = known.OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase).Select(item => new
            {
                receiver_id = GetToolReceiverAlias(item.Key),
                receiver = item.Value == "Saved receiver" ? GetToolReceiverAlias(item.Key) : item.Value,
                alignment_trim_ms = GetReceiverAlignmentTrim(item.Key)
            }).ToArray()
        };
    }

    private object SetAlignmentTrimTool(JsonElement arguments)
    {
        var alias = arguments.GetProperty("receiver_id").GetString()!;
        var receiverId = ResolveToolReceiverId(alias);
        var delay = arguments.GetProperty("trim_ms").GetInt32();
        SetReceiverAlignmentTrim(receiverId, delay);
        return new { receiver_id = alias, alignment_trim_ms = delay };
    }

    private object GetSilenceStandbyTool()
    {
        var settings = GetSilenceStandbySettings();
        return new { enabled = settings.Enabled, after_seconds = settings.Seconds };
    }

    private object SetSilenceStandbyTool(JsonElement arguments)
    {
        var enabled = arguments.GetProperty("enabled").GetBoolean();
        var seconds = arguments.GetProperty("after_seconds").GetInt32();
        SetSilenceStandbySettings(enabled, seconds);
        return new { enabled, after_seconds = seconds };
    }

    private object SetBufferTarget(JsonElement arguments)
    {
        var before = _hub.Snapshot();
        var target = arguments.GetProperty("milliseconds").GetInt32();
        _hub.SetTarget(target);
        _pendingBufferVerification = (DateTimeOffset.UtcNow, before.Underruns);
        return new { before_ms = before.TargetMilliseconds, after_ms = target, verification_required = true, measure_after_seconds = 10 };
    }

    private async Task<StreamHealth> GetVerifiedHealthAsync(CancellationToken cancellationToken)
    {
        if (_pendingBufferVerification is { } pending)
        {
            var remaining = pending.ChangedUtc.AddSeconds(10) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            var verified = _hub.Snapshot().Underruns == pending.BaselineUnderruns && Coordinator.Route.State == StreamState.Streaming;
            Coordinator.MarkFixVerified(verified);
            _pendingBufferVerification = null;
        }
        return Coordinator.Health();
    }

    private async Task<object> ReconnectToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var receiver = arguments.GetProperty("receiver_id");
        if (!arguments.GetProperty("all").GetBoolean() && receiver.ValueKind == JsonValueKind.String)
            await ReconnectReceiverAsync(ResolveToolReceiverId(receiver.GetString()!), cancellationToken);
        else await ReconnectAsync(cancellationToken);
        return GetToolRoute();
    }

    private async Task<object> MeasureAcousticDelayToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var alias = arguments.GetProperty("receiver_id").GetString()!;
        var result = await MeasureAcousticDelayAsync(ResolveToolReceiverId(alias), cancellationToken);
        return new
        {
            receiver_id = alias,
            receiver = result.ReceiverName,
            median_delay_ms = result.MedianMilliseconds,
            samples_ms = result.DelaysMilliseconds,
            note = "Use this value in the browser extension. Microphone PCM stayed in memory and was not sent to OpenAI."
        };
    }

    private async Task<object> AlignGroupToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var aliases = arguments.GetProperty("receiver_ids").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var ids = aliases.Select(ResolveToolReceiverId).ToArray();
        var result = await AlignGroupAsync(ids, apply: true, cancellationToken);
        return new
        {
            measurements = result.Measurements.Select(item => new { receiver_id = GetToolReceiverAlias(item.ReceiverId), receiver = item.ReceiverName, median_delay_ms = item.MedianMilliseconds }),
            pairwise_skews = result.PairwiseSkews.Select(item => new
            {
                first_receiver_id = GetToolReceiverAlias(item.FirstReceiverId),
                second_receiver_id = GetToolReceiverAlias(item.SecondReceiverId),
                item.SkewMilliseconds,
                early_receiver_id = GetToolReceiverAlias(item.EarlyReceiverId)
            }),
            applied_trims_ms = result.ProposedTrimMilliseconds.ToDictionary(item => GetToolReceiverAlias(item.Key), item => item.Value, StringComparer.Ordinal),
            applied = true,
            note = "Calibration microphone PCM remained in memory and was not sent to OpenAI. Re-run alignment after a group restart or receiver move."
        };
    }

    private object EnableBrowserSync(int offset) { BrowserSyncActive = true; BrowserSyncOffsetMs = Math.Clamp(offset, 0, 10000); return new { active = true, offset_ms = BrowserSyncOffsetMs }; }
    private object DisableBrowserSync() { BrowserSyncActive = false; return new { active = false }; }
    private object GetToolRoute()
    {
        var route = Coordinator.Route;
        return new
        {
            route.StreamId,
            route.Mode,
            route.ProcessId,
            receiver_id = route.ReceiverId is null ? null : GetToolReceiverAlias(route.ReceiverId),
            route.ReceiverName,
            route.State,
            route.StartedUtc,
            destinations = route.Destinations.Select(item => new
            {
                receiver = new { id = GetToolReceiverAlias(item.Receiver.Id), item.Receiver.Name, item.Receiver.RequiresPassword },
                item.State,
                item.Volume,
                item.StartedUtc,
                item.LastError,
                item.AlignmentTrimMilliseconds
            }).ToArray()
        };
    }

    private string GetToolReceiverAlias(string receiverId)
    {
        lock (_toolAliasGate)
        {
            if (_toolAliasesByReceiverId.TryGetValue(receiverId, out var existing)) return existing;
            var alias = $"receiver-{_toolAliasesByReceiverId.Count + 1}";
            _toolAliasesByReceiverId.Add(receiverId, alias);
            _receiverIdsByToolAlias.Add(alias, receiverId);
            return alias;
        }
    }

    private string ResolveToolReceiverId(string alias)
    {
        lock (_toolAliasGate)
            return _receiverIdsByToolAlias.TryGetValue(alias, out var receiverId)
                ? receiverId
                : throw new InvalidOperationException("Receiver alias is not known. Refresh the receiver list and try again.");
    }

    private ReceiverInfo ResolveToolReceiver(string alias)
    {
        var receiverId = ResolveToolReceiverId(alias);
        return _receivers.SingleOrDefault(item => item.Id == receiverId) ?? throw new InvalidOperationException("Receiver is not currently discovered.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { ForceCleanup(); }
        _routeGate.Dispose();
    }

    /// <summary>Stops receiver sessions and releases all controller-owned resources.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("lifecycle", "Controller graceful shutdown started.");
        try { await StopAsync(cancellationToken).ConfigureAwait(false); } catch when (cancellationToken.IsCancellationRequested) { throw; }
        _capture.Dispose();
        await _pump.DisposeAsync().ConfigureAwait(false);
        await _raop.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        AppLog.Info("lifecycle", "Controller graceful shutdown completed.");
    }

    /// <summary>
    /// Emergency cleanup used only while the process is exiting. It must not
    /// wait for native capture or pipe threads that may already be wedged.
    /// </summary>
    public void ForceCleanup()
    {
        AppLog.Warning("lifecycle", "Controller force cleanup started.");
        _raop.ForceTerminate();
        ReceiverLeg[] legs;
        lock (_legsGate) { legs = _legs.Values.ToArray(); _legs.Clear(); }
        foreach (var leg in legs)
        {
            _hub.Unsubscribe(leg.Receiver.Id);
            _pump.RemoveLeg(leg.Receiver.Id);
            leg.Pipe.Abort();
        }
    }
}

public sealed class ReceiverAlignmentSettingChangedEventArgs(string receiverId, int milliseconds) : EventArgs
{
    public string ReceiverId { get; } = receiverId;
    public int Milliseconds { get; } = milliseconds;
}

public sealed class SilenceStandbySettingChangedEventArgs(bool enabled, int seconds) : EventArgs
{
    public bool Enabled { get; } = enabled;
    public int Seconds { get; } = seconds;
}
