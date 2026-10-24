using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AICreditsSandboxCatalogTests
{
    [DataTestMethod]
    [DataRow("credits-starter", 20, 20, "test_eVqdR98eFeXbgS1fo25Rm08", null)]
    [DataRow("credits-pro", 40, 40, "test_cNidR9fH7cP3gS14Jo5Rm07", null)]
    [DataRow("credits-plus", 60, 60, "test_28E5kDdyZg1f6dnb7M5Rm06", null)]
    [DataRow("plus", 79, 80, "test_6oU7sL3YpbKZbxH5Ns5Rm0a", "plus")]
    [DataRow("extreme", 129, 130, "test_00w3cv8eF9CR59j1xc5Rm09", "extreme")]
    public void Catalog_PreservesSuppliedMapping(string id, int price, int credits, string link, string? plan)
    {
        var offer = AICreditsSandboxCatalog.Offers.Single(x => x.Id == id);
        Assert.AreEqual((decimal)price, offer.Price);
        Assert.AreEqual((decimal)credits, offer.Credits);
        Assert.AreEqual("https://buy.stripe.com/" + link, offer.PaymentLink);
        Assert.AreEqual(plan, offer.SubscriptionPlan);
    }

    [DataTestMethod]
    [DataRow("true", "sk_test_fixture", true)]
    [DataRow("true", "rk_test_fixture", true)]
    [DataRow("false", "sk_test_fixture", false)]
    [DataRow("true", "sk_live_fixture", false)]
    [DataRow("true", "", false)]
    [DataRow("", "sk_test_fixture", false)]
    public void Sandbox_RequiresOptInAndTestKey(string enabled, string key, bool expected)
    {
        var configuration = Configuration(enabled, key);
        var summary = AICreditsExhaustionTests.Summary();
        var result = AICreditsRecoveryConfiguration.ForSummary(summary, configuration, AIUsageQuotaOptions.FromConfiguration(configuration));
        Assert.AreEqual(expected, result.IsSandbox);
        Assert.AreEqual(expected ? 3 : 0, result.CreditPacks.Count);
        Assert.IsFalse(result.CanPurchaseCredits);
        Assert.IsTrue(result.Upgrades.All(x => !x.CanUpgrade));
        Assert.AreEqual(0m, summary.GenerationsRemaining);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(result).Contains("buy.stripe.com"));
    }

    [TestMethod]
    public void MonthlyAllowances_UseApprovedDefaultsAndPreserveExplicitOverrides()
    {
        var configuration = Configuration("true", "sk_test_fixture");
        var quotas = AIUsageQuotaOptions.FromConfiguration(configuration);
        Assert.AreEqual(80m, quotas.LimitFor("plus"));
        Assert.AreEqual(50m, quotas.LimitFor("pro"));
        Assert.AreEqual(130m, quotas.LimitFor("extreme"));
        var options = AICreditsRecoveryConfiguration.ForSummary(AICreditsExhaustionTests.Summary(), configuration, quotas);
        Assert.AreEqual(80m, options.Upgrades[0].IncludedCredits);
        Assert.AreEqual(130m, options.Upgrades[1].IncludedCredits);
        configuration["AI_GENERATION_LIMIT_PLUS"] = "95";
        Assert.AreEqual(95m, AIUsageQuotaOptions.FromConfiguration(configuration).LimitFor("plus"));
    }

    private static IConfigurationRoot Configuration(string enabled, string key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_CREDITS_SANDBOX_CATALOG_ENABLED"] = enabled,
            ["StripeSecretKey"] = key
        }).Build();
}
