using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PurchaseReportingTests
{
    private static readonly DateTimeOffset PaidAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static MarketingReportingPeriod Period => new()
    {
        StartInclusive = PaidAt.AddDays(-1), EndExclusive = PaidAt.AddDays(1)
    };

    [TestMethod]
    public void Classify_InitialRenewalAndEvidenceBasedUpgrade_AreSubscriptionRevenue()
    {
        Assert.AreEqual(PurchaseRevenueKind.SubscriptionRevenue, PurchaseReportingSource.Classify(Purchase("subscription_create", first: true))?.RevenueKind);
        Assert.AreEqual(PurchaseRevenueKind.SubscriptionRevenue, PurchaseReportingSource.Classify(Purchase("subscription_cycle", renewal: true))?.RevenueKind);
        Assert.IsNull(PurchaseReportingSource.Classify(Purchase("subscription_update")));
        Assert.AreEqual(PurchaseRevenueKind.SubscriptionRevenue, PurchaseReportingSource.Classify(Purchase("subscription_update"), isSupportedUpgrade: true)?.RevenueKind);
    }

    [TestMethod]
    public async Task GetAsync_ClassifiesUpgradeOnlyWhenPlanChangedWithinKnownBillingPeriod()
    {
        var previous = Purchase("subscription_create", first: true) with
        { StripeSubscriptionId = "sub_1", Plan = "starter" };
        var update = Purchase("subscription_update") with
        { StripeSubscriptionId = "sub_1", Plan = "plus", PaidAt = PaidAt.AddHours(1), BillingPeriodStart = PaidAt.AddDays(-10) };
        var result = await new PurchaseReportingSource(new FakeReader([previous, update], ReportingDataState.Partial)).GetAsync(Period);
        Assert.AreEqual(2, result.Rows.Count);
        Assert.IsTrue(result.Rows.Any(x => x.TransactionId == update.TransactionId));

        var downgrade = update with { Plan = "starter" };
        var rejected = await new PurchaseReportingSource(new FakeReader([previous with { Plan = "plus" }, downgrade], ReportingDataState.Partial)).GetAsync(Period);
        Assert.AreEqual(1, rejected.Rows.Count);

        var unknownPlan = update with { Plan = "unlisted" };
        var unknown = await new PurchaseReportingSource(new FakeReader([previous, unknownPlan], ReportingDataState.Partial)).GetAsync(Period);
        Assert.AreEqual(1, unknown.Rows.Count);

        var unresolvedPeriod = update with { BillingPeriodStart = null };
        var unresolved = await new PurchaseReportingSource(new FakeReader([previous, unresolvedPeriod], ReportingDataState.Partial)).GetAsync(Period);
        Assert.AreEqual(1, unresolved.Rows.Count);
    }

    [TestMethod]
    public void Classify_PaidCreditsAreSeparateAndTestCreditsAreExcluded()
    {
        var fact = PurchaseReportingSource.Classify(Purchase("ai_credit_topup", type: "AICredits"));
        Assert.AreEqual(PurchaseRevenueKind.AICreditRevenue, fact?.RevenueKind);
        Assert.IsNull(PurchaseReportingSource.Classify(Purchase("ai_credit_topup", type: "AICredits", testCredit: true)));
    }

    [TestMethod]
    public void Classify_RejectsUnpaidNonPositiveAndUnsupportedPurchases()
    {
        Assert.IsNull(PurchaseReportingSource.Classify(Purchase("subscription_create", first: true) with { PaymentStatus = "Pending" }));
        Assert.IsNull(PurchaseReportingSource.Classify(Purchase("subscription_create", first: true) with { Amount = 0 }));
        Assert.IsNull(PurchaseReportingSource.Classify(Purchase("subscription_create", first: false)));
        Assert.IsNull(PurchaseReportingSource.Classify(Purchase("unknown")));
    }

    [TestMethod]
    public void Classify_RequiresTransactionSnapshotEvidenceAndRetainsSourceCurrency()
    {
        var earlierAcquisitionOnly = Purchase("ai_credit_topup", type: "AICredits") with { AcquisitionId = "google-acquisition" };
        var unattributed = PurchaseReportingSource.Classify(earlierAcquisitionOnly);
        Assert.IsFalse(unattributed!.HasGoogleTransactionAttribution);
        var attributed = PurchaseReportingSource.Classify(earlierAcquisitionOnly with { Snapshot = new AttributionSnapshot { Gclid = "click" }, Currency = "EUR" });
        Assert.IsTrue(attributed!.HasGoogleTransactionAttribution);
        Assert.AreEqual("EUR", attributed.Currency);
        Assert.AreEqual(12.34m, attributed.Amount);
    }

    [TestMethod]
    public async Task ClickDateAttribution_UsesVerifiedOriginalTouchTimestampNotCheckoutSnapshotTime()
    {
        var originalClickTime = PaidAt.AddDays(-2);
        var purchase = Purchase("subscription_create", first: true) with
        {
            FunnelId = "funnel-1",
            Snapshot = new AttributionSnapshot { TouchId = "touch-1", Gclid = "click-1", CreatedAt = PaidAt }
        };
        var period = new MarketingReportingPeriod
        {
            StartDate = new DateOnly(2026, 9, 28), EndDate = new DateOnly(2026, 9, 30),
            TimeZoneId = "America/New_York"
        };
        var source = new PurchaseReportingSource(new FakeReader([purchase], ReportingDataState.Partial),
            new FakeClickProvenance(originalClickTime));

        var result = await source.GetVerifiedClickAttributionCandidatesAsync();

        Assert.AreEqual(1, result.Rows.Count);
    }

    [TestMethod]
    public async Task GetAsync_ReportsFinancialSourceGapsAndPreservesPartialPurchaseCoverage()
    {
        var reader = new FakeReader([Purchase("subscription_create", first: true)], ReportingDataState.Partial);
        var result = await new PurchaseReportingSource(reader).GetAsync(Period);
        Assert.AreEqual(ReportingDataState.Partial, result.State);
        Assert.AreEqual(1, result.Rows.Count);
        foreach (var source in new[] { MarketingReportingSource.Refunds, MarketingReportingSource.Chargebacks, MarketingReportingSource.Taxes, MarketingReportingSource.StripeFees })
        {
            var coverage = result.SourceCoverage.Single(x => x.Source == source);
            Assert.AreEqual(ReportingDataState.Unavailable, coverage.State);
            Assert.IsTrue(coverage.Explanation!.Contains("unknown, not zero", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task GetAsync_UnavailablePurchaseSourceReturnsNoRows()
    {
        var read = new PurchaseReportingRead([Purchase("subscription_create", first: true)], new ReportingSourceCoverage
        { Source = MarketingReportingSource.Purchases, State = ReportingDataState.Unavailable });
        var result = await new PurchaseReportingSource(new FakeReader(read)).GetAsync(Period);
        Assert.AreEqual(ReportingDataState.Unavailable, result.State);
        Assert.AreEqual(0, result.Rows.Count);
    }

    private static PurchaseRecord Purchase(string reason, bool first = false, bool renewal = false,
        string type = "Subscription", bool testCredit = false) => new()
    {
        TransactionId = Guid.NewGuid().ToString(), PurchaseType = type, BillingReason = reason,
        PaymentStatus = "Paid", IsFirstPayment = first, IsRenewal = renewal,
        IsTestCreditPurchase = testCredit, Amount = 12.34m, AmountMinor = 1234,
        Currency = "USD", PaidAt = PaidAt
    };

    private sealed class FakeReader : IPurchaseReportingReader
    {
        private readonly PurchaseReportingRead _read;
        public FakeReader(IReadOnlyList<PurchaseRecord> records, ReportingDataState state) => _read = new PurchaseReportingRead(records,
            new ReportingSourceCoverage { Source = MarketingReportingSource.Purchases, State = state });
        public FakeReader(PurchaseReportingRead read) => _read = read;

        public Task<PurchaseReportingRead> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_read);
    }

    private sealed class FakeClickProvenance(DateTimeOffset? clickTime) : IPurchaseClickProvenanceReader
    {
        public Task<DateTimeOffset?> GetOriginalClickTimeAsync(PurchaseRecord purchase, CancellationToken cancellationToken = default)
            => Task.FromResult(clickTime);
    }
}
