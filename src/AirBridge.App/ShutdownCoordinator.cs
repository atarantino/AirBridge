namespace AirBridge.App;

/// <summary>
/// Runs application cleanup once, gives it a fixed grace period, and invokes a
/// non-blocking emergency cleanup when graceful shutdown cannot finish.
/// </summary>
public sealed class ShutdownCoordinator
{
    private readonly Func<CancellationToken, Task> _gracefulShutdown;
    private readonly Action _forceCleanup;
    private readonly TimeSpan _gracePeriod;
    private readonly object _gate = new();
    private Task<bool>? _completion;

    public ShutdownCoordinator(Func<CancellationToken, Task> gracefulShutdown, Action forceCleanup, TimeSpan gracePeriod)
    {
        ArgumentNullException.ThrowIfNull(gracefulShutdown);
        ArgumentNullException.ThrowIfNull(forceCleanup);
        if (gracePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        _gracefulShutdown = gracefulShutdown;
        _forceCleanup = forceCleanup;
        _gracePeriod = gracePeriod;
    }

    /// <returns><see langword="true"/> when graceful cleanup completed; otherwise <see langword="false"/>.</returns>
    public Task<bool> ShutdownAsync()
    {
        lock (_gate) return _completion ??= RunAsync();
    }

    private async Task<bool> RunAsync()
    {
        using var cancellation = new CancellationTokenSource(_gracePeriod);
        Task gracefulTask;
        try
        {
            // Native audio cleanup is allowed to block, so it must never run on
            // the WinForms thread or prevent the deadline from being enforced.
            gracefulTask = Task.Run(() => _gracefulShutdown(cancellation.Token));
            await gracefulTask.WaitAsync(_gracePeriod).ConfigureAwait(false);
            return true;
        }
        catch
        {
            cancellation.Cancel();
            try { _forceCleanup(); } catch { }
            return false;
        }
    }
}
