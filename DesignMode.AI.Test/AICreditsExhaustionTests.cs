using System.Net;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AICreditsExhaustionTests
{
    internal static AIUsageSummary Summary(decimal remaining = 0, string plan = "starter") => new()
    {
        Plan = plan, GenerationLimit = 20, GenerationsUsed = 20 - remaining,
        GenerationsRemaining = remaining, BillingPeriodStart = DateTimeOffset.Parse("2026-09-01T00:00:00Z")
    };

    [DataTestMethod]
    [DataRow(1, false)]
    [DataRow(0, true)]
    [DataRow(-1, true)]
    public void Summary_TriggersOnlyExhaustedBalance(int remaining, bool expected)
    {
        var state = new AIUsageRefreshService();
        state.SetSummary(Summary(remaining));
        Assert.AreEqual(expected, state.IsModalOpen);
    }

    [TestMethod]
    public void UnknownAndUnlimitedSummary_DoNotOpen()
    {
        var state = new AIUsageRefreshService();
        state.SetSummary(null);
        Assert.IsFalse(state.IsModalOpen);
        var summary = Summary();
        summary.GenerationLimit = null;
        summary.GenerationsRemaining = null;
        state.SetSummary(summary);
        Assert.IsFalse(state.IsModalOpen);
    }

    [TestMethod]
    public void DismissedModal_StaysClosedUntilExplicitRetryOrNewPeriod()
    {
        var state = new AIUsageRefreshService();
        state.SetSummary(Summary());
        state.Close();
        state.SetSummary(Summary());
        Assert.IsFalse(state.IsModalOpen);
        state.Open();
        Assert.IsTrue(state.IsModalOpen);
        state.Close();
        var next = Summary();
        next.BillingPeriodStart = next.BillingPeriodStart.AddMonths(1);
        state.SetSummary(next);
        Assert.IsTrue(state.IsModalOpen);
    }

    [DataTestMethod]
    [DataRow(429, "{\"code\":\"ai_generation_limit_exhausted\"}", true)]
    [DataRow(429, "{\"code\":\"rate_limit\"}", false)]
    [DataRow(429, "{\"code\":123}", false)]
    [DataRow(429, "[]", false)]
    [DataRow(429, "Too many requests", false)]
    [DataRow(429, "{}", false)]
    [DataRow(400, "{\"code\":\"ai_generation_limit_exhausted\"}", false)]
    public async Task Response_RequiresExactQuotaCodeAnd429(int status, string body, bool expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        var state = new AIUsageRefreshService();
        var refreshed = 0;
        state.RefreshRequested += () => { refreshed++; state.SetSummary(Summary(1)); return Task.CompletedTask; };
        Assert.AreEqual(expected, await state.HandleResponseAsync(response));
        Assert.AreEqual(expected, state.IsModalOpen);
        Assert.AreEqual(expected ? 1 : 0, refreshed);
        Assert.AreEqual(body, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task QuotaRejection_StillOpensWhenSummaryRefreshFails()
    {
        var state = new AIUsageRefreshService();
        state.RefreshRequested += () => throw new HttpRequestException();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("{\"code\":\"ai_generation_limit_exhausted\"}") };
        Assert.IsTrue(await state.HandleResponseAsync(response));
        Assert.IsTrue(state.IsModalOpen);
        Assert.IsNull(state.Summary);
    }

    [TestMethod]
    public async Task EntitlementRefresh_UpdatesSharedBalanceAndClosesWithoutGenerating()
    {
        var state = new AIUsageRefreshService();
        state.SetSummary(Summary());
        var refreshed = 0;
        state.RefreshRequested += () => { refreshed++; state.SetSummary(Summary(10, "plus")); return Task.CompletedTask; };
        await state.RequestRefreshAsync();
        Assert.AreEqual(1, refreshed);
        Assert.AreEqual(10m, state.Summary!.GenerationsRemaining);
        Assert.AreEqual("plus", state.Summary.Plan);
        Assert.IsFalse(state.IsModalOpen);
    }

    [DataTestMethod]
    [DataRow("starter", "plus,extreme")]
    [DataRow("plus", "extreme")]
    [DataRow("pro", "extreme")]
    [DataRow("extreme", "")]
    [DataRow("unknown", "")]
    public void ServerSummaryPlan_SelectsOnlyHigherCanonicalTiers(string plan, string expected)
    {
        var configuration = new ConfigurationBuilder().Build();
        var options = AICreditsRecoveryConfiguration.ForSummary(Summary(plan: plan), configuration, AIUsageQuotaOptions.FromConfiguration(configuration));
        Assert.AreEqual(expected, string.Join(',', options.Upgrades.Select(x => x.Plan)));
        Assert.IsTrue(options.Upgrades.All(x => !x.CanUpgrade));
    }

    [TestMethod]
    public void UpgradeLabelsAndAllowances_UseRequiredPricesAndCanonicalQuotaConfig()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AI_GENERATION_LIMIT_PLUS"] = "45", ["AI_GENERATION_LIMIT_EXTREME"] = "90" }).Build();
        var options = AICreditsRecoveryConfiguration.ForSummary(Summary(), configuration, AIUsageQuotaOptions.FromConfiguration(configuration));
        Assert.AreEqual(new AICreditsUpgradeOption("plus", "Plus", 79, 45), options.Upgrades[0]);
        Assert.AreEqual(new AICreditsUpgradeOption("extreme", "Extreme", 129, 90), options.Upgrades[1]);
    }

    [DataTestMethod]
    [DataRow("false", "10", "price_test", "$10", false)]
    [DataRow("true", "0", "price_test", "$10", false)]
    [DataRow("true", "10", "", "$10", false)]
    [DataRow("true", "10", "price_test", "", false)]
    [DataRow("true", "10", "price_test", "$10", true)]
    public void CreditPack_RequiresCompleteConfigurationAndStillCannotGrantEntitlement(string enabled, string units, string priceId, string display, bool configured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_CREDIT_PACK_ENABLED"] = enabled, ["AI_CREDIT_PACK_UNITS"] = units,
            ["AI_CREDIT_PACK_STRIPE_PRICE_ID"] = priceId, ["AI_CREDIT_PACK_DISPLAY_PRICE"] = display
        }).Build();
        var summary = Summary();
        var options = AICreditsRecoveryConfiguration.ForSummary(summary, configuration, AIUsageQuotaOptions.FromConfiguration(configuration));
        Assert.AreEqual(configured, options.CreditPackConfigured);
        Assert.IsFalse(options.CanPurchaseCredits);
        Assert.AreEqual(configured ? 10m : (decimal?)null, options.CreditPackUnits);
        Assert.AreEqual(0m, summary.GenerationsRemaining);
        Assert.AreEqual("starter", summary.Plan);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(options).Contains("price_test"));
    }
}
