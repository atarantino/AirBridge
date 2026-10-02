using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.App;

public sealed partial class MainForm
{
    private SessionManifest? _sessionManifest;
    private DebugPipeServer? _debugServer;
    private string? _sessionDirectory;
    private readonly Dictionary<string, string> _debugReceiverAliases = new(StringComparer.Ordinal);
    private System.Windows.Forms.Timer? _debugQuitTimer;

    internal void StartDebugSession(LaunchOptions options)
    {
        _sessionDirectory = options.SessionFile is { } file ? Path.GetDirectoryName(file) : null;
        _sessionManifest = new(options);
        _sessionManifest.Update("initializing");
        if (options.DebugPipe is not { } pipe) return;
        _ = Handle;
        _debugServer = new(pipe, DispatchDebugRequestAsync);
        _controller.Coordinator.RouteChanged += (_, route) =>
            _debugServer.Events.Add("route", $"Route state: {route.State}.");
        _controller.PlaybackChanged += (_, _) => _debugServer.Events.Add("playback", "Receiver playback changed.");
        _debugServer.Start();
        _debugServer.Events.Add("starting", "Application session started.");
    }

    internal void CompleteDebugSession()
    {
        _sessionManifest?.Update("stopped");
        _debugQuitTimer?.Dispose();
        _debugServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private Task<object?> DispatchDebugRequestAsync(DebugRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (IsDisposed || Disposing)
        {
            registration.Dispose();
            return Task.FromException<object?>(new DebugProtocolException("not_ready", "The application is shutting down."));
        }
        try
        {
            BeginInvoke(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(await HandleDebugRequestAsync(request, cancellationToken));
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { registration.Dispose(); }
            });
        }
        catch (InvalidOperationException)
        {
            registration.Dispose();
            completion.TrySetException(new DebugProtocolException("not_ready", "The application window is unavailable."));
        }
        return completion.Task;
    }

