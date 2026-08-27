using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AirBridge.Core;
using Xunit;
using Xunit.Abstractions;

namespace AirBridge.Tests;

public sealed class ModelToolSelectionEvalTests(ITestOutputHelper output)
{
    private const string Instructions = """
        You are evaluating AirBridge tool selection. The complete synthetic state is: receivers receiver-1 and receiver-2 are known and active; stream-1 is the current stream; process 4242 is an active audio application. Decide whether the user's current utterance requests exactly one AirBridge operation. For an actionable request, call exactly one matching tool with the stated aliases and values. Do not call discovery or telemetry tools first because the synthetic state is complete. For an informational, negated, hypothetical, privacy-invasive, or out-of-scope request, make no tool call and briefly explain or ask a question. Never invent a tool or identifier.
        """;

    [Fact]
    public async Task ModelToolSelectionArgumentsAndRefusalsMatchTheFixtureWhenExplicitlyEnabled()
    {
        var cases = LoadCases("model-tool-selection.jsonl");
        if (Environment.GetEnvironmentVariable("AIRBRIDGE_MODEL_EVALS") != "1")
        {
            WriteSkip(cases.Length, "Set AIRBRIDGE_MODEL_EVALS=1 to run paid model evals.");
            return;
        }
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            WriteSkip(cases.Length, "Set OPENAI_API_KEY to run paid model evals.");
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var results = new List<ModelEvalResult>();
        foreach (var testCase in cases)
            results.Add(await EvaluateAsync(http, testCase));

        WriteScorecard(results);

        var failures = results.Where(result => !result.SelectionCorrect || !result.ArgumentsCorrect || !result.RefusalCorrect).ToArray();
        Assert.True(failures.Length == 0, $"Model tool-selection eval had {failures.Length} failing cases.");
    }

    private void WriteSkip(int caseCount, string reason)
    {
        var message = $"Model tool-selection eval ({OpenAiAgent.Model}){Environment.NewLine}  cases: {caseCount}{Environment.NewLine}  SKIPPED: {reason}";
        output.WriteLine(message);
        Console.WriteLine(message);
    }

