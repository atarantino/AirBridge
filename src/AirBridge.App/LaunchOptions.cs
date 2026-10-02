namespace AirBridge.App;

internal sealed record LaunchOptions(string[] Arguments, string? DataDirectory, string? FixtureScenario,
    string? DebugPipe, string? SessionFile, bool Help)
{
    internal bool IsPreview => Arguments.Length > 0;

    internal static LaunchOptions Parse(string[] args)
    {
        string? directory = null, fixture = null, pipe = null, session = null;
        var remaining = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var help = false;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--data-dir" or "--fixture" or "--debug-pipe" or "--session-file")
            {
                if (!seen.Add(option)) throw new ArgumentException($"Duplicate option: {option}.");
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{option} requires a value.");
                switch (option)
                {
                    case "--data-dir": directory = Path.GetFullPath(args[index]); break;
                    case "--fixture": fixture = args[index]; break;
                    case "--debug-pipe": pipe = args[index]; break;
                    case "--session-file": session = Path.GetFullPath(args[index]); break;
                }
            }
            else if (option is "--help" or "-h") help = true;
            else remaining.Add(option);
        }
        if (fixture is not null && !FixtureRuntime.KnownScenarios.Contains(fixture, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown fixture scenario: {fixture}.");
        if (fixture is not null && remaining.Count != 0)
            throw new ArgumentException("Fixture mode cannot be combined with preview/snapshot commands.");
        if (remaining.Count != 0)
        {
            var maximum = remaining[0] switch
            {
                "--preview" => 1,
                "--stress-flyout" => 2,
                "--snapshot" => 5,
                "--snapshot-flyout" => 4,
                "--snapshot-settings" or "--snapshot-hud" => 4,
                "--snapshot-activity" => 3,
                _ => throw new ArgumentException($"Unknown argument: {remaining[0]}. Use --help.")
            };
            if (remaining.Count > maximum || remaining.Any(value => value.StartsWith("--", StringComparison.Ordinal) && value != remaining[0]))
                throw new ArgumentException("Unexpected preview/snapshot argument.");
            if (remaining[0].StartsWith("--snapshot", StringComparison.Ordinal) && remaining.Count < 2)
                throw new ArgumentException("Snapshot commands require an output path.");
            if (pipe is not null || session is not null)
                throw new ArgumentException("Debug sessions require normal or fixture mode.");
        }
        if (pipe is not null && (pipe.Length > 128 || pipe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')))
            throw new ArgumentException("Debug pipe names must contain only ASCII letters, digits, dots, hyphens and underscores (maximum 128 characters).");
        return new(remaining.ToArray(), directory, fixture, pipe, session, help);
    }
}
