using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.App;

public sealed class PythonRaopClient : IRaopClient, IAsyncDisposable
{
    private sealed record PendingRequest(Process Process, TaskCompletionSource<JsonElement> Completion);
    private readonly ConcurrentDictionary<string, PendingRequest> _requests = new();
    private readonly ConcurrentDictionary<Process, byte> _closedResponseStreams = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly string? _runtimeOverride;
    private readonly string? _hostPathOverride;
    private Process? _process;
    private CancellationTokenSource? _cancellation;
    private Task? _outputTask;
    private Task? _errorTask;

    public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged;

    public PythonRaopClient() { }
    internal PythonRaopClient(string runtime, string hostPath)
    {
        _runtimeOverride = runtime;
        _hostPathOverride = hostPath;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (_process is { HasExited: false }) return;
        if (_process is { } previous)
            await CleanupProcessAsync(previous).ConfigureAwait(false);
        var basePath = AppContext.BaseDirectory;
        var (runtime, runtimeArguments) = FindRuntime(basePath, _runtimeOverride ?? Environment.GetEnvironmentVariable("AIRBRIDGE_PYTHON"));
        var host = _hostPathOverride ?? Path.Combine(basePath, "RaopHost", "host.py");
        if (runtimeArguments is not null && !File.Exists(host)) throw new FileNotFoundException("The bundled RAOP host was not found.", host);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(runtime, runtimeArguments is null ? string.Empty : $"-u \"{host}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(host)!
            },
            EnableRaisingEvents = true
        };
        ConfigureHostEnvironment(_process.StartInfo);
        if (!_process.Start()) throw new InvalidOperationException("Unable to launch the RAOP host.");
        AppLog.Info("raop-host", $"Started RAOP host process; pid={_process.Id}; runtime={Path.GetFileName(runtime)}.");
        _cancellation = new();
        _outputTask = ReadOutputAsync(_process, _cancellation.Token);
        _errorTask = DrainErrorsAsync(_process, _cancellation.Token);
        try { await SendAsync("ping", new { }, cancellationToken).ConfigureAwait(false); }
        catch
        {
            ForceTerminate();
            await CleanupProcessAsync(_process).ConfigureAwait(false);
            throw;
        }
        }
        finally { _lifecycleGate.Release(); }
    }

    public Task<JsonElement> PingAsync(CancellationToken cancellationToken = default) =>
        SendAsync("ping", new { }, cancellationToken);

    internal static void ConfigureHostEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment[RuntimeProfile.DataDirectoryVariable] = RuntimeProfile.DataDirectory;
        startInfo.Environment["AIRBRIDGE_RUN_ID"] = RuntimeProfile.RunId;
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var name in new[] { "OPENAI_API_KEY", "AIRBRIDGE_MODEL_EVAL_KEY", "AIRBRIDGE_RUN_HARDWARE_TESTS", "AIRBRIDGE_MODEL_EVALS", "AIRBRIDGE_RUN_MODEL_EVALS" })
            startInfo.Environment.Remove(name);
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

    public Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default) =>
        SendAsync("stop", new { receiver_id = receiverId }, cancellationToken);
    public Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default) => SendAsync("stop_all", new { }, cancellationToken);

    public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default) =>
        SendAsync("set_volume", new { receiver_id = receiverId, percent }, cancellationToken);

    private async Task<JsonElement> SendAsync(string command, object arguments, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is not { HasExited: false }) throw new InvalidOperationException("RAOP host is not running.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[requestId] = new(process, completion);
        if (_closedResponseStreams.ContainsKey(process))
        {
            _requests.TryRemove(requestId, out _);
            throw new InvalidOperationException("The RAOP host response stream is closed.");
        }
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
        values["request_id"] = requestId;
        values["command"] = command;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        AppLog.Info("raop-command", $"Sending {command}; operation={requestId}; run={RuntimeProfile.RunId}.");
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(values).AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            AppLog.Info("raop-command", $"Completed {command}; operation={requestId}; duration_ms={elapsed.ElapsedMilliseconds}.");
            return result;
        }
        catch (TimeoutException)
        {
            _requests.TryRemove(requestId, out _);
            AppLog.Error("raop-command", $"Timed out waiting for {command}; operation={requestId}.");
            throw new TimeoutException($"RAOP host did not answer the {command} command within 15 seconds.");
        }
        finally { _requests.TryRemove(requestId, out _); }
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
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
                if (root.TryGetProperty("request_id", out var requestIdElement) && requestIdElement.GetString() is { } requestId &&
                    _requests.TryGetValue(requestId, out var pending) && ReferenceEquals(pending.Process, process))
                {
                    var accepted = root.GetProperty("ok").GetBoolean();
                    var result = accepted ? root.GetProperty("result").Clone() : default;
                    var error = accepted ? null : root.GetProperty("error").GetString() ?? "RAOP host command failed.";
                    if (!_requests.TryRemove(requestId, out pending)) continue;
                    if (accepted) pending.Completion.TrySetResult(result);
                    else
                    {
                        AppLog.Error("raop-host", error!);
                        pending.Completion.TrySetException(new InvalidOperationException(error));
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
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                // Raw malformed output may contain credentials or audio. Keep
                // the reader alive without putting that payload into logs.
                AppLog.Warning("raop-host", $"Ignored malformed host output ({ex.GetType().Name}).");
            }
        }
        }
        catch (Exception ex)
        {
            AppLog.Warning("raop-host", $"RAOP response reader stopped ({ex.GetType().Name}).");
        }
        finally
        {
            // Process.Exited can occur before buffered final JSON is read.
            // Resolve requests only after draining stdout or a reader failure.
            _closedResponseStreams.TryAdd(process, 0);
            FailPendingRequests(process, "The RAOP host closed its response stream before responding.");
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

    private void FailPendingRequests(Process process, string reason)
    {
        foreach (var request in _requests)
            if (ReferenceEquals(request.Value.Process, process) && _requests.TryRemove(request.Key, out var pending))
                pending.Completion.TrySetException(new InvalidOperationException(reason));
    }

    internal static (string Runtime, string? Arguments) FindRuntime(string basePath, string? runtimeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(runtimeOverride))
        {
            var explicitRuntime = Path.GetFullPath(runtimeOverride);
            if (!File.Exists(explicitRuntime)) throw new FileNotFoundException("AIRBRIDGE_PYTHON must name an existing Python executable.", explicitRuntime);
            return (explicitRuntime, "python");
        }
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
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        await CleanupProcessAsync(process).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task CleanupProcessAsync(Process process)
    {
        var cancellation = _cancellation;
        var output = _outputTask;
        var errors = _errorTask;
        if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
        await AwaitReaderAsync(output).ConfigureAwait(false);
        await AwaitReaderAsync(errors).ConfigureAwait(false);
        FailPendingRequests(process, "The RAOP host was stopped before responding.");
        _closedResponseStreams.TryRemove(process, out _);
        if (ReferenceEquals(_process, process))
        {
            _process = null;
            _cancellation = null;
            _outputTask = null;
            _errorTask = null;
        }
        process.Dispose();
        cancellation?.Dispose();
    }

    public void ForceTerminate()
    {
        var process = _process;
        try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        if (process is null) return;
        FailPendingRequests(process, "The RAOP host was terminated during application shutdown.");
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
                await CleanupProcessAsync(process).ConfigureAwait(false);
            }
        }
        _sendGate.Dispose();
    }
}
