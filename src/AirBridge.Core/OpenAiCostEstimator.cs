using System.Text.Json;

namespace AirBridge.Core;

/// <summary>Estimates standard Responses API token charges using the embedded OpenAI rate card.</summary>
public static class OpenAiCostEstimator
{
    public const string PricingUrl = "https://developers.openai.com/api/docs/pricing";
    public static readonly DateOnly RateCardDate = new(2026, 7, 18);

    public static OpenAiApiUsage? FromResponse(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage)) return null;
        var model = response.TryGetProperty("model", out var modelProperty)
            ? modelProperty.GetString() ?? OpenAiAgent.Model
            : OpenAiAgent.Model;
        var serviceTier = response.TryGetProperty("service_tier", out var tierProperty)
            ? tierProperty.GetString() ?? "default"
            : "default";
        if (!TryGetRates(model, serviceTier, out var rates)) return null;

        var input = ReadInt64(usage, "input_tokens");
        var output = ReadInt64(usage, "output_tokens");
        var cached = 0L;
        var cacheWrite = 0L;
        if (usage.TryGetProperty("input_tokens_details", out var details))
        {
            cached = ReadInt64(details, "cached_tokens");
            cacheWrite = ReadInt64(details, "cache_write_tokens");
        }
        var uncached = Math.Max(0, input - cached - cacheWrite);
        var cost = (uncached * rates.Input + cached * rates.CachedInput +
            cacheWrite * rates.CacheWrite + output * rates.Output) / 1_000_000m;
        return new(model, NormalizeTier(serviceTier), input, cached, cacheWrite, output, cost);
    }

    public static OpenAiApiUsage? FromTranscriptionResponse(JsonElement response, string model = "gpt-4o-transcribe")
    {
        if (!response.TryGetProperty("usage", out var usage) ||
            !usage.TryGetProperty("type", out var type) || type.GetString() != "tokens") return null;
        var input = ReadInt64(usage, "input_tokens");
        var output = ReadInt64(usage, "output_tokens");
        var rates = model.Equals("gpt-4o-mini-transcribe", StringComparison.OrdinalIgnoreCase)
            ? new Rates(1.25m, 0, 0, 5m)
            : model.Equals("gpt-4o-transcribe", StringComparison.OrdinalIgnoreCase)
                ? new Rates(2.5m, 0, 0, 10m)
                : default;
        if (rates == default) return null;
        var cost = (input * rates.Input + output * rates.Output) / 1_000_000m;
        return new(model, "standard", input, 0, 0, output, cost);
    }

    public static string FormatUsd(decimal value) => value switch
    {
        >= 1m => $"${value:N2}",
        >= 0.01m => $"${value:N3}",
        _ => $"${value:N6}"
    };

    private static bool TryGetRates(string model, string serviceTier, out Rates rates)
    {
        var family = model.StartsWith("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase) ? "terra" :
            model.StartsWith("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase) ? "luna" :
            model.Equals("gpt-5.6", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase) ? "sol" : null;
        var tier = NormalizeTier(serviceTier);
        rates = (family, tier) switch
        {
            ("sol", "priority") => new(10m, 1m, 12.5m, 60m),
            ("terra", "priority") => new(5m, 0.5m, 6.25m, 30m),
            ("luna", "priority") => new(2m, 0.2m, 2.5m, 12m),
            ("sol", "flex") => new(2.5m, 0.25m, 3.125m, 15m),
            ("terra", "flex") => new(1.25m, 0.125m, 1.5625m, 7.5m),
            ("luna", "flex") => new(0.5m, 0.05m, 0.625m, 3m),
            ("sol", _) => new(5m, 0.5m, 6.25m, 30m),
            ("terra", _) => new(2.5m, 0.25m, 3.125m, 15m),
            ("luna", _) => new(1m, 0.1m, 1.25m, 6m),
            _ => default
        };
        return family is not null;
    }

    private static string NormalizeTier(string tier) => tier.Equals("priority", StringComparison.OrdinalIgnoreCase)
        ? "priority"
        : tier.Equals("flex", StringComparison.OrdinalIgnoreCase) ? "flex" : "standard";

    private static long ReadInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : 0;

    private readonly record struct Rates(decimal Input, decimal CachedInput, decimal CacheWrite, decimal Output);
}
