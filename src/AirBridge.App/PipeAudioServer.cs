using System.IO.Pipes;
using System.Threading.Channels;

namespace AirBridge.App;

public sealed class PipeAudioServer : IAudioPipeEndpoint, IAsyncDisposable
{
    private readonly object _pipeGate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _serverTask;
    private PipeConnection? _connection;

    private sealed class PipeConnection(NamedPipeServerStream pipe)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        public int PendingWrites;
    }

    public PipeAudioServer() => PipeName = $"AirBridge.Pcm.{Environment.ProcessId}.{Guid.NewGuid():N}";

    public string PipeName { get; }
    public bool IsConnected { get { lock (_pipeGate) return _connection?.Pipe.IsConnected == true; } }
    public bool CanAcceptWrite
    {
        get
        {
            lock (_pipeGate)
            {
                try { return _connection is { Pipe.IsConnected: true } connection && Volatile.Read(ref connection.PendingWrites) == 0; }
                catch (ObjectDisposedException) { return false; }
            }
        }
    }

    public void Start()
    {
        if (_serverTask is not null) return;
        _cancellation = new();
        _serverTask = RunAsync(_cancellation.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, SharedAudioPump.BlockBytes * 4, SharedAudioPump.BlockBytes * 4);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                var connection = new PipeConnection(pipe);
                lock (_pipeGate) _connection = connection;
                await DrainWritesAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { /* RAOP reconnects by opening a new current-user pipe instance. */ }
            catch (Exception ex)
            {
                // A client failure must never permanently remove this session's pipe name.
                AppLog.Error("pcm-pipe", "Pipe client failed; reopening the server instance.", ex);
            }
            finally
            {
                lock (_pipeGate)
                {
                    if (_connection?.Pipe == pipe) _connection = null;
                }
                try { await pipe.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Error("pcm-pipe", "Could not dispose a pipe instance cleanly.", ex); }
            }
        }
    }

    private async Task DrainWritesAsync(PipeConnection connection, CancellationToken cancellationToken)
    {
        await foreach (var block in connection.Queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await connection.Pipe.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                await connection.Pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref connection.PendingWrites); }
        }
    }

    /// <summary>
    /// Queues one pump block without waiting on pipe I/O. A receiver that falls
    /// behind the bounded queue drops the excess block so it cannot stall siblings.
    /// Transient scheduler jitter must not tear down the RAOP session.
    /// </summary>
    public bool TryWrite(byte[] pcm, bool tolerateBackpressure = false)
    {
        PipeConnection? connection;
        lock (_pipeGate) connection = _connection;
        try
        {
            if (connection?.Pipe.IsConnected != true) return false;
        }
        catch (ObjectDisposedException) { return false; }
        Interlocked.Increment(ref connection.PendingWrites);
        if (connection.Queue.Writer.TryWrite(pcm)) return true;
        Interlocked.Decrement(ref connection.PendingWrites);
        if (tolerateBackpressure) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null) return;
        Abort();
        if (_serverTask is not null)
        {
            try { await _serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
        _cancellation = null;
        _serverTask = null;
    }

    /// <summary>Ends the current client leg while leaving the server available for a live-edge reconnect.</summary>
    public void DisconnectClient()
    {
        PipeConnection? connection;
        lock (_pipeGate) connection = _connection;
        if (connection is not null) DisconnectConnection(connection);
    }

    private void DisconnectConnection(PipeConnection connection)
    {
        lock (_pipeGate)
        {
            if (ReferenceEquals(_connection, connection)) _connection = null;
        }
        connection.Queue.Writer.TryComplete();
        try { connection.Pipe.Dispose(); } catch { }
    }

    /// <summary>Cancels pipe I/O without waiting for a blocked server task.</summary>
    public void Abort()
    {
        try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        DisconnectClient();
    }
}
