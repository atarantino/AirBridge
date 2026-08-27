using System.Text.Json;
using AirBridge.Core;
using Xunit;
using Xunit.Abstractions;

namespace AirBridge.Tests;

public sealed class MicAuthorizationEvalTests(ITestOutputHelper output)
{
    // The original gate measured 23 false positives in this fixture. Because a
    // false positive bypasses the microphone dialog, the ratcheted budget is zero.
    private const int FalsePositiveBudget = 0;

    [Fact]
    public void DirectMicrophoneAuthorizationMeetsTheFalsePositiveBudget()
    {
        var cases = LoadCases("mic-authorization.jsonl");
        var results = cases.Select(Evaluate).ToArray();
        var truePositives = results.Count(result => result.Expected && result.Actual);
        var trueNegatives = results.Count(result => !result.Expected && !result.Actual);
        var falsePositives = results.Where(result => !result.Expected && result.Actual).ToArray();
        var falseNegatives = results.Where(result => result.Expected && !result.Actual).ToArray();
        var accuracy = Ratio(truePositives + trueNegatives, results.Length);
        var precision = Ratio(truePositives, truePositives + falsePositives.Length);
        var recall = Ratio(truePositives, truePositives + falseNegatives.Length);

        WriteScorecard(results.Length, accuracy, precision, recall, falsePositives, falseNegatives);

        Assert.True(falsePositives.Length <= FalsePositiveBudget,
            $"Microphone authorization produced {falsePositives.Length} false positives; budget is {FalsePositiveBudget}.");
    }

    private static EvalResult Evaluate(MicAuthorizationCase testCase)
    {
        var authorization = DirectMicrophoneAuthorization.FromUserText(testCase.Utterance);
        var actual = authorization.TryConsume(testCase.Tool);
        Assert.False(authorization.TryConsume(testCase.Tool));
        return new(testCase, testCase.Expect == "authorize", actual);
    }

    private void WriteScorecard(int total, double accuracy, double precision, double recall,
        IReadOnlyCollection<EvalResult> falsePositives, IReadOnlyCollection<EvalResult> falseNegatives)
    {
        var lines = new List<string>
        {
            "Microphone authorization eval",
            $"  cases: {total}",
            $"  accuracy: {accuracy:P1}",
            $"  authorize precision: {precision:P1}",
            $"  authorize recall: {recall:P1}",
            $"  false positives: {falsePositives.Count} (budget: {FalsePositiveBudget})",
            $"  false negatives: {falseNegatives.Count}"
        };
        lines.AddRange(falsePositives.Select(result =>
            $"  FP [{result.Case.Id}] ({string.Join(',', result.Case.Tags)}): {result.Case.Utterance}"));
        lines.AddRange(falseNegatives.Select(result =>
            $"  FN [{result.Case.Id}] ({string.Join(',', result.Case.Tags)}): {result.Case.Utterance}"));
        var scorecard = string.Join(Environment.NewLine, lines);
        output.WriteLine(scorecard);
        Console.WriteLine(scorecard);
    }

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 1 : (double)numerator / denominator;

    private static MicAuthorizationCase[] LoadCases(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
        var cases = File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
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
        return cases;
    }

    private sealed record MicAuthorizationCase(string Id, string Utterance, string Expect, string Tool, string[] Tags, string Note);
    private sealed record EvalResult(MicAuthorizationCase Case, bool Expected, bool Actual);
}
