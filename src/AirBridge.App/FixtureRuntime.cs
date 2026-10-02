using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.App;

/// <summary>Offline backends that exercise the real controller, fanout, pump and PCM pipes.</summary>
internal sealed class FixtureRuntime
{
    internal static IReadOnlyList<string> KnownScenarios { get; } =
        ["healthy", "no-receivers", "partial-failure", "pairing", "reconnect"];

    internal FixtureRuntime(string scenario)
    {
        if (!KnownScenarios.Contains(scenario, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown fixture scenario '{scenario}'. Available: {string.Join(", ", KnownScenarios)}.", nameof(scenario));
        Scenario = scenario;
        Raop = new(scenario);
    }

    internal string Scenario { get; }
    internal FixtureRaopClient Raop { get; }
    internal FixtureAudioCaptureService Capture { get; } = new();
    internal object Snapshot() => new { scenario = Scenario, capture = Capture.Snapshot(), receivers = Raop.Snapshot() };
}

internal sealed class FixtureAudioCaptureService : IAudioCaptureService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _producer;
    private long _blocksGenerated;

    internal Action<byte[], bool>? WritePcm { get; set; }

    public Task StartSystemAsync(CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_cancellation is not null) return Task.CompletedTask;
            if (WritePcm is null) throw new InvalidOperationException("Fixture PCM writer has not been attached.");
            _cancellation = new();
            _producer = ProduceAsync(_cancellation.Token);
        }
        return Task.CompletedTask;
    }

    public Task StartProcessTreeAsync(int processId, bool exclude = false, CancellationToken cancellationToken = default, TimeSpan? activationTimeout = null) =>
        StartSystemAsync(cancellationToken, activationTimeout);

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        var block = new byte[SharedAudioPump.BlockBytes];
        long frame = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SharedAudioPump.BlockMilliseconds));
            do
            {
                for (var offset = 0; offset < block.Length; offset += 4, frame++)
                {
                    var sample = (short)(2400 * Math.Sin(2 * Math.PI * 523.25 * frame / SharedAudioPump.SampleRate));
                    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(offset, 2), sample);
                    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(offset + 2, 2), sample);
                }
                cancellationToken.ThrowIfCancellationRequested();
                WritePcm!(block, true);
                Interlocked.Increment(ref _blocksGenerated);
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal object Snapshot()
    {
        lock (_gate) return new { running = _cancellation is not null, blocksGenerated = Interlocked.Read(ref _blocksGenerated) };
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void Dispose()
    {
        Stop();
        // The producer never blocks on I/O. Bound cleanup so fixtures cannot leave an owned task running.
        try { _producer?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
    }
}

internal sealed class FixtureRaopClient(string scenario) : IRaopClient
{
    private sealed class ReceiverSession(NamedPipeClientStream pipe, int volume)
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly NamedPipeClientStream Pipe = pipe;
        internal Task Reader = Task.CompletedTask;
        internal long BytesReceived;
        internal long SignalBlocks;
        internal int Volume = volume;
    }

    private readonly ConcurrentDictionary<string, ReceiverSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _paired = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _transitions = new(StringComparer.Ordinal);
    private bool _started;

    public event EventHandler<(string? ReceiverId, StreamState State, string? Error)>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReceiverInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started) throw new InvalidOperationException("Fixture host is not initialized.");
        IReadOnlyList<ReceiverInfo> receivers = scenario == "no-receivers" ? [] :
        [
            new("fixture-a", "Desk Speaker", "fixture", false, DateTimeOffset.UnixEpoch),
            new("fixture-b", "Media Room", "fixture", false, DateTimeOffset.UnixEpoch,
                RequiresPairing: scenario == "pairing" && !_paired.ContainsKey("fixture-b"), SupportsPairing: scenario == "pairing"),
            new("fixture-c", "Upstairs Speaker", "fixture", false, DateTimeOffset.UnixEpoch)
        ];
        return Task.FromResult(receivers);
    }

    public Task<JsonElement> BeginPairingAsync(string receiverId, bool controlPairing = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scenario != "pairing" || receiverId != "fixture-b") throw new InvalidOperationException("This fixture receiver does not need pairing.");
        return Task.FromResult(JsonSerializer.SerializeToElement(new { code_required = true }));
    }

    public Task<JsonElement> FinishPairingAsync(string receiverId, string pin, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scenario != "pairing" || receiverId != "fixture-b" || pin != "1234")
            throw new InvalidOperationException("Fixture pairing requires code 1234 for Media Room.");
        _paired[receiverId] = true;
        return Task.FromResult(JsonSerializer.SerializeToElement(new { paired = true }));
    }

    public Task<JsonElement> CancelPairingAsync(string receiverId, CancellationToken cancellationToken = default) => Completed();

    public async Task<JsonElement> StartStreamAsync(ReceiverInfo receiver, string pipeName, int initialVolume = 30, CancellationToken cancellationToken = default)
    {
        if (!_started) throw new InvalidOperationException("Fixture host is not initialized.");
        if (receiver.Id is not ("fixture-a" or "fixture-b" or "fixture-c")) throw new InvalidOperationException("Unknown fixture receiver.");
        await StopStreamAsync(receiver.Id, cancellationToken).ConfigureAwait(false);
        Emit(receiver.Id, StreamState.Connecting);
        if (receiver.Id == "fixture-b" && scenario == "partial-failure")
        {
            Emit(receiver.Id, StreamState.Failed, "Injected fixture connection failure.");
            return JsonSerializer.SerializeToElement(new { started = false });
        }
        if (receiver.Id == "fixture-b" && scenario == "pairing" && !_paired.ContainsKey(receiver.Id))
            throw new InvalidOperationException("Fixture receiver must be paired first.");

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var session = new ReceiverSession(pipe, Math.Clamp(initialVolume, 0, 100));
        try
        {
            await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
            _sessions[receiver.Id] = session;
            session.Reader = DrainAsync(receiver.Id, session);
            if (receiver.Id == "fixture-b" && scenario == "reconnect")
            {
                Emit(receiver.Id, StreamState.Reconnecting, "Injected fixture retry.");
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }
            Emit(receiver.Id, StreamState.Streaming);
            return JsonSerializer.SerializeToElement(new { started = true });
        }
        catch
        {
            await StopStreamAsync(receiver.Id, CancellationToken.None).ConfigureAwait(false);
            session.Cancellation.Dispose();
            pipe.Dispose();
            throw;
        }
    }

    private async Task DrainAsync(string receiverId, ReceiverSession session)
    {
        var buffer = new byte[SharedAudioPump.BlockBytes];
        try
        {
            while (!session.Cancellation.IsCancellationRequested)
            {
                var read = await session.Pipe.ReadAsync(buffer, session.Cancellation.Token).ConfigureAwait(false);
                if (read == 0) break;
                Interlocked.Add(ref session.BytesReceived, read);
                if (buffer.AsSpan(0, read).ContainsAnyExcept((byte)0)) Interlocked.Increment(ref session.SignalBlocks);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
    }

    public async Task<JsonElement> StopStreamAsync(string receiverId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(receiverId, out var session))
        {
            try
            {
                session.Cancellation.Cancel();
                session.Pipe.Dispose();
                try { await session.Reader.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            finally
            {
                session.Cancellation.Dispose();
                Emit(receiverId, StreamState.Idle);
            }
        }
        return JsonSerializer.SerializeToElement(new { stopped = true });
    }

    public async Task<JsonElement> StopAllStreamsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in _sessions.Keys) await StopStreamAsync(id, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { stopped = true });
    }

    public Task<JsonElement> SetVolumeAsync(string receiverId, int percent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(receiverId, out var session)) throw new InvalidOperationException("Fixture receiver is not connected.");
        Volatile.Write(ref session.Volume, Math.Clamp(percent, 0, 100));
        return Task.FromResult(JsonSerializer.SerializeToElement(new { volume = Math.Clamp(percent, 0, 100) }));
    }

    internal object[] Snapshot() => _sessions.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (object)new
    {
        id = item.Key,
        bytesReceived = Interlocked.Read(ref item.Value.BytesReceived),
        signalBlocks = Interlocked.Read(ref item.Value.SignalBlocks),
        volume = Volatile.Read(ref item.Value.Volume),
        connected = IsConnected(item.Value.Pipe),
        transitions = _transitions.TryGetValue(item.Key, out var transitions) ? transitions.ToArray() : []
    }).ToArray();

    private static bool IsConnected(NamedPipeClientStream pipe)
    {
        try { return pipe.IsConnected; }
        catch (ObjectDisposedException) { return false; }
    }

    private void Emit(string receiverId, StreamState state, string? error = null)
    {
        var history = _transitions.GetOrAdd(receiverId, _ => new());
        history.Enqueue(state.ToString());
        while (history.Count > 30) history.TryDequeue(out _);
        StateChanged?.Invoke(this, (receiverId, state, error));
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await StopAllStreamsAsync(cancellationToken).ConfigureAwait(false);
        _started = false;
    }

    public void ForceTerminate()
    {
        foreach (var item in _sessions)
        {
            if (!_sessions.TryRemove(item.Key, out var session)) continue;
            try
            {
                session.Cancellation.Cancel();
                session.Pipe.Dispose();
            }
            finally { session.Cancellation.Dispose(); }
        }
        _started = false;
    }

    private static Task<JsonElement> Completed() => Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
}
