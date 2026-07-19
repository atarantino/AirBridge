using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.App;

public sealed class PythonRaopClient : IRaopClient, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Process? _process;
    private CancellationTokenSource? _cancellation;
    private Task? _outputTask;
    private Task? _errorTask;

    public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false }) return;
        var basePath = AppContext.BaseDirectory;
        var (runtime, runtimeArguments) = FindRuntime(basePath);
        var host = Path.Combine(basePath, "RaopHost", "host.py");
        if (runtimeArguments is not null && !File.Exists(host)) throw new FileNotFoundException("The bundled RAOP host was not found.", host);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(runtime, runtimeArguments is null ? string.Empty : $"-u \"{host}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(host)!
            },
            EnableRaisingEvents = true
        };
        if (!_process.Start()) throw new InvalidOperationException("Unable to launch the RAOP host.");
        AppLog.Info("raop-host", $"Started RAOP host process; pid={_process.Id}; runtime={Path.GetFileName(runtime)}.");
        _cancellation = new();
        _outputTask = ReadOutputAsync(_process, _cancellation.Token);
        _errorTask = DrainErrorsAsync(_process, _cancellation.Token);
        await SendAsync("ping", new { }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("discover", new { timeout = 5 }, cancellationToken);
        return result.EnumerateArray().Select(item => new ReceiverInfo(
            item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!, "local-network-receiver",
            item.GetProperty("requires_password").GetBoolean(), DateTimeOffset.UtcNow,
            item.GetProperty("device_type").GetString() ?? "speaker",
            item.GetProperty("requires_pairing").GetBoolean(),
            item.GetProperty("supports_pairing").GetBoolean(),
            item.GetProperty("supports_power_control").GetBoolean(),
            item.GetProperty("requires_control_pairing").GetBoolean(),
            item.TryGetProperty("connection_issue", out var issue) && issue.ValueKind == JsonValueKind.String
                ? issue.GetString()
                : null)).ToArray();
    }

    public Task<JsonElement> BeginPairingAsync(string receiverId, bool controlPairing = false, CancellationToken cancellationToken = default) =>
        SendAsync("begin_pairing", new { receiver_id = receiverId, pairing_kind = controlPairing ? "control" : "raop" }, cancellationToken);
    public Task<JsonElement> FinishPairingAsync(string receiverId, string pin, CancellationToken cancellationToken = default) =>
        SendAsync("finish_pairing", new { receiver_id = receiverId, pin }, cancellationToken);
    public Task<JsonElement> CancelPairingAsync(string receiverId, CancellationToken cancellationToken = default) =>
        SendAsync("cancel_pairing", new { receiver_id = receiverId }, cancellationToken);
    public Task<JsonElement> SleepAsync(string receiverId, CancellationToken cancellationToken = default) =>
        SendAsync("sleep", new { receiver_id = receiverId }, cancellationToken);

    public Task<JsonElement> StartStreamAsync(ReceiverInfo receiver, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default) =>
        SendAsync("start", new { receiver_id = receiver.Id, receiver_name = receiver.Name, pipe_name = pipeName, initial_volume = Math.Clamp(initialVolume, 0, 100) }, cancellationToken);

    /// <summary>Stops all sessions. Retained for compatibility with the original single-receiver controller.</summary>
    public Task<JsonElement> StopStreamAsync(CancellationToken cancellationToken = default) => SendAsync("stop", new { }, cancellationToken);
    public Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default) =>
        SendAsync("stop", new { receiver_id = receiverId }, cancellationToken);
    public Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default) => SendAsync("stop_all", new { }, cancellationToken);

    /// <summary>Targets the sole connected session. Retained for compatibility with the original controller.</summary>
    public Task<JsonElement> SetVolumeAsync(int percent, CancellationToken cancellationToken = default) => SendAsync("set_volume", new { percent }, cancellationToken);
    public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default) =>
        SendAsync("set_volume", new { receiver_id = receiverId, percent }, cancellationToken);
    public Task<JsonElement> PlayDiagnosticToneAsync(string receiverName, double seconds, CancellationToken cancellationToken = default) =>
        SendAsync("diagnostic_tone", new { receiver_name = receiverName, seconds = Math.Clamp(seconds, 0.1, 10.0) }, cancellationToken);

    private async Task<JsonElement> SendAsync(string command, object arguments, CancellationToken cancellationToken)
    {
        if (_process is not { HasExited: false }) throw new InvalidOperationException("RAOP host is not running.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[requestId] = completion;
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
        values["request_id"] = requestId;
        values["command"] = command;
        AppLog.Info("raop-command", $"Sending {command}.");
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(values).AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
        }
        catch
        {
            _requests.TryRemove(requestId, out _);
            throw;
        }
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            AppLog.Info("raop-command", $"Completed {command}.");
            return result;
        }
        catch (TimeoutException)
        {
            _requests.TryRemove(requestId, out _);
            AppLog.Error("raop-command", $"Timed out waiting for {command}.");
            throw new TimeoutException($"RAOP host did not answer the {command} command within 15 seconds.");
        }
        finally { _requests.TryRemove(requestId, out _); }
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try { line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested) { break; }
            if (line is null) break;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("request_id", out var requestIdElement) && requestIdElement.GetString() is { } requestId && _requests.TryRemove(requestId, out var completion))
                {
                    if (root.GetProperty("ok").GetBoolean()) completion.TrySetResult(root.GetProperty("result").Clone());
                    else
                    {
                        var error = root.GetProperty("error").GetString() ?? "RAOP host command failed.";
                        AppLog.Error("raop-host", error);
                        completion.TrySetException(new InvalidOperationException(error));
                    }
                    continue;
                }
                if (root.TryGetProperty("event", out var eventType) && eventType.GetString() == "state")
                {
                    var stateText = root.GetProperty("state").GetString();
                    var state = Enum.TryParse<StreamState>(stateText, true, out var parsed) ? parsed : StreamState.Failed;
                    var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
                    var receiverId = root.TryGetProperty("receiver_id", out var receiverIdElement) ? receiverIdElement.GetString() : null;
                    AppLog.Info("raop-state", $"Receiver state changed to {state}{(error is null ? "." : $"; {error}")}");
                    StateChanged?.Invoke(this, (receiverId, state, error));
                }
            }
            catch (JsonException ex) { AppLog.Error("raop-host", $"Ignored malformed host output: {line}", ex); }
        }
    }

    private static async Task DrainErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                AppLog.Warning("raop-stderr", line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static (string Runtime, string? Arguments) FindRuntime(string basePath)
    {
        var bundled = Path.Combine(basePath, "RaopHost", "AirBridge.RaopHost.exe");
        if (File.Exists(bundled)) return (bundled, null);
        var current = new DirectoryInfo(basePath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate)) return (candidate, "python");
            current = current.Parent;
        }
        return ("python3.12.exe", "python");
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null) return;
        if (!process.HasExited)
        {
            await StopAllStreamsAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        AppLog.Info("raop-host", $"RAOP host exited; code={(process.HasExited ? process.ExitCode : -1)}.");
        if (_cancellation is not null) await _cancellation.CancelAsync().ConfigureAwait(false);
        await AwaitReaderAsync(_outputTask).ConfigureAwait(false);
        await AwaitReaderAsync(_errorTask).ConfigureAwait(false);
        if (ReferenceEquals(_process, process)) _process = null;
        process.Dispose();
        _cancellation?.Dispose();
        _cancellation = null;
        _outputTask = null;
        _errorTask = null;
    }

    public void ForceTerminate()
    {
        var process = _process;
        try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var request in _requests.Values)
            request.TrySetException(new OperationCanceledException("The RAOP host was terminated during application shutdown."));
        _requests.Clear();
        if (process is null) return;
        try { process.StandardInput.Close(); } catch { }
        try
        {
            if (!process.HasExited)
            {
                AppLog.Warning("raop-host", "Force-terminating RAOP host process tree.");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) { AppLog.Error("raop-host", "Failed to force-terminate RAOP host.", ex); }
    }

    private static async Task AwaitReaderAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try { await ShutdownAsync(cancellation.Token).ConfigureAwait(false); }
        catch
        {
            ForceTerminate();
            var process = _process;
            if (process is not null)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                process.Dispose();
                _process = null;
            }
            _cancellation?.Dispose();
            _cancellation = null;
        }
        _sendGate.Dispose();
    }
}
