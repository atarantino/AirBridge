using System.Text.Json;

namespace AirBridge.Core;

public enum ToolPermission { ReadOnly, Reversible, ConfirmationRequired, Forbidden }

public sealed record PolicyDecision(bool Allowed, bool RequiresConfirmation, string Reason);

public sealed class AgentPolicy
{
    private static readonly IReadOnlyDictionary<string, ToolPermission> Catalog = new Dictionary<string, ToolPermission>(StringComparer.Ordinal)
    {
        ["list_airplay_devices"] = ToolPermission.ReadOnly,
        ["list_audio_sessions"] = ToolPermission.ReadOnly,
        ["get_current_routes"] = ToolPermission.ReadOnly,
        ["get_stream_health"] = ToolPermission.ReadOnly,
        ["get_buffer_metrics"] = ToolPermission.ReadOnly,
        ["get_network_metrics"] = ToolPermission.ReadOnly,
        ["get_alignment"] = ToolPermission.ReadOnly,
        ["get_standby"] = ToolPermission.ReadOnly,
        ["get_sync_status"] = ToolPermission.ReadOnly,
        ["run_connectivity_test"] = ToolPermission.ReadOnly,
        ["start_system_stream"] = ToolPermission.Reversible,
        ["start_application_stream"] = ToolPermission.Reversible,
        ["stop_stream"] = ToolPermission.Reversible,
        ["move_stream"] = ToolPermission.Reversible,
        ["set_receiver_volume"] = ToolPermission.Reversible,
        ["set_alignment_trim"] = ToolPermission.Reversible,
        ["set_standby"] = ToolPermission.Reversible,
        ["set_buffer_target"] = ToolPermission.Reversible,
        ["set_quality_profile"] = ToolPermission.Reversible,
        ["reconnect_stream"] = ToolPermission.Reversible,
        ["measure_acoustic_delay"] = ToolPermission.ConfirmationRequired,
        ["align_group"] = ToolPermission.ConfirmationRequired,
        ["enable_browser_sync"] = ToolPermission.Reversible,
        ["disable_browser_sync"] = ToolPermission.Reversible,
        ["apply_sync_offset"] = ToolPermission.Reversible,
        ["save_routing_rule"] = ToolPermission.ConfirmationRequired,
        ["change_startup_behavior"] = ToolPermission.ConfirmationRequired,
        ["enable_microphone_calibration"] = ToolPermission.ConfirmationRequired,
        ["arbitrary_shell"] = ToolPermission.Forbidden
    };

    public PolicyDecision Evaluate(string toolName, JsonElement arguments, bool userConfirmed = false)
    {
        if (!Catalog.TryGetValue(toolName, out var permission)) return new(false, false, "Tool is not in the local catalog.");
        if (permission == ToolPermission.Forbidden) return new(false, false, "Capability is forbidden by policy.");
        if (permission == ToolPermission.ConfirmationRequired && !userConfirmed) return new(false, true, "Explicit user confirmation is required.");
        if (toolName == "set_buffer_target" && arguments.TryGetProperty("milliseconds", out var ms) && ms.GetInt32() is < 100 or > 5000)
            return new(false, false, "Buffer target must be between 100 and 5000 milliseconds.");
        if (toolName == "set_receiver_volume" && arguments.TryGetProperty("percent", out var volume) && volume.GetInt32() is < 0 or > 100)
            return new(false, false, "Volume must be between 0 and 100 percent.");
        if (toolName == "set_alignment_trim" && arguments.TryGetProperty("trim_ms", out var delay) &&
            delay.GetInt32() is < ReceiverAlignmentPlan.MinimumTrimMilliseconds or > ReceiverAlignmentPlan.MaximumTrimMilliseconds)
            return new(false, false, $"Receiver alignment delay must be between {ReceiverAlignmentPlan.MinimumTrimMilliseconds} and {ReceiverAlignmentPlan.MaximumTrimMilliseconds} milliseconds.");
        if (toolName == "set_standby" && arguments.TryGetProperty("after_seconds", out var standbySeconds) && standbySeconds.GetInt32() is < 10 or > 600)
            return new(false, false, "Silence standby must be between 10 and 600 seconds.");
        return new(true, false, "Allowed.");
    }
}

/// <summary>Binds a pending confirmation to both the tool name and canonical arguments.</summary>
public sealed class ToolConfirmationStore
{
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    public void Request(string toolName, JsonElement arguments) => _pending[toolName] = Canonicalize(toolName, arguments);

    public bool TryConsume(string toolName, JsonElement arguments)
    {
        if (!_pending.TryGetValue(toolName, out var expected) || expected != Canonicalize(toolName, arguments)) return false;
        _pending.Remove(toolName);
        return true;
    }

    private static string Canonicalize(string toolName, JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value, toolName, null);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, string toolName, string? propertyName)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value, toolName, property.Name);
            }
            writer.WriteEndObject();
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            var items = value.EnumerateArray().ToArray();
            if (toolName == "align_group" && propertyName == "receiver_ids" && items.All(item => item.ValueKind == JsonValueKind.String))
                items = items.OrderBy(item => item.GetString(), StringComparer.Ordinal).ToArray();
            foreach (var item in items) WriteCanonical(writer, item, toolName, null);
            writer.WriteEndArray();
            return;
        }
        value.WriteTo(writer);
    }
}

/// <summary>Recognizes a clear, non-negated current-turn request and authorizes one matching microphone tool call.</summary>
public sealed class DirectMicrophoneAuthorization
{
    private static readonly char[] TokenSeparators =
    [
        ' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '“', '”', '(', ')', '[', ']', '{', '}', '-', '—'
    ];