    private async Task<object?> HandleDebugRequestAsync(DebugRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "state") return DebugState();
        if (request.Method == "events") return _debugServer!.Events.Snapshot(OptionalInt64(request.Parameters, "afterSequence", 0));
        if (request.Method == "shutdown")
        {
            _sessionManifest?.Update("stopping");
            if (_debugQuitTimer is null)
            {
                _debugQuitTimer = new() { Interval = 200 };
                _debugQuitTimer.Tick += (_, _) => { _debugQuitTimer.Stop(); RequestQuit(); };
            }
            _debugQuitTimer.Start();
            return new { stopping = true };
        }
        if (_sessionManifest?.Ready != true) throw new DebugProtocolException("not_ready", "Application initialization has not completed.");
        switch (request.Method)
        {
            case "wait-for-state":
                var expected = RequiredString(request.Parameters, "state");
                if (!Enum.TryParse<StreamState>(expected, true, out var target) || !Enum.IsDefined(target))
                    throw new DebugProtocolException("invalid_params", "state must be a valid stream state.");
                var timeout = Math.Clamp(OptionalInt(request.Parameters, "timeoutMs", 10000), 1, 15000);
                var deadline = Environment.TickCount64 + timeout;
                while (_controller.Coordinator.Route.State != target)
                {
                    if (Environment.TickCount64 >= deadline) throw new DebugProtocolException("timeout", $"The route did not reach {target}.");
                    await Task.Delay(50, cancellationToken);
                }
                return DebugState();
            case "snapshot":
                return CaptureDebugSnapshot(request.Parameters);
            case "start":
                RequireFixture();
                var ids = ReadReceiverIds(request.Parameters);
                if (ids.Length == 0) throw new DebugProtocolException("invalid_params", "Select at least one fixture receiver.");
                var selected = ids.Select(id => _controller.Receivers.SingleOrDefault(receiver => receiver.Id == id)
                    ?? throw new DebugProtocolException("invalid_params", "Unknown fixture receiver.")).ToArray();
                if (selected.Any(receiver => receiver.RequiresPairing))
                    throw new DebugProtocolException("pairing_required", "Pair the fixture receiver before starting playback.");
                if (selected.Any(receiver => !receiver.CanConnect))
                    throw new DebugProtocolException("invalid_params", "A selected fixture receiver is unavailable.");
                foreach (var receiver in _controller.Receivers) SetReceiverSelected(receiver.Id, ids.Contains(receiver.Id, StringComparer.Ordinal));
                _sourceMode.SelectedIndex = 0;
                await StartSelectedAsync(propagateErrors: true, cancellationToken);
                return DebugState();
            case "stop":
                RequireFixture();
                await _controller.StopAsync(cancellationToken);
                UpdateTelemetry();
                return DebugState();
            case "volume":
                RequireFixture();
                var receiverId = RequiredString(request.Parameters, "receiverId");
                if (!_controller.Receivers.Any(receiver => receiver.Id == receiverId))
                    throw new DebugProtocolException("invalid_params", "Unknown fixture receiver.");
                var volume = OptionalInt(request.Parameters, "percent", -1);
                if (volume is < 0 or > 100) throw new DebugProtocolException("invalid_params", "percent must be in 0–100.");
                await ChangeReceiverVolumeAsync(receiverId, volume, propagateErrors: true, cancellationToken);
                return DebugState();
            case "pair":
                RequireFixture();
                var pairingReceiver = RequiredString(request.Parameters, "receiverId");
                var code = RequiredString(request.Parameters, "code");
                if (!_controller.Receivers.Any(receiver => receiver.Id == pairingReceiver && receiver.RequiresPairing && receiver.SupportsPairing))
                    throw new DebugProtocolException("invalid_params", "Select a fixture receiver requiring pairing.");
                await _controller.BeginPairingAsync(pairingReceiver, cancellationToken: cancellationToken);
                await _controller.PairReceiverAsync(pairingReceiver, code, cancellationToken);
                RebuildReceiverRows(_controller.Receivers);
                return DebugState();
            default:
                throw new DebugProtocolException("unknown_method", "Unknown debug method.");
        }
    }

    private object DebugState()
    {
        var route = _controller.Coordinator.Route;
        var verification = _controller.LastBufferVerification;
        if (verification is not null)
            verification = verification with { Receivers = verification.Receivers.ToDictionary(item => DebugReceiverAlias(item.Key), item => item.Value, StringComparer.Ordinal) };
        var health = _controller.Coordinator.Health();
        return new
        {
            ready = _sessionManifest?.Ready == true,
            mode = _fixture is null ? "live" : "fixture",
            scenario = _fixture?.Scenario,
            session = _sessionManifest?.Snapshot(),
            route = new { route.StreamId, state = route.State, route.Mode, route.StartedUtc },
            health = new { health.Buffer, health.LastFixVerified, verification },
            receivers = _controller.Receivers.Select(receiver => new
            {
                id = DebugReceiverAlias(receiver.Id), name = AgentActivitySanitizer.Sanitize(receiver.Name), receiver.RequiresPairing, receiver.CanConnect
            }).ToArray(),
            playback = _controller.ReceiverPlayback.Select(playback => new
            {
                id = DebugReceiverAlias(playback.Receiver.Id), playback.State, playback.Volume,
                lastError = AgentActivitySanitizer.Sanitize(playback.LastError), playback.AlignmentTrimMilliseconds
            }).ToArray(),
            fixture = _fixture?.Snapshot()
        };
    }

    private string DebugReceiverAlias(string receiverId)
    {
        if (_fixture is not null) return receiverId;
        if (!_debugReceiverAliases.TryGetValue(receiverId, out var alias))
            _debugReceiverAliases[receiverId] = alias = $"receiver-{_debugReceiverAliases.Count + 1}";
        return alias;
    }

    private object CaptureDebugSnapshot(JsonElement parameters)
    {
        var path = Path.GetFullPath(RequiredString(parameters, "path"));
        if (!WithinDirectory(path, RuntimeProfile.DataDirectory) && !(_sessionDirectory is { } directory && WithinDirectory(path, directory)))
            throw new DebugProtocolException("invalid_params", "Snapshots must be saved inside this session's profile or evidence directory.");
        var surface = RequiredString(parameters, "surface");
        Form form = surface switch
        {
            "flyout" => _trayFlyout,
            "dashboard" => this,
            _ => throw new DebugProtocolException("invalid_params", "surface must be flyout or dashboard.")
        };
        var visible = form.Visible;
        var opacity = form.Opacity;
        var autoHide = _trayFlyout.AutoHide;
        try
        {
            form.Opacity = 1;
            if (form == _trayFlyout) _trayFlyout.AutoHide = false;
            form.Show();
            foreach (var row in form.Controls.OfType<ReceiverRowControl>()) row.ApplyTextScale();
            form.PerformLayout();
            if (form == _trayFlyout) _trayFlyout.SettleSnapshotLayout();
            else SettleSnapshotLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            SnapshotEvidence.WriteLayout(form, path);
            return new { surface, path, bitmap.Width, bitmap.Height };
        }
        finally
        {
            form.Opacity = opacity;
            if (!visible) form.Hide();
            if (form == _trayFlyout) _trayFlyout.AutoHide = autoHide;
        }
    }

    private static bool WithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void RequireFixture()
    {
        if (_fixture is null) throw new DebugProtocolException("fixture_required", "Debug mutations are available only in a fixture session.");
    }

    private string[] ReadReceiverIds(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("receiverIds", out var values))
            return _controller.Receivers.Select(receiver => receiver.Id).ToArray();
        if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
            throw new DebugProtocolException("invalid_params", "receiverIds must be an array of nonempty strings.");
        return values.EnumerateArray().Select(value => value.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string RequiredString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new DebugProtocolException("invalid_params", $"{name} must be a nonempty string.");

    private static int OptionalInt(JsonElement parameters, string name, int fallback) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw new DebugProtocolException("invalid_params", $"{name} must be an integer.") : fallback;

    private static long OptionalInt64(JsonElement parameters, string name, long fallback) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw new DebugProtocolException("invalid_params", $"{name} must be an integer.") : fallback;
}
