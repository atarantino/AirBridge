using System.Net;
using System.Text;
using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class AgentActivityTests
{
    [Fact]
    public void StoreIsBoundedAndSanitizesEveryPublishedEvent()
    {
        var store = new AgentActivityStore(capacity: 10);
        for (var index = 0; index < 15; index++)
            store.Publish(new(DateTimeOffset.UtcNow, AgentActivityKind.ToolResult, "result", $"event-{index}",
                @"peer 192.168.1.40 AA:BB:CC:DD:EE:FF sk-examplecredential123 C:\Users\private\trace.txt"));

        var events = store.Snapshot();
        Assert.Equal(10, events.Count);
        Assert.Equal("event-5", events[0].Summary);
        Assert.DoesNotContain("192.168.1.40", events[^1].Details, StringComparison.Ordinal);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", events[^1].Details, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-examplecredential123", events[^1].Details, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\private\trace.txt", events[^1].Details, StringComparison.Ordinal);
        Assert.Contains("[network-address]", events[^1].Details, StringComparison.Ordinal);
        Assert.Contains("[hardware-address]", events[^1].Details, StringComparison.Ordinal);
        Assert.Contains("[credential]", events[^1].Details, StringComparison.Ordinal);
        Assert.Contains("[local-path]", events[^1].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentPublishesRequestPolicyToolResultAndFinalResponse()
    {
        var responses = new Queue<string>(
        [
            """
            {"id":"resp_first_123456789","usage":{"input_tokens":12,"output_tokens":4},"output":[{"type":"function_call","name":"get_stream_health","call_id":"call_1","arguments":"{}"}]}
            """,
            """
            {"id":"resp_second_123456789","usage":{"input_tokens":21,"output_tokens":7},"output_text":"The stream is healthy.","output":[]}
            """
        ]);
        using var http = new HttpClient(new StubHandler(() => responses.Dequeue())) { BaseAddress = new Uri("https://api.openai.com/") };
        var store = new AgentActivityStore();
        var agent = new OpenAiAgent("test-key", new AgentPolicy(), new StubRuntime(), http, store);

        var result = await agent.AskAsync("Check the stream", diagnostic: true);

        Assert.Equal("The stream is healthy.", result);
        var events = store.Snapshot();
        Assert.Equal(2, events.Count(item => item.Kind == AgentActivityKind.ApiRequest));
        Assert.Equal(2, events.Count(item => item.Kind == AgentActivityKind.ApiResponse));
        Assert.Contains(events, item => item.Kind == AgentActivityKind.ToolCall && item.Title == "get_stream_health");
        Assert.Contains(events, item => item.Kind == AgentActivityKind.Policy && item.Tone == AgentActivityTone.Success);
        Assert.Contains(events, item => item.Kind == AgentActivityKind.ToolResult && item.Details!.Contains("Streaming", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Kind == AgentActivityKind.AssistantResponse && item.Details == "The stream is healthy.");
    }

    private sealed class StubRuntime : IAgentToolRuntime
    {
        public Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(new { state = "Streaming", buffer_fill_percent = 72 });
    }

    private sealed class StubHandler(Func<string> nextResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nextResponse(), Encoding.UTF8, "application/json")
            });
    }
}
