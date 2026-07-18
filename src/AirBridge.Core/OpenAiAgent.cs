using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AirBridge.Core;

public sealed class OpenAiAgent
{
    public const string Model = "gpt-5.6";
    private readonly HttpClient _http;
    private readonly AgentPolicy _policy;
    private readonly IAgentToolRuntime _runtime;
    private readonly ToolConfirmationStore _pendingConfirmations = new();
    private string? _previousResponseId;

    public OpenAiAgent(string apiKey, AgentPolicy policy, IAgentToolRuntime runtime, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An OpenAI API key is required.", nameof(apiKey));
        _policy = policy;
        _runtime = runtime;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> AskAsync(string userText, bool diagnostic, CancellationToken cancellationToken = default)
    {
        var confirmsPending = IsExplicitConfirmation(userText);
        var directMicrophoneAuthorization = DirectMicrophoneAuthorization.FromUserText(userText);
        object input = userText;
        for (var turn = 0; turn < 8; turn++)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["instructions"] = Instructions,
                ["input"] = input,
                ["tools"] = ToolDefinitions,
                ["reasoning"] = new { effort = diagnostic ? "high" : "low" },
                ["max_output_tokens"] = 4000,
                ["store"] = true
            };
            if (_previousResponseId is not null) payload["previous_response_id"] = _previousResponseId;

            using var response = await _http.PostAsync("v1/responses", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI returned {(int)response.StatusCode}: {ReadError(raw)}");
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            _previousResponseId = root.GetProperty("id").GetString();
            var calls = ReadFunctionCalls(root).ToArray();
            if (calls.Length == 0) return ReadOutputText(root) ?? "The agent returned no text.";

            var outputs = new List<object>();
            foreach (var call in calls)
            {
                using var argsDocument = JsonDocument.Parse(call.Arguments);
                var userConfirmed = directMicrophoneAuthorization.TryConsume(call.Name) ||
                    (confirmsPending && _pendingConfirmations.TryConsume(call.Name, argsDocument.RootElement));
                var decision = _policy.Evaluate(call.Name, argsDocument.RootElement, userConfirmed);
                if (decision.RequiresConfirmation) _pendingConfirmations.Request(call.Name, argsDocument.RootElement);
                object? result = decision.Allowed
                    ? await _runtime.ExecuteAsync(call.Name, argsDocument.RootElement.Clone(), cancellationToken)
                    : new { error = decision.Reason, requires_confirmation = decision.RequiresConfirmation };
                outputs.Add(new { type = "function_call_output", call_id = call.CallId, output = JsonSerializer.Serialize(result) });
            }
            input = outputs;
        }
        throw new InvalidOperationException("Agent exceeded the eight-turn tool limit.");
    }

