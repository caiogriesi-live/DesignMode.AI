using DesignMode.AI.Server.Services;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass, DoNotParallelize]
public sealed class UpgradeAccountingTests
{
    [DataTestMethod]
    [DataRow("valid", true)]
    [DataRow("other-user", false)]
    [DataRow("annual", false)]
    [DataRow("canceled", false)]
    [DataRow("live", false)]
    [DataRow("downgrade", false)]
    [DataRow("quantity", false)]
    public void Upgrade_RequiresOwnedActiveMonthlySubscription(string scenario, bool expected)
    {
        var subscription = new Stripe.Subscription
        {
            Status = "active", Metadata = new() { ["userId"] = scenario == "other-user" ? "other" : "owner" },
            Livemode = scenario == "live", CancelAtPeriodEnd = scenario == "canceled",
            Items = new() { Data = [new()
            {
                Quantity = scenario == "quantity" ? 2 : 1,
                Price = new() { Currency = "usd", UnitAmount = 4900,
                    Recurring = new() { Interval = scenario == "annual" ? "year" : "month", IntervalCount = 1 } }
            }] }
        };
        Assert.AreEqual(expected, DesignMode.AI.Server.AccountUpgradeFunction.IsEligible(subscription,
            new() { UnitAmount = scenario == "downgrade" ? 2900 : 12900 }, "owner", true));
    }

    [TestMethod]
    public void PaidUpgrade_KeepsOriginalPeriodAcrossRepeatedUpgrades()
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var initial = new PurchaseRecord { PaidAt = start, BillingInterval = "monthly" };
        var upgrade = initial with { PaidAt = start.AddDays(5), BillingPeriodStart = start };
        Assert.AreEqual(start, PurchaseMeasurementStore.UpgradePeriodStart([initial, upgrade], start.AddDays(10)));
        Assert.ThrowsException<InvalidOperationException>(() => PurchaseMeasurementStore.UpgradePeriodStart([initial], start.AddMonths(1)));
        Assert.ThrowsException<InvalidOperationException>(() => PurchaseMeasurementStore.UpgradePeriodStart([], start));
    }

    [TestMethod]
    public void InvoicePlan_UsesPositiveConfiguredPriceAndCorrectEnvironment()
    {
        const string key = "AI_UPGRADES_TEST_EXTREME_PRICE_ID";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "price_upgrade_fixture");
            using var invoice = JsonDocument.Parse("""{"lines":{"data":[{"amount":-100,"price":{"id":"price_old"}},{"amount":2500,"pricing":{"price_details":{"price":"price_upgrade_fixture"}}}]}}""");
            Assert.AreEqual("extreme", StripeVerifiedPaymentResolver.ConfiguredInvoicePlan(invoice.RootElement, false));
            Assert.IsNull(StripeVerifiedPaymentResolver.ConfiguredInvoicePlan(invoice.RootElement, true));
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }
}
