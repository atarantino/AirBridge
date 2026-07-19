using System.Net;
using System.Text;
using System.Text.Json;
using AirBridge.App;
using AirBridge.Core;

namespace AirBridge.Tests;

public sealed class AgentActivityTests
{
    [Theory]
    [InlineData("I explicitly approve this microphone measurement")]
    [InlineData("I fully consent to the requested calibration")]
    [InlineData("I explicitly authorize it")]
    public async Task PendingToolAcceptsNaturalExplicitApprovalPhrases(string approval)
    {
        var responses = new Queue<string>(
        [
            """
            {"id":"resp_request_123456789","output":[{"type":"function_call","name":"measure_acoustic_delay","call_id":"call_request","arguments":"{\"receiver_id\":\"speaker-a\"}"}]}
            """,
            """
            {"id":"resp_confirmation_123456789","output_text":"Please confirm.","output":[]}
            """,
            """
            {"id":"resp_approved_call_123456789","output":[{"type":"function_call","name":"measure_acoustic_delay","call_id":"call_approved","arguments":"{\"receiver_id\":\"speaker-a\"}"}]}
            """,
            """
            {"id":"resp_approved_result_123456789","output_text":"Measurement completed.","output":[]}
            """
        ]);
        var runtime = new CountingRuntime();
        using var http = new HttpClient(new StubHandler(() => responses.Dequeue())) { BaseAddress = new Uri("https://api.openai.com/") };
        var agent = new OpenAiAgent("test-key", new AgentPolicy(), runtime, http);

        Assert.Equal("Please confirm.", await agent.AskAsync("Measure the delay", diagnostic: false));
        Assert.Equal("Measurement completed.", await agent.AskAsync(approval, diagnostic: false));
        Assert.Equal(1, runtime.ExecutionCount);
    }

    [Fact]
    public async Task PendingConfirmationSurvivesAgentRecreationAndAcceptsOrdinaryApproval()
    {
        var confirmationStore = new ToolConfirmationStore();
        var runtime = new CountingRuntime();
        var firstResponses = new Queue<string>(
        [
            """{"id":"resp_request_recreated","output":[{"type":"function_call","name":"align_group","call_id":"call_request","arguments":"{\"receiver_ids\":[\"speaker-a\",\"speaker-b\"]}"}]}""",
            """{"id":"resp_prompt_recreated","output_text":"Please confirm.","output":[]}"""
        ]);
        using var firstHttp = new HttpClient(new StubHandler(() => firstResponses.Dequeue())) { BaseAddress = new Uri("https://api.openai.com/") };
        var firstAgent = new OpenAiAgent("test-key", new AgentPolicy(), runtime, firstHttp, confirmationStore: confirmationStore);
        Assert.Equal("Please confirm.", await firstAgent.AskAsync("Should these speakers be aligned?", diagnostic: false));

        var secondResponses = new Queue<string>(
        [
            """{"id":"resp_approved_recreated","output":[{"type":"function_call","name":"align_group","call_id":"call_approved","arguments":"{\"receiver_ids\":[\"speaker-b\",\"speaker-a\"]}"}]}""",
            """{"id":"resp_complete_recreated","output_text":"Alignment completed.","output":[]}"""
        ]);
        using var secondHttp = new HttpClient(new StubHandler(() => secondResponses.Dequeue())) { BaseAddress = new Uri("https://api.openai.com/") };
        var secondAgent = new OpenAiAgent("test-key", new AgentPolicy(), runtime, secondHttp, confirmationStore: confirmationStore);

        Assert.Equal("Alignment completed.", await secondAgent.AskAsync("Sure, go ahead.", diagnostic: false));
        Assert.Equal(1, runtime.ExecutionCount);
    }

