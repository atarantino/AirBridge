using AirBridge.App;

namespace AirBridge.Tests;

public sealed class ShutdownCoordinatorTests
{
    [Fact]
    public async Task GracefulShutdownCompletesWithoutForcedCleanup()
    {
        var gracefulCalls = 0;
        var forcedCalls = 0;
        var coordinator = new ShutdownCoordinator(
            _ => { Interlocked.Increment(ref gracefulCalls); return Task.CompletedTask; },
            () => Interlocked.Increment(ref forcedCalls),
            TimeSpan.FromSeconds(1));

        Assert.True(await coordinator.ShutdownAsync());
        Assert.Equal(1, gracefulCalls);
        Assert.Equal(0, forcedCalls);
    }

    [Fact]
    public async Task HungShutdownUsesForcedCleanupAfterDeadline()
    {
        var forced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShutdownCoordinator(
            _ => never.Task,
            () => forced.TrySetResult(),
            TimeSpan.FromMilliseconds(75));

        Assert.False(await coordinator.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        await forced.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneShutdownAttempt()
    {
        var gracefulCalls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShutdownCoordinator(
            _ => { Interlocked.Increment(ref gracefulCalls); return release.Task; },
            () => throw new Xunit.Sdk.XunitException("Forced cleanup was not expected."),
            TimeSpan.FromSeconds(1));

        var first = coordinator.ShutdownAsync();
        var second = coordinator.ShutdownAsync();
        release.SetResult();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Same(first, second);
        Assert.Equal(1, gracefulCalls);
    }

    [Fact]
    public async Task GracefulFailureUsesForcedCleanupOnlyOnce()
    {
        var forcedCalls = 0;
        var coordinator = new ShutdownCoordinator(
            _ => Task.FromException(new InvalidOperationException("failure")),
            () => Interlocked.Increment(ref forcedCalls),
            TimeSpan.FromSeconds(1));

        Assert.False(await coordinator.ShutdownAsync());
        Assert.False(await coordinator.ShutdownAsync());
        Assert.Equal(1, forcedCalls);
    }
}
