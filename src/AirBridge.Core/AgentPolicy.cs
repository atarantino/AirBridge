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
    private readonly HashSet<string> _tools;
    private DirectMicrophoneAuthorization(HashSet<string> tools) => _tools = tools;

    public static DirectMicrophoneAuthorization FromUserText(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        var negated = normalized.Contains("don't", StringComparison.Ordinal) ||
            normalized.Contains("do not", StringComparison.Ordinal) ||
            normalized.Contains("dont", StringComparison.Ordinal) ||
            normalized.Contains("not align", StringComparison.Ordinal) ||
            normalized.Contains("not measure", StringComparison.Ordinal) ||
            normalized.Contains("without using", StringComparison.Ordinal) ||
            normalized.Contains("avoid ", StringComparison.Ordinal);
        HashSet<string> tools = new(StringComparer.Ordinal);
        if (negated) return new(tools);
        if (IsImperative(normalized, "align") &&
            (normalized.Contains("group", StringComparison.Ordinal) || normalized.Contains("speaker", StringComparison.Ordinal) ||
             normalized.Contains("receiver", StringComparison.Ordinal) || normalized.Contains("alignment", StringComparison.Ordinal) ||
             normalized.Contains(" and ", StringComparison.Ordinal)) ||
            IsApprovalOf(normalized, "align"))
            tools.Add("align_group");
        if (IsImperative(normalized, "measure") &&
            (normalized.Contains("delay", StringComparison.Ordinal) || normalized.Contains("latency", StringComparison.Ordinal)) ||
            IsApprovalOf(normalized, "measure"))
            tools.Add("measure_acoustic_delay");
        return new(tools);
    }

    public bool TryConsume(string toolName) => _tools.Remove(toolName);

    private static bool IsImperative(string text, string verb) =>
        text.StartsWith(verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("please " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("can you " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("could you " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("would you " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("let's " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("go ahead and " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("i want you to " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("i need you to " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("i allow you to " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("i explicitly allow you to " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("i authorize you to " + verb + " ", StringComparison.Ordinal) ||
        text.StartsWith("you have my permission to " + verb + " ", StringComparison.Ordinal);

    private static bool IsApprovalOf(string text, string action) =>
        (text.Contains("approve", StringComparison.Ordinal) || text.Contains("allow", StringComparison.Ordinal) ||
         text.Contains("authorize", StringComparison.Ordinal) || text.Contains("permission", StringComparison.Ordinal) ||
         text.Contains("go ahead", StringComparison.Ordinal)) &&
        (action == "align"
            ? text.Contains("align", StringComparison.Ordinal)
            : text.Contains("measure", StringComparison.Ordinal) &&
              (text.Contains("delay", StringComparison.Ordinal) || text.Contains("latency", StringComparison.Ordinal)));
}

public interface IAgentToolRuntime
{
    Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}
