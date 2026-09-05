using System.Text.Json;
using AirBridge.Core;
using Xunit;
using Xunit.Abstractions;

namespace AirBridge.Tests;

public sealed class MicAuthorizationEvalTests(ITestOutputHelper output)
{
    private static readonly string[] MicrophoneTools = ["align_group", "measure_acoustic_delay"];

    // The original gate measured 23 false positives in this fixture. Because a
    // false positive bypasses the microphone dialog, the ratcheted budget is zero.
    private const int FalsePositiveBudget = 0;
    // Safe false negatives fall back to the dialog, but a small budget prevents
    // an always-refuse implementation from satisfying the safety gate.
    private const int FalseNegativeBudget = 2;

    [Fact]
    public void DirectMicrophoneAuthorizationMeetsTheFalsePositiveBudget()
    {
        var cases = LoadCases("mic-authorization.jsonl", "mic-authorization-heldout.jsonl");
        var results = cases.Select(Evaluate).ToArray();
        var truePositives = results.Count(result => result.Expected && result.ExpectedToolAuthorized);
        var falsePositives = results.SelectMany(result => result.UnexpectedTools.Select(tool => new FalsePositive(result.Case, tool))).ToArray();
        var falseNegatives = results.Where(result => result.Expected && !result.ExpectedToolAuthorized).ToArray();
        var accuracy = Ratio(results.Count(result => result.Correct), results.Length);
        var precision = Ratio(truePositives, truePositives + falsePositives.Length);
        var recall = Ratio(truePositives, truePositives + falseNegatives.Length);

        WriteScorecard(results, accuracy, precision, recall, falsePositives, falseNegatives);

        Assert.True(falsePositives.Length <= FalsePositiveBudget,
            $"Microphone authorization produced {falsePositives.Length} false positives; budget is {FalsePositiveBudget}.");
        Assert.True(falseNegatives.Length <= FalseNegativeBudget,
            $"Microphone authorization produced {falseNegatives.Length} false negatives; budget is {FalseNegativeBudget}.");
    }

    private static EvalResult Evaluate(MicAuthorizationCase testCase)
    {
        var authorization = DirectMicrophoneAuthorization.FromUserText(testCase.Utterance);
        var authorizedTools = MicrophoneTools.Where(authorization.TryConsume).ToArray();
        foreach (var tool in MicrophoneTools) Assert.False(authorization.TryConsume(tool));
        var expected = testCase.Expect == "authorize";
        var expectedToolAuthorized = authorizedTools.Contains(testCase.Tool, StringComparer.Ordinal);
        var unexpectedTools = authorizedTools.Where(tool => !expected || tool != testCase.Tool).ToArray();
        var correct = expected ? expectedToolAuthorized && unexpectedTools.Length == 0 : authorizedTools.Length == 0;
        return new(testCase, expected, expectedToolAuthorized, unexpectedTools, correct);
    }

    private void WriteScorecard(IReadOnlyCollection<EvalResult> results, string accuracy, string precision, string recall,
        IReadOnlyCollection<FalsePositive> falsePositives, IReadOnlyCollection<EvalResult> falseNegatives)
    {
        var heldOutCases = results.Count(result => result.Case.Tags.Contains("heldout", StringComparer.Ordinal));
        var lines = new List<string>
        {
            "Microphone authorization eval",
            $"  cases: {results.Count} ({heldOutCases} externally sourced held-out)",
            $"  accuracy: {accuracy}",
            $"  authorize precision: {precision}",
            $"  authorize recall: {recall}",
            $"  false positives: {falsePositives.Count} (budget: {FalsePositiveBudget})",
            $"  false negatives: {falseNegatives.Count} (budget: {FalseNegativeBudget})"
        };
        lines.AddRange(falsePositives.Select(result =>
            $"  FP [{result.Case.Id}] unexpected={result.Tool} ({string.Join(',', result.Case.Tags)}): {result.Case.Utterance}"));
        lines.AddRange(falseNegatives.Select(result =>
            $"  FN [{result.Case.Id}] ({string.Join(',', result.Case.Tags)}): {result.Case.Utterance}"));
        var scorecard = string.Join(Environment.NewLine, lines);
        output.WriteLine(scorecard);
        Console.WriteLine(scorecard);
    }

    private static string Ratio(int numerator, int denominator) => denominator == 0 ? "n/a" : ((double)numerator / denominator).ToString("P1");

    private static MicAuthorizationCase[] LoadCases(params string[] fixtureNames)
    {
        var lines = fixtureNames.SelectMany(fixtureName =>
        {
            var fixtureLines = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName))
                .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            if (fixtureLines.Length == 0)
                throw new InvalidDataException($"Microphone authorization fixture '{fixtureName}' must not be empty.");
            return fixtureLines;
        });
        var cases = lines
            .Select(line => JsonSerializer.Deserialize<MicAuthorizationCase>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                throw new InvalidDataException($"Invalid eval fixture line: {line}"))
            .ToArray();
        if (cases.Select(testCase => testCase.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length)
            throw new InvalidDataException("Microphone authorization eval IDs must be unique.");
        foreach (var testCase in cases)
        {
            if (testCase.Expect is not ("authorize" or "refuse"))
                throw new InvalidDataException($"{testCase.Id} has invalid expectation '{testCase.Expect}'.");
            if (testCase.Tool is not ("align_group" or "measure_acoustic_delay"))
                throw new InvalidDataException($"{testCase.Id} has invalid microphone tool '{testCase.Tool}'.");
            if (testCase.Tags.Length == 0 || string.IsNullOrWhiteSpace(testCase.Note))
                throw new InvalidDataException($"{testCase.Id} must have tags and a note.");
        }
        foreach (var tool in MicrophoneTools)
        {
            if (cases.Count(testCase => testCase.Tool == tool && testCase.Expect == "authorize") <= FalseNegativeBudget ||
                !cases.Any(testCase => testCase.Tool == tool && testCase.Expect == "refuse"))
                throw new InvalidDataException($"Microphone authorization fixtures must include more than {FalseNegativeBudget} authorize cases and at least one refuse case for '{tool}'.");
        }
        return cases;
    }

    private sealed record MicAuthorizationCase(string Id, string Utterance, string Expect, string Tool, string[] Tags, string Note);
    private sealed record EvalResult(MicAuthorizationCase Case, bool Expected, bool ExpectedToolAuthorized, string[] UnexpectedTools, bool Correct);
    private sealed record FalsePositive(MicAuthorizationCase Case, string Tool);
}