    private static IEnumerable<(string Name, string CallId, string Arguments)> ReadFunctionCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output)) yield break;
        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "function_call")
                yield return (item.GetProperty("name").GetString()!, item.GetProperty("call_id").GetString()!, item.GetProperty("arguments").GetString() ?? "{}");
        }
    }

    private static string? ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct)) return direct.GetString();
        if (!root.TryGetProperty("output", out var output)) return null;
        foreach (var item in output.EnumerateArray())
            if (item.TryGetProperty("content", out var content))
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text") return part.GetProperty("text").GetString();
        return null;
    }

    private static string ReadError(string raw)
    {
        try { using var doc = JsonDocument.Parse(raw); return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Unknown error"; }
        catch (JsonException) { return "Unparseable API error"; }
    }

    private static bool IsExplicitConfirmation(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return normalized is "yes" or "confirm" or "confirmed" or "go ahead" or "do it" or "proceed" ||
               normalized.StartsWith("yes,", StringComparison.Ordinal) || normalized.StartsWith("confirm ", StringComparison.Ordinal);
    }

    private const string Instructions = """
You are AirBridge's operational agent. Route Windows audio to AirPlay receivers using only the provided typed tools. Never request or expose raw audio, credentials, IP addresses, paths, or full window titles. Resolve ambiguous receivers or applications before acting. Core streaming must remain useful without you. For diagnostics follow observe, classify, gather evidence, explain, act, wait for a fresh measurement, then observe again. Every configuration change must be re-measured; explicitly say whether the fix was verified. Persistent actions require user confirmation and must not be retried after the policy rejects them. An explicit user request to align a group authorizes align_group and its proposed trims in one operation.
""";

    public static readonly object[] ToolDefinitions =
    [
        Tool("list_airplay_devices", "Lists discovered AirPlay receivers.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("list_audio_sessions", "Lists active audio applications without sensitive window titles or paths.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("get_current_routes", "Returns the current audio route.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("get_stream_health", "Returns aggregated live stream health including buffer fill percent, underruns, overruns, benign producer-idle padding, and true starvation; never raw audio.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("get_buffer_metrics", "Returns receiver-scoped bounded-buffer fill and silence-padding telemetry.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("get_network_metrics", "Returns receiver transport metrics available to the app.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("get_alignment", "Returns the extra alignment delay for every known receiver in milliseconds.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("get_standby", "Returns whether silence standby is enabled and its idle timeout.", new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool("start_system_stream", "Streams the Windows system mix to one or more receivers.", new { type = "object", properties = new { receiver_ids = StringArray(), quality_profile = Enum("balanced", "stable", "low_latency") }, required = new[] { "receiver_ids", "quality_profile" }, additionalProperties = false }),
        Tool("start_application_stream", "Streams one Windows process tree to one or more receivers.", new { type = "object", properties = new { process_id = new { type = "integer" }, receiver_ids = StringArray(), quality_profile = Enum("balanced", "stable", "low_latency") }, required = new[] { "process_id", "receiver_ids", "quality_profile" }, additionalProperties = false }),
        Tool("stop_stream", "Stops one receiver or every active receiver.", new { type = "object", properties = new { receiver_id = NullableString(), all = new { type = "boolean" } }, required = new[] { "receiver_id", "all" }, additionalProperties = false }),
        Tool("move_stream", "Moves the current stream to one or more receivers.", new { type = "object", properties = new { receiver_ids = StringArray() }, required = new[] { "receiver_ids" }, additionalProperties = false }),
        Tool("set_receiver_volume", "Sets one receiver's volume percent.", new { type = "object", properties = new { receiver_id = new { type = "string" }, percent = new { type = "integer", minimum = 0, maximum = 100 } }, required = new[] { "receiver_id", "percent" }, additionalProperties = false }),
        Tool("set_alignment_trim", "Sets an extra per-receiver delay for speaker alignment. For example, 'delay Kitchen by 60 ms' sets trim_ms to 60.", new { type = "object", properties = new { receiver_id = new { type = "string" }, trim_ms = new { type = "integer", minimum = 0, maximum = 500 } }, required = new[] { "receiver_id", "trim_ms" }, additionalProperties = false }),
        Tool("set_standby", "Enables or disables release of RAOP sessions after sustained silence while capture stays ready. Timeout is 10–600 seconds.", new { type = "object", properties = new { enabled = new { type = "boolean" }, after_seconds = new { type = "integer", minimum = 10, maximum = 600 } }, required = new[] { "enabled", "after_seconds" }, additionalProperties = false }),
        Tool("set_buffer_target", "Sets the temporary buffer target; larger is more stable but adds latency.", new { type = "object", properties = new { stream_id = new { type = "string" }, milliseconds = new { type = "integer", minimum = 100, maximum = 5000 } }, required = new[] { "stream_id", "milliseconds" }, additionalProperties = false }),
        Tool("reconnect_stream", "Reconnects one receiver or every active receiver near the live edge.", new { type = "object", properties = new { receiver_id = NullableString(), all = new { type = "boolean" } }, required = new[] { "receiver_id", "all" }, additionalProperties = false }),
        Tool("measure_acoustic_delay", "Emits five calibration chirps to one active receiver and uses the default microphone in memory to measure end-to-end delay. Only call after the user explicitly asks to measure delay.", new { type = "object", properties = new { receiver_id = new { type = "string" } }, required = new[] { "receiver_id" }, additionalProperties = false }),
        Tool("align_group", "Sequentially measures two or more active receivers with five chirps each, reports pairwise skew, and applies bounded per-receiver trims. Only call after the user explicitly asks to align the group.", new { type = "object", properties = new { receiver_ids = StringArray() }, required = new[] { "receiver_ids" }, additionalProperties = false }),
        Tool("enable_browser_sync", "Enables supported HTML5 media synchronization.", new { type = "object", properties = new { offset_ms = new { type = "integer", minimum = 0, maximum = 10000 } }, required = new[] { "offset_ms" }, additionalProperties = false })
    ];

    private static object Tool(string name, string description, object parameters) => new { type = "function", name, description, strict = true, parameters };
    private static object Enum(params string[] values) => new { type = "string", @enum = values };
    private static object StringArray() => new { type = "array", items = new { type = "string" }, minItems = 1 };
    private static object NullableString() => new { type = new[] { "string", "null" } };
}
