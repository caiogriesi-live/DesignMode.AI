using System.Globalization;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Server.Services;

public static class AICreditsRecoveryConfiguration
{
    public static AICreditsRecoveryOptions ForSummary(AIUsageSummary summary, IConfiguration configuration,
        AIUsageQuotaOptions quotas)
    {
        string? Read(string key) => configuration[key] ?? configuration[$"Values:{key}"];
        var configured = bool.TryParse(Read("AI_CREDIT_PACK_ENABLED"), out var enabled) && enabled
            && decimal.TryParse(Read("AI_CREDIT_PACK_UNITS"), NumberStyles.Number, CultureInfo.InvariantCulture, out var units) && units > 0
            && !string.IsNullOrWhiteSpace(Read("AI_CREDIT_PACK_STRIPE_PRICE_ID"))
            && !string.IsNullOrWhiteSpace(Read("AI_CREDIT_PACK_DISPLAY_PRICE"));
        var result = new AICreditsRecoveryOptions
        {
            CreditPackConfigured = configured,
            CreditPackUnits = configured ? decimal.Parse(Read("AI_CREDIT_PACK_UNITS")!, CultureInfo.InvariantCulture) : null,
            CreditPackDisplayPrice = configured ? Read("AI_CREDIT_PACK_DISPLAY_PRICE") : null
        };
        if (AICreditsSandboxCatalog.IsEnabled(configuration))
        {
            result.IsSandbox = true;
            result.CreditPacks = AICreditsSandboxCatalog.Offers
                .Where(x => x.SubscriptionPlan is null)
                .Select(x => new AICreditPackOption(x.Id, x.Name, x.Price, x.Credits)).ToList();
            // Keep payment URLs server-side until authenticated checkout and fulfillment exist.
        }
        var purchases = new AICreditPurchaseOptions(configuration);
        if (purchases.Enabled)
        {
            result.IsSandbox = purchases.IsTest;
            result.CreditPacks = purchases.Packages.Select(x => new AICreditPackOption(x.Id, $"{x.Credits} Credits", x.AmountMinor / 100m, x.Credits, purchases.IsConfigured(x))).ToList();
            result.CanPurchaseCredits = result.CreditPacks.Any(x => x.CanPurchase);
        }
        // Pro and Extreme are distinct tiers. Upgrades use the separately configured
        // confirmation flow for an existing subscription, never the signup links.
        var plan = summary.Plan.Trim().ToLowerInvariant();
        if (plan == "starter")
            result.Upgrades.Add(new("plus", "Plus", 79, quotas.LimitFor("plus")));
        if (plan is "starter" or "pro" or "plus")
            result.Upgrades.Add(new("extreme", "Extreme", 129, quotas.LimitFor("extreme")));
        var upgradePrefix = $"AI_UPGRADES_{(purchases.IsTest ? "TEST" : "LIVE")}";
        result.Upgrades = result.Upgrades.Select(x => x with { CanUpgrade = purchases.Enabled &&
            string.Equals(Read("MEASUREMENT_LEDGER_ENABLED"), "true", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(Read(upgradePrefix + "_PORTAL_CONFIGURATION_ID")) &&
            !string.IsNullOrWhiteSpace(Read(upgradePrefix + "_" + x.Plan.ToUpperInvariant() + "_PRICE_ID")) }).ToList();
        return result;
    }
}