    private static async Task<ModelEvalResult> EvaluateAsync(HttpClient http, ModelToolCase testCase)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = OpenAiAgent.Model,
            ["instructions"] = Instructions,
            ["input"] = testCase.Utterance,
            ["tools"] = OpenAiAgent.ToolDefinitions,
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = false,
            ["reasoning"] = new { effort = "low" },
            ["max_output_tokens"] = 1500,
            ["store"] = false
        };
        using var response = await http.PostAsync("v1/responses",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Model eval request failed for {testCase.Id} with HTTP {(int)response.StatusCode}: {ReadError(raw)}");

        using var document = JsonDocument.Parse(raw);
        var calls = ReadFunctionCalls(document.RootElement).ToArray();
        var outputText = ReadOutputText(document.RootElement);
        var expectsRefusal = testCase.ExpectedTool is null;
        var selectionCorrect = expectsRefusal || calls.Length == 1 && calls[0].Name == testCase.ExpectedTool;
        var argumentsCorrect = expectsRefusal || selectionCorrect &&
            JsonEquivalent(testCase.ExpectedArguments, calls[0].Arguments);
        var refusalCorrect = !expectsRefusal || calls.Length == 0 && !string.IsNullOrWhiteSpace(outputText);
        return new(testCase, calls, outputText, selectionCorrect, argumentsCorrect, refusalCorrect);
    }

    private void WriteScorecard(IReadOnlyCollection<ModelEvalResult> results)
    {
        var actionable = results.Where(result => result.Case.ExpectedTool is not null).ToArray();
        var refusals = results.Where(result => result.Case.ExpectedTool is null).ToArray();
        var selectionCorrect = actionable.Count(result => result.SelectionCorrect);
        var argumentsCorrect = actionable.Count(result => result.ArgumentsCorrect);
        var refusalCorrect = refusals.Count(result => result.RefusalCorrect);
        var lines = new List<string>
        {
            $"Model tool-selection eval ({OpenAiAgent.Model})",
            $"  cases: {results.Count}",
            $"  tool selection accuracy: {Ratio(selectionCorrect, actionable.Length):P1} ({selectionCorrect}/{actionable.Length})",
            $"  argument correctness: {Ratio(argumentsCorrect, actionable.Length):P1} ({argumentsCorrect}/{actionable.Length})",
            $"  refusal correctness: {Ratio(refusalCorrect, refusals.Length):P1} ({refusalCorrect}/{refusals.Length})"
        };
        lines.AddRange(results.Where(result => !result.SelectionCorrect).Select(result =>
            $"  TOOL [{result.Case.Id}] expected={result.Case.ExpectedTool} actual={FormatCalls(result.Calls)}: {result.Case.Utterance}"));
        lines.AddRange(results.Where(result => !result.ArgumentsCorrect).Select(result =>
            $"  ARGS [{result.Case.Id}] expected={result.Case.ExpectedArguments.GetRawText()} actual={FormatCalls(result.Calls)}"));
        lines.AddRange(results.Where(result => !result.RefusalCorrect).Select(result =>
            $"  REFUSAL [{result.Case.Id}] calls={FormatCalls(result.Calls)} text={(string.IsNullOrWhiteSpace(result.OutputText) ? "missing" : "present")}: {result.Case.Utterance}"));
        var scorecard = string.Join(Environment.NewLine, lines);
        output.WriteLine(scorecard);
        Console.WriteLine(scorecard);
    }

    private static IEnumerable<ModelCall> ReadFunctionCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputItems)) yield break;
        foreach (var item in outputItems.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "function_call") continue;
            using var arguments = JsonDocument.Parse(item.GetProperty("arguments").GetString() ?? "{}");
            yield return new(item.GetProperty("name").GetString() ?? string.Empty, arguments.RootElement.Clone());
        }
    }

    private static string ReadError(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Unknown API error";
        }
        catch (JsonException)
        {
            return "Unparseable API error";
        }
    }

    private static string? ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct)) return direct.GetString();
        if (!root.TryGetProperty("output", out var outputItems)) return null;
        foreach (var item in outputItems.EnumerateArray())
            if (item.TryGetProperty("content", out var content))
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text")
                        return part.GetProperty("text").GetString();
        return null;
    }

    private static string FormatCalls(IReadOnlyCollection<ModelCall> calls) => calls.Count == 0
        ? "none"
        : string.Join("; ", calls.Select(call => $"{call.Name} {call.Arguments.GetRawText()}"));

    private static bool JsonEquivalent(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var expectedProperties = expected.EnumerateObject().ToArray();
            var actualProperties = actual.EnumerateObject().ToArray();
            return expectedProperties.Length == actualProperties.Length && expectedProperties.All(property =>
                actual.TryGetProperty(property.Name, out var actualValue) && JsonEquivalent(property.Value, actualValue));
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            var expectedItems = expected.EnumerateArray().ToArray();
            var actualItems = actual.EnumerateArray().ToArray();
            return expectedItems.Length == actualItems.Length &&
                expectedItems.Zip(actualItems).All(pair => JsonEquivalent(pair.First, pair.Second));
        }
        return expected.GetRawText() == actual.GetRawText();
    }

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 1 : (double)numerator / denominator;

    private static ModelToolCase[] LoadCases(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
        var cases = File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<ModelToolCase>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                throw new InvalidDataException($"Invalid eval fixture line: {line}"))
            .ToArray();
        if (cases.Select(testCase => testCase.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length)
            throw new InvalidDataException("Model eval IDs must be unique.");
        var knownTools = OpenAiAgent.ToolDefinitions
            .Select(definition => JsonSerializer.SerializeToElement(definition).GetProperty("name").GetString() ??
                throw new InvalidDataException("A production tool definition is missing its name."))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var testCase in cases)
        {
            if (testCase.ExpectedArguments.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(testCase.Category) ||
                string.IsNullOrWhiteSpace(testCase.Note))
                throw new InvalidDataException($"{testCase.Id} must have an argument object, category, and note.");
            if (testCase.ExpectedTool is not null && !knownTools.Contains(testCase.ExpectedTool))
                throw new InvalidDataException($"{testCase.Id} names unknown tool '{testCase.ExpectedTool}'.");
        }
        return cases;
    }

    private sealed record ModelToolCase(string Id, string Utterance, string? ExpectedTool, JsonElement ExpectedArguments, string Category, string Note);
    private sealed record ModelCall(string Name, JsonElement Arguments);
    private sealed record ModelEvalResult(ModelToolCase Case, ModelCall[] Calls, string? OutputText, bool SelectionCorrect, bool ArgumentsCorrect, bool RefusalCorrect);
}
