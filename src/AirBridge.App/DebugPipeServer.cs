using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AirBridge.Core;

namespace AirBridge.App;

internal sealed record DebugRequest(string Id, string Method, [property: JsonPropertyName("params")] JsonElement Parameters);
internal sealed class DebugProtocolException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed record DebugSessionEvent(long Sequence, DateTimeOffset Timestamp, string Kind, string Message, long? DurationMs, string? OperationId);

internal sealed class DebugEventStore
{
    private readonly object _gate = new();
    private readonly Queue<DebugSessionEvent> _events = new();
    private long _sequence;

    internal void Add(string kind, string message, long? durationMs = null, string? operationId = null)
    {
        lock (_gate)
        {
            _events.Enqueue(new(++_sequence, DateTimeOffset.UtcNow, kind,
                AgentActivitySanitizer.Sanitize(RuntimeLog.Redact(message)), durationMs,
                operationId is null ? null : AgentActivitySanitizer.Sanitize(operationId)));
            while (_events.Count > 250) _events.Dequeue();
        }
    }

    internal IReadOnlyList<DebugSessionEvent> Snapshot(long afterSequence = 0)
    {
        lock (_gate) return _events.Where(item => item.Sequence > afterSequence).ToArray();
    }
}

/// <summary>Explicitly enabled, bounded current-user debug transport. One request per connection.</summary>
internal sealed class DebugPipeServer : IAsyncDisposable
{
    private const int MaximumRequestCharacters = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
        { "state", "events", "wait-for-state", "start", "stop", "volume", "pair", "snapshot", "shutdown" };
    private readonly Func<DebugRequest, CancellationToken, Task<object?>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _clients = [];
    private readonly SemaphoreSlim _slots = new(8, 8);
    private Task? _acceptTask;
    private int _disposeStarted;

    internal DebugPipeServer(string pipeName, Func<DebugRequest, CancellationToken, Task<object?>> handler)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128 || pipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("Debug pipe name must use 1–128 ASCII letters, digits, dots, hyphens or underscores.", nameof(pipeName));
        PipeName = pipeName;
        _handler = handler;
    }

    internal string PipeName { get; }
    internal DebugEventStore Events { get; } = new();

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (_acceptTask is not null) return;
        _acceptTask = AcceptAsync(_shutdown.Token);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var ownsSlot = false;
            try
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                ownsSlot = true;
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var client = HandleClientAsync(pipe, cancellationToken);
                lock (_gate) _clients.Add(client);
                _ = RemoveCompletedClientAsync(client);
                pipe = null;
                ownsSlot = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException ex)
            {
                Events.Add("transport-error", ex.Message);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                pipe?.Dispose();
                if (ownsSlot) _slots.Release();
            }
        }
    }

    private async Task RemoveCompletedClientAsync(Task client)
    {
        try { await client.ConfigureAwait(false); }
        catch (Exception ex) { Events.Add("transport-error", ex.Message); }
        finally
        {
            lock (_gate) _clients.Remove(client);
            _slots.Release();
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken shutdownToken)
    {
        await using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var token = deadline.Token;
            string? requestId = null;
            string? method = null;
            var timer = Stopwatch.StartNew();
            object reply;
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
                var line = await ReadBoundedLineAsync(reader, token).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<DebugRequest>(line, JsonOptions)
                    ?? throw new DebugProtocolException("invalid_request", "A JSON object is required.");
                requestId = request.Id;
                method = request.Method;
                if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100)
                    throw new DebugProtocolException("invalid_request", "id must contain 1–100 characters.");
                if (method is null || !Methods.Contains(method))
                    throw new DebugProtocolException("unknown_method", "Unknown debug method.");
                if (request.Parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                    throw new DebugProtocolException("invalid_params", "params must be a JSON object.");
                Events.Add("operation-started", method, operationId: requestId);
                var result = await _handler(request, token).WaitAsync(token).ConfigureAwait(false);
                Events.Add("operation-completed", method, timer.ElapsedMilliseconds, requestId);
                reply = new { id = requestId, ok = true, result };
            }
            catch (DebugProtocolException ex)
            {
                Events.Add("operation-failed", $"{method ?? "request"}: {ex.Code}", timer.ElapsedMilliseconds, requestId);
                reply = Error(requestId, ex.Code, AgentActivitySanitizer.Sanitize(RuntimeLog.Redact(ex.Message)));
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                reply = Error(requestId, "invalid_json", "The request must contain valid UTF-8 JSON.");
            }
            catch (OperationCanceledException)
            {
                Events.Add("operation-failed", $"{method ?? "request"}: timeout", timer.ElapsedMilliseconds, requestId);
                reply = Error(requestId, "timeout", "The debug operation exceeded its deadline or the session is stopping.");
            }
            catch (Exception ex)
            {
                Events.Add("operation-failed", $"{method ?? "request"}: {ex.GetType().Name}", timer.ElapsedMilliseconds, requestId);
                AppLog.Error("debug", "Debug operation failed.", ex);
                reply = Error(requestId, "operation_failed", "Debug operation failed; inspect the session log.");
            }
            try
            {
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true);
                using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, JsonOptions).AsMemory(), writeDeadline.Token).ConfigureAwait(false);
                await writer.FlushAsync(writeDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    private static object Error(string? id, string code, string message) => new { id, ok = false, error = new { code, message } };

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var characters = new char[1024];
        var received = 0;
        var oversized = false;
        while (true)
        {
            var read = await reader.ReadAsync(characters.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new DebugProtocolException("invalid_request", "Terminate the request with a newline.");
            var newline = Array.IndexOf(characters, '\n', 0, read);
            var count = newline < 0 ? read : newline;
            received += count;
            oversized |= received > MaximumRequestCharacters;
            // Drain a modest oversized line before replying so a duplex client whose
            // WriteAsync is still pending can finish writing and then read the error.
            // A much larger or unterminated line is disconnected within the same deadline.
            if (received > MaximumRequestCharacters * 2)
                throw new DebugProtocolException("request_too_large", "The debug request exceeds 32 KiB characters.");
            if (!oversized) result.Append(characters, 0, count);
            if (newline >= 0)
            {
                if (oversized) throw new DebugProtocolException("request_too_large", "The debug request exceeds 32 KiB characters.");
                return result.ToString().TrimEnd('\r');
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        Task[] clients;
        lock (_gate) clients = _clients.ToArray();
        try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        // Completion callbacks own the remaining slots. Keep their semaphore alive until they return.
        _shutdown.Dispose();
    }
}
