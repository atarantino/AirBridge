using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AirBridge.App;

namespace AirBridge.Tests;

public sealed class DebugPipeServerTests
{
    [Fact]
    public async Task RoundTripPreservesRequestIdAndReturnsState()
    {
        var name = $"AirBridge.Test.{Guid.NewGuid():N}";
        await using var server = new DebugPipeServer(name, (request, _) =>
            Task.FromResult<object?>(new { ready = true, requested = request.Method }));
        server.Start();
        var reply = await RequestAsync(name, "{\"id\":\"request-one\",\"method\":\"state\",\"params\":{}}");
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("request-one", reply.GetProperty("id").GetString());
        Assert.True(reply.GetProperty("result").GetProperty("ready").GetBoolean());
        Assert.Contains(server.Events.Snapshot(), item => item.Kind == "operation-completed");
    }

    [Fact]
    public async Task ProtocolRejectsUnknownMethodAndLargeRequestsWithoutCallingHandler()
    {
        var calls = 0;
        var name = $"AirBridge.Test.{Guid.NewGuid():N}";
        await using var server = new DebugPipeServer(name, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<object?>(null);
        });
        server.Start();
        var unknown = await RequestAsync(name, "{\"id\":\"one\",\"method\":\"arbitrary-command\"}");
        Assert.Equal("unknown_method", unknown.GetProperty("error").GetProperty("code").GetString());
        var oversized = await RequestAsync(name, new string('x', 33 * 1024));
        Assert.Equal("request_too_large", oversized.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ControlledErrorsAreStructuredAndParametersStayOutOfEvents()
    {
        var name = $"AirBridge.Test.{Guid.NewGuid():N}";
        await using var server = new DebugPipeServer(name, (_, _) =>
            Task.FromException<object?>(new DebugProtocolException("fixture_required", "Only fixture sessions support this action.")));
        server.Start();
        var reply = await RequestAsync(name, "{\"id\":\"one\",\"method\":\"start\",\"params\":{\"secret\":\"never-log-this\"}}");
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("fixture_required", reply.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("never-log-this", JsonSerializer.Serialize(server.Events.Snapshot()));
    }

    [Fact]
    public async Task ConcurrentStateRequestDoesNotWaitForPendingCondition()
    {
        var name = $"AirBridge.Test.{Guid.NewGuid():N}";
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new DebugPipeServer(name, async (request, token) =>
        {
            if (request.Method == "wait-for-state")
            {
                waiting.SetResult();
                await release.Task.WaitAsync(token);
            }
            return new { ready = true };
        });
        server.Start();
        var condition = RequestAsync(name, "{\"id\":\"wait\",\"method\":\"wait-for-state\",\"params\":{}}");
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var state = await RequestAsync(name, "{\"id\":\"state\",\"method\":\"state\"}");
        Assert.True(state.GetProperty("ok").GetBoolean());
        release.SetResult();
        Assert.True((await condition).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ShutdownCancelsWaitingClientEvenWhenHandlerIgnoresCancellation()
    {
        var name = $"AirBridge.Test.{Guid.NewGuid():N}";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new DebugPipeServer(name, (_, _) =>
        {
            entered.SetResult();
            return release.Task;
        });
        server.Start();
        var request = RequestAsync(name, "{\"id\":\"one\",\"method\":\"state\"}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            var reply = await request;
            Assert.Equal("timeout", reply.GetProperty("error").GetProperty("code").GetString());
        }
        finally { release.TrySetResult(null); }
    }

    [Fact]
    public void EventStoreIsBoundedAndCursorsSuppressDeliveredEvents()
    {
        var store = new DebugEventStore();
        for (var index = 0; index < 300; index++) store.Add("test", $"event {index}");
        Assert.Equal(250, store.Snapshot().Count);
        Assert.Equal(50, store.Snapshot(250).Count);
        Assert.Empty(store.Snapshot(300));
        Assert.Throws<ArgumentException>(() => new DebugPipeServer("invalid\\pipe", (_, _) => Task.FromResult<object?>(null)));
    }

    private static async Task<JsonElement> RequestAsync(string pipeName, string json)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await client.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), deadline.Token);
        await client.FlushAsync(deadline.Token);
        var line = await reader.ReadLineAsync(deadline.Token);
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
}
