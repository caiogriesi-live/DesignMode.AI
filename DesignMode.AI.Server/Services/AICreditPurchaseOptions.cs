using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Server.Services;

public sealed record CreditPackage(string Id, int Credits, long AmountMinor, string PaymentLink, string PaymentLinkId, string PriceId);

public sealed class AICreditPurchaseOptions(IConfiguration configuration)
{
    private string? Read(string key) => configuration[key] ?? configuration[$"Values:{key}"];
    public bool IsTest => Read("AI_CREDITS_MODE") == "Test";
    public bool Enabled => bool.TryParse(Read("AI_CREDITS_PURCHASES_ENABLED"), out var enabled) && enabled &&
        bool.TryParse(Read("AI_CREDITS_LEDGER_ENABLED"), out var ledger) && ledger && ModeMatchesKey;
    private bool ModeMatchesKey => Read("AI_CREDITS_MODE") switch
    {
        "Test" => (!string.Equals(Read("AZURE_FUNCTIONS_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase) ||
            bool.TryParse(Read("AI_CREDITS_ALLOW_HOSTED_TEST_PAYMENTS"), out var allowHostedTest) && allowHostedTest) && IsKey("test"),
        "Live" => IsKey("live"),
        _ => false
    };
    private bool IsKey(string mode) => Read("StripeSecretKey") is { } key &&
        (key.StartsWith($"sk_{mode}_", StringComparison.Ordinal) || key.StartsWith($"rk_{mode}_", StringComparison.Ordinal));

    public IReadOnlyList<CreditPackage> Packages => AICreditsSandboxCatalog.Offers
        .Where(x => x.SubscriptionPlan is null).Select(x =>
        {
            var prefix = $"AI_CREDITS_{(IsTest ? "TEST" : "LIVE")}_{(int)x.Credits}";
            return new CreditPackage(x.Id, (int)x.Credits, (long)x.Price * 100,
                Read(prefix + "_PAYMENT_LINK") ?? (IsTest ? x.PaymentLink : ""),
                Read(prefix + "_PAYMENT_LINK_ID") ?? "", Read(prefix + "_PRICE_ID") ?? "");
        }).ToList();

    public bool IsConfigured(CreditPackage pack) => Enabled && IsFulfillmentConfigured(pack);
    public bool IsFulfillmentConfigured(CreditPackage pack) => ModeMatchesKey &&
        pack.PaymentLinkId.StartsWith("plink_", StringComparison.Ordinal) && pack.PriceId.StartsWith("price_", StringComparison.Ordinal) &&
        Uri.TryCreate(pack.PaymentLink, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "buy.stripe.com" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath.StartsWith("/test_", StringComparison.Ordinal) == IsTest;
}
