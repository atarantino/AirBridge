using System.Net;
using System.Text.RegularExpressions;

namespace AirBridge.Core;

public enum AgentActivityKind
{
    Transcription,
    ApiRequest,
    ApiResponse,
    ToolCall,
    Policy,
    ToolResult,
    AssistantResponse,
    Error
}

public enum AgentActivityTone { Neutral, Success, Warning, Error }

public sealed record AgentActivityEvent(
    DateTimeOffset Timestamp,
    AgentActivityKind Kind,
    string Title,
    string Summary,
    string? Details = null,
    long? DurationMilliseconds = null,
    AgentActivityTone Tone = AgentActivityTone.Neutral,
    OpenAiApiUsage? ApiUsage = null);

public sealed record OpenAiApiUsage(
    string Model,
    string ServiceTier,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteTokens,
    long OutputTokens,
    decimal EstimatedCostUsd);

public interface IAgentActivitySink
{
    void Publish(AgentActivityEvent activity);
}

/// <summary>Final defense for diagnostic text shown by the activity inspector.</summary>
public static partial class AgentActivitySanitizer
{
    public const int MaximumDetailLength = 4000;

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var redacted = BearerTokenRegex().Replace(value, "$1[credential]");
        redacted = ApiKeyRegex().Replace(redacted, "[credential]");
        redacted = IpCandidateRegex().Replace(redacted, match =>
            IPAddress.TryParse(match.Value.Trim('[', ']'), out _) ? "[network-address]" : match.Value);
        redacted = MacAddressRegex().Replace(redacted, "[hardware-address]");
        redacted = PipeNameRegex().Replace(redacted, "AirBridge.[pipe]");
        redacted = WindowsPathRegex().Replace(redacted, "[local-path]");
        return redacted.Length <= MaximumDetailLength ? redacted : redacted[..MaximumDetailLength] + "\n…truncated";
    }

    [GeneratedRegex("""(?i)("?authorization"?\s*[:=]\s*"?bearer\s+)[^\s",}]+""", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{12,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"(?<![\w:])(?:\[[0-9A-Fa-f:.%]+\]|(?:\d{1,3}\.){3}\d{1,3}|[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7})(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex IpCandidateRegex();

    [GeneratedRegex(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex(@"airbridge(?:\.pcm\.\d+\.[0-9a-f]{16,}|-[0-9a-f]{16,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PipeNameRegex();

    [GeneratedRegex("""(?<![\w])(?:[A-Za-z]:\\|\\\\)[^\s\"<>|]+""", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();
}
