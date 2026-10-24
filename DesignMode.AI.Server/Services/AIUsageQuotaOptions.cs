using Microsoft.Extensions.Configuration;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DesignMode.AI.Server.Services;

public sealed class AIUsageQuotaOptions
{
    private readonly IReadOnlyDictionary<string, decimal?> _values;
    private AIUsageQuotaOptions(IReadOnlyDictionary<string, decimal?> values) => _values = values;

    private static readonly IReadOnlyDictionary<string, decimal?> DefaultValues = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
    {
        ["AI_GENERATION_LIMIT_STARTER"] = 20,
        ["AI_GENERATION_LIMIT_PLUS"] = 80,
        ["AI_GENERATION_LIMIT_PRO"] = 50,
        ["AI_GENERATION_LIMIT_EXTREME"] = 130,
        ["AI_GENERATION_UNITS_IMAGE"] = 0.1m,
        ["AI_GENERATION_UNITS_WEBSITE"] = 5,
        ["AI_GENERATION_UNITS_SOCIAL_ASSET"] = 3,
        ["AI_GENERATION_UNITS_COMMERCIAL_VIDEO"] = 10,
        ["AI_GENERATION_UNITS_RECOMMENDATIONS"] = 1,
        ["AI_GENERATION_UNITS_PRINT_MATERIALS"] = 3,
        ["AI_GENERATION_UNITS_EVENTS_ACTIVATIONS"] = 3
    };

    public decimal? LimitFor(string plan) => Value($"AI_GENERATION_LIMIT_{plan.Trim().ToUpperInvariant()}");

    public decimal UnitsFor(string feature) => Value(feature switch
    {
        "gen_main_image" or "gen_secondary_image" or "social_image" or "branding_logo" or "digital_marketing" or "package_label_image" => "AI_GENERATION_UNITS_IMAGE",
        "website_generation" => "AI_GENERATION_UNITS_WEBSITE",
        "social_text" or "social_post" => "AI_GENERATION_UNITS_SOCIAL_ASSET",
        "ai_recommendations" => "AI_GENERATION_UNITS_RECOMMENDATIONS",
        "commercial_video" => "AI_GENERATION_UNITS_COMMERCIAL_VIDEO",
        "package_label" or "print_material" => "AI_GENERATION_UNITS_PRINT_MATERIALS",
        "event_activation" => "AI_GENERATION_UNITS_EVENTS_ACTIVATIONS",
        _ => string.Empty
    }) ?? 0;

    public static AIUsageQuotaOptions FromConfiguration(IConfiguration configuration)
    {
        var keys = new[]
        {
            "AI_GENERATION_LIMIT_STARTER", "AI_GENERATION_LIMIT_PLUS", "AI_GENERATION_LIMIT_PRO", "AI_GENERATION_LIMIT_EXTREME",
            "AI_GENERATION_UNITS_IMAGE", "AI_GENERATION_UNITS_WEBSITE",
            "AI_GENERATION_UNITS_SOCIAL_ASSET", "AI_GENERATION_UNITS_COMMERCIAL_VIDEO",
            "AI_GENERATION_UNITS_RECOMMENDATIONS", "AI_GENERATION_UNITS_PRINT_MATERIALS",
            "AI_GENERATION_UNITS_EVENTS_ACTIVATIONS"
        };
        return new(keys.ToDictionary(
            key => key,
            key => Parse(Read(configuration, key)) ?? DefaultValues[key],
            StringComparer.OrdinalIgnoreCase));
    }

    private decimal? Value(string key) => !string.IsNullOrEmpty(key) && _values.TryGetValue(key, out var value) ? value : null;
    private static string? Read(IConfiguration configuration, string key) =>
        configuration[key] ?? configuration[$"Values:{key}"];
    private static decimal? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0m ? value : null;
    }
}

public sealed record AIQuotaDecision(AIUsageSummary? Summary, decimal RequestedUnits, bool Allowed, string Reason);

public static class AIUsageQuotaRegistration
{
    public static IServiceCollection AddAIUsageQuotaOptions(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(AIUsageQuotaOptions.FromConfiguration(configuration));
}
