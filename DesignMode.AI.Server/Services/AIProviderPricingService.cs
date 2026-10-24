namespace DesignMode.AI.Server.Services;

public sealed class AIProviderPricingService
{
    public const string CurrentVersion = "2026-09-03.v3";

    // Standard USD / million tokens, verified 2026-08-28:
    // https://developers.openai.com/api/docs/models/{model}
    // v1 rows are not backfilled. Null cached rate means no cache discount.
    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal? CachedInput, decimal Output)> OpenAi =
        new Dictionary<string, (decimal, decimal?, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6"] = (4.00m, 0.40m, 20.00m),
            ["gpt-5.6-sol"] = (4.00m, 0.40m, 20.00m),
            ["gpt-5.6-terra"] = (2.00m, 0.20m, 12.00m),
            ["gpt-5.6-luna"] = (0.20m, 0.02m, 1.20m),
            ["gpt-3.5-turbo"] = (0.50m, null, 1.50m),
            ["gpt-4.1-mini"] = (0.40m, 0.10m, 1.60m),
            ["gpt-4.1"] = (2.00m, 0.50m, 8.00m),
            ["gpt-4o-mini"] = (0.15m, 0.075m, 0.60m),
            ["gpt-4o"] = (2.50m, 1.25m, 10.00m)
        };

    // Explicit allowlist: unknown/older/fine-tuned snapshots are not priced by substring.
    private static readonly IReadOnlyDictionary<string, string> Snapshots = new Dictionary<string, string>
    {
        ["gpt-3.5-turbo-0125"] = "gpt-3.5-turbo",
        ["gpt-4.1-2025-04-14"] = "gpt-4.1",
        ["gpt-4.1-mini-2025-04-14"] = "gpt-4.1-mini",
        ["gpt-4o-mini-2024-07-18"] = "gpt-4o-mini",
        ["gpt-4o-2024-08-06"] = "gpt-4o",
        ["gpt-4o-2024-11-20"] = "gpt-4o"
    };

    public static string NormalizeModel(string? model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        return Snapshots.TryGetValue(normalized, out var family) ? family : normalized;
    }

    public bool HasOpenAiPricing(string model) => OpenAi.ContainsKey(NormalizeModel(model));

    public decimal? EstimateOpenAiText(string model, long? inputTokens, long? cachedInputTokens, long? outputTokens)
    {
        if (!OpenAi.TryGetValue(NormalizeModel(model), out var price) ||
            !inputTokens.HasValue || !outputTokens.HasValue || inputTokens < 0 || outputTokens < 0 ||
            cachedInputTokens < 0 || cachedInputTokens > inputTokens) return null;
        var cached = price.CachedInput.HasValue ? cachedInputTokens ?? 0 : 0;
        return ((inputTokens.Value - cached) * price.Input + cached * (price.CachedInput ?? price.Input) +
            outputTokens.Value * price.Output) / 1_000_000m;
    }
}
