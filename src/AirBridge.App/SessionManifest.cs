using System.Text.Json;
using AirBridge.Core;

namespace AirBridge.App;

internal sealed class SessionManifest(LaunchOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    internal bool Ready { get; private set; }
    internal string State { get; private set; } = "initializing";
    internal string? Error { get; private set; }
    internal object Snapshot() => new
    {
        pid = Environment.ProcessId,
        runId = RuntimeProfile.RunId,
        executable = Environment.ProcessPath,
        assemblyName = typeof(Program).Assembly.GetName().Name,
        baseDirectory = AppContext.BaseDirectory,
        version = typeof(Program).Assembly.GetName().Version?.ToString(),
        buildCommit = Environment.GetEnvironmentVariable("AIRBRIDGE_BUILD_COMMIT"),
        profile = RuntimeProfile.DataDirectory,
        startedUtc = _startedUtc,
        updatedUtc = DateTimeOffset.UtcNow,
        mode = options.FixtureScenario is not null ? "fixture" : "live",
        scenario = options.FixtureScenario,
        debugPipe = options.DebugPipe,
        ready = Ready,
        state = State,
        error = Error
    };

    internal void Update(string state, bool ready = false, string? error = null)
    {
        State = state;
        Ready = ready;
        Error = error is null ? null : AgentActivitySanitizer.Sanitize(error);
        if (options.SessionFile is not { } path) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Snapshot(), JsonOptions));
        File.Move(temporary, path, true);
    }
}