    [Fact]
    public async Task LocalConfirmationPromptExecutesExactToolCallWithoutTypedApprovalRoundTrip()
    {
        var responses = new Queue<string>(
        [
            """{"id":"resp_local_prompt","output":[{"type":"function_call","name":"align_group","call_id":"call_local_prompt","arguments":"{\"receiver_ids\":[\"speaker-a\",\"speaker-b\"]}"}]}""",
            """{"id":"resp_local_complete","output_text":"Alignment completed.","output":[]}"""
        ]);
        var promptedTools = new List<string>();
        var runtime = new CountingRuntime();
        using var http = new HttpClient(new StubHandler(() => responses.Dequeue())) { BaseAddress = new Uri("https://api.openai.com/") };
        var agent = new OpenAiAgent("test-key", new AgentPolicy(), runtime, http,
            confirmationPrompt: (toolName, _) =>
            {
                promptedTools.Add(toolName);
                return Task.FromResult(true);
            });

        Assert.Equal("Alignment completed.", await agent.AskAsync("Check speaker timing", diagnostic: false));
        Assert.Equal(["align_group"], promptedTools);
        Assert.Equal(1, runtime.ExecutionCount);
    }

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
    public void PersistentActivityLogIncludesDiagnosticsButExcludesTranscriptsAndAssistantText()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AirBridgeActivityTests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "ai-activity.jsonl");
        try
        {
            var store = new AgentActivityStore(persistToDisk: true, logPath: path);
            store.Publish(new(DateTimeOffset.UtcNow, AgentActivityKind.Transcription, "Transcript ready", "Private", "private spoken text"));
            store.Publish(new(DateTimeOffset.UtcNow, AgentActivityKind.ToolCall, "align_group", "Tool requested", "{\"receiver_ids\":[\"receiver-1\",\"receiver-2\"]}"));
            store.Publish(new(DateTimeOffset.UtcNow, AgentActivityKind.Policy, "align_group", "Awaiting user confirmation", "Explicit user confirmation is required."));
            store.Publish(new(DateTimeOffset.UtcNow, AgentActivityKind.AssistantResponse, "Assistant response", "Returned", "private assistant text"));

            var persisted = File.ReadAllText(path);
            Assert.Contains("align_group", persisted, StringComparison.Ordinal);
            Assert.Contains("Awaiting user confirmation", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("private spoken text", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("private assistant text", persisted, StringComparison.Ordinal);
            Assert.Equal(2, File.ReadLines(path).Count());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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

    [Fact]
    public async Task AgentReturnsToolFailureOutputAndKeepsResponseChainValid()
    {
        var requests = new List<string>();
        var responses = new Queue<string>(
        [
            """
            {"id":"resp_failure_123456789","output":[{"type":"function_call","name":"get_stream_health","call_id":"call_failed","arguments":"{}"}]}
            """,
            """
            {"id":"resp_recovery_123456789","output_text":"The local check failed safely.","output":[]}
            """
        ]);
        using var http = new HttpClient(new RecordingStubHandler(requests, () => responses.Dequeue())) { BaseAddress = new Uri("https://api.openai.com/") };
        var agent = new OpenAiAgent("test-key", new AgentPolicy(), new FailingRuntime(), http);

        var result = await agent.AskAsync("Check the stream", diagnostic: true);

        Assert.Equal("The local check failed safely.", result);
        Assert.Equal(2, requests.Count);
        Assert.Contains("function_call_output", requests[1], StringComparison.Ordinal);
        Assert.Contains("tool_failed", requests[1], StringComparison.Ordinal);
        Assert.Contains("call_failed", requests[1], StringComparison.Ordinal);
    }

    private sealed class StubRuntime : IAgentToolRuntime
    {
        public Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(new { state = "Streaming", buffer_fill_percent = 72 });
    }

    private sealed class FailingRuntime : IAgentToolRuntime
    {
        public Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromException<object?>(new InvalidOperationException("Local measurement failed."));
    }

    private sealed class CountingRuntime : IAgentToolRuntime
    {
        public int ExecutionCount { get; private set; }

        public Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult<object?>(new { median_ms = 1200 });
        }
    }

    private sealed class StubHandler(Func<string> nextResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nextResponse(), Encoding.UTF8, "application/json")
            });
    }

    private sealed class RecordingStubHandler(List<string> requests, Func<string> nextResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(nextResponse(), Encoding.UTF8, "application/json")
            };
        }
    }
}
