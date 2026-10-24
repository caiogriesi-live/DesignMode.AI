using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Server.Services;

// User-supplied sandbox catalog. These links are NOT authorization to grant credits
// and subscription Payment Links must not be used to upgrade existing subscriptions.
public static class AICreditsSandboxCatalog
{
    public sealed record Offer(string Id, string Name, decimal Price, decimal Credits,
        string PaymentLink, string? SubscriptionPlan = null);

    public static IReadOnlyList<Offer> Offers { get; } = Array.AsReadOnly(new[]
    {
        new Offer("credits-starter", "Credits Starter", 20, 20, "https://buy.stripe.com/test_eVqdR98eFeXbgS1fo25Rm08"),
        new Offer("credits-pro", "Credits Pro", 40, 40, "https://buy.stripe.com/test_cNidR9fH7cP3gS14Jo5Rm07"),
        new Offer("credits-plus", "Credits Plus", 60, 60, "https://buy.stripe.com/test_28E5kDdyZg1f6dnb7M5Rm06"),
        new Offer("plus", "Plus", 79, 80, "https://buy.stripe.com/test_6oU7sL3YpbKZbxH5Ns5Rm0a", "plus"),
        new Offer("extreme", "Extreme", 129, 130, "https://buy.stripe.com/test_00w3cv8eF9CR59j1xc5Rm09", "extreme")
    });

    public static bool IsEnabled(IConfiguration configuration)
    {
        string? Read(string key) => configuration[key] ?? configuration[$"Values:{key}"];
        var key = Read("StripeSecretKey");
        return bool.TryParse(Read("AI_CREDITS_SANDBOX_CATALOG_ENABLED"), out var enabled) && enabled
            && (key?.StartsWith("sk_test_", StringComparison.Ordinal) == true
                || key?.StartsWith("rk_test_", StringComparison.Ordinal) == true);
    }
}