    private static readonly HashSet<string> AmbiguityTokens = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "nothing", "neither", "nor", "hardly", "barely", "dont",
        "none", "zero", "without", "avoid", "cancel", "cancelled", "canceled", "denied", "unable",
        "refuse", "refused", "revoke", "revoked", "withdraw", "withdrawn",
        "if", "unless", "hypothetically", "assuming", "suppose", "supposing", "provided",
        "when", "after", "before", "until", "once", "later", "might", "maybe", "perhaps",
        "but", "however", "although", "though", "except", "instead", "actually", "wait"
    };

    private static readonly HashSet<string> ConversationalPrefixes = new(StringComparer.Ordinal)
    {
        "hey", "hi", "hello", "sorry", "ok", "okay"
    };

    private static readonly HashSet<string> AlignmentObjects = new(StringComparer.Ordinal)
    {
        "speaker", "speakers", "receiver", "receivers", "group", "alignment", "them", "those", "these", "both", "together", "and"
    };

    private readonly HashSet<string> _tools;
    private DirectMicrophoneAuthorization(HashSet<string> tools) => _tools = tools;

    public static DirectMicrophoneAuthorization FromUserText(string text)
    {
        HashSet<string> tools = new(StringComparer.Ordinal);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('"') || trimmed.StartsWith('“') || trimmed.StartsWith('\'') || HasTrailingSentence(trimmed)) return new(tools);
        var words = Tokenize(text);
        if (words.Length == 0 || IsAmbiguous(words)) return new(tools);

        var actionIndex = FindActionIndex(words);
        if (IsAlignmentApproval(words) || actionIndex >= 0 && IsAlignmentRequest(words, actionIndex))
            tools.Add("align_group");
        if (actionIndex >= 0 && IsMeasurementRequest(words, actionIndex))
            tools.Add("measure_acoustic_delay");
        return new(tools);
    }

    public bool TryConsume(string toolName) => _tools.Remove(toolName);

    private static string[] Tokenize(string text) => text
        .Trim()
        .ToLowerInvariant()
        .Replace('’', '\'')
        .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsAmbiguous(IEnumerable<string> words) => words.Any(word =>
        AmbiguityTokens.Contains(word) || word.EndsWith("n't", StringComparison.Ordinal));

    private static bool HasTrailingSentence(string text)
    {
        var sentenceEnd = text.IndexOfAny(['.', '!', '?', ';']);
        if (sentenceEnd < 0) return false;
        return text[(sentenceEnd + 1)..].Trim([' ', '\t', '\r', '\n', '"', '\'', '”']).Length > 0;
    }

    private static int FindActionIndex(IReadOnlyList<string> words)
    {
        var start = ConversationalPrefixes.Contains(words[0]) ? 1 : 0;
        if (StartsWith(words, start, "please")) return start + 1;
        if (StartsWith(words, start, "can", "you") ||
            StartsWith(words, start, "could", "you") ||
            StartsWith(words, start, "would", "you"))
        {
            var action = start + 2;
            return action < words.Count && words[action] == "please" ? action + 1 : action;
        }
        if (StartsWith(words, start, "let's")) return start + 1;
        if (StartsWith(words, start, "go", "ahead", "and")) return start + 3;
        if (StartsWith(words, start, "i", "want", "you", "to") ||
            StartsWith(words, start, "i", "need", "you", "to") ||
            StartsWith(words, start, "i", "allow", "you", "to"))
            return start + 4;
        if (StartsWith(words, start, "i", "explicitly", "allow", "you", "to")) return start + 5;
        if (StartsWith(words, start, "i", "authorize", "you", "to")) return start + 4;
        if (StartsWith(words, start, "you", "have", "my", "permission", "to")) return start + 5;
        return start;
    }

    private static bool IsAlignmentApproval(IReadOnlyList<string> words)
    {
        var start = ConversationalPrefixes.Contains(words[0]) ? 1 : 0;
        if (!StartsWith(words, start, "i", "approve", "this", "alignment") &&
            !StartsWith(words, start, "i", "approve", "the", "alignment"))
            return false;
        return words.Count == start + 4 || words.Count == start + 5 && words[^1] is "now" or "please";
    }

    private static bool IsAlignmentRequest(IReadOnlyList<string> words, int actionIndex)
    {
        if (actionIndex >= words.Count) return false;
        var action = words[actionIndex];
        var hasAlignmentObject = words.Skip(actionIndex + 1).Any(AlignmentObjects.Contains);
        if (action is "align" or "sync") return hasAlignmentObject;
        return action == "get" && hasAlignmentObject &&
            (ContainsSequence(words, actionIndex + 1, "in", "time") ||
             words.Skip(actionIndex + 1).Any(word => word is "synced" or "synchronized"));
    }

    private static bool IsMeasurementRequest(IReadOnlyList<string> words, int actionIndex)
    {
        if (actionIndex >= words.Count || words[actionIndex] is not ("measure" or "check")) return false;
        return words.Skip(actionIndex + 1).Any(word => word is "delay" or "latency" or "acoustic" or "timing" or "it");
    }

    private static bool StartsWith(IReadOnlyList<string> words, int start, params string[] prefix)
    {
        if (start < 0 || start + prefix.Length > words.Count) return false;
        for (var index = 0; index < prefix.Length; index++)
            if (words[start + index] != prefix[index]) return false;
        return true;
    }

    private static bool ContainsSequence(IReadOnlyList<string> words, int start, string first, string second)
    {
        for (var index = start; index + 1 < words.Count; index++)
            if (words[index] == first && words[index + 1] == second) return true;
        return false;
    }
}

public interface IAgentToolRuntime
{
    ToolConfirmationRequest? GetConfirmationRequest(string name, JsonElement arguments) => null;
    Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}

public sealed record ToolConfirmationRequest(
    string ToolName,
    string Reason,
    string? Title = null,
    string? Message = null);
