using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class MarketingFinancialCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly MarketingReportingPeriod Period = new() { StartDate = new(2026, 10, 1), EndDate = new(2026, 10, 3), StartInclusive = Start, EndExclusive = Start.AddDays(3) };
    private static ReportingSourceCoverage Coverage(MarketingReportingSource source) => new() { Source = source, State = ReportingDataState.Complete };

    [TestMethod]
    public void Calculate_UsesDistinctAttributedNewCustomersAndExplicitDecimalRounding()
    {
        var purchases = new[]
        {
            Fact("one", "c1", 20.005m, first: true, attributed: true),
            Fact("two", "c1", 10m, attributed: true),
            Fact("three", "c2", 10m, first: true, attributed: true),
            Fact("four", "c3", 999m, first: true, attributed: false) with { Currency = "EUR" }
        };
        var result = MarketingFinancialCalculator.Calculate(Period, purchases,
            [new(new(2026, 10, 1), 10m, "USD", "acct")], Coverage(MarketingReportingSource.Purchases), Coverage(MarketingReportingSource.GoogleAds));
        Assert.AreEqual(40.01m, result.EligibleRevenue.Amount);
        Assert.AreEqual(2L, result.EligibleCustomers.Count);
        Assert.AreEqual(2L, result.NewCustomers.Count);
        Assert.AreEqual(5m, result.CustomerAcquisitionCost.Amount);
        Assert.AreEqual(4.0005m, result.ReturnOnAdSpend.Amount);
        Assert.AreEqual(ReportingDataState.Partial, result.EligibleRevenue.State);
        Assert.AreEqual(999m, result.NonUsdAmounts.Single().Amount);
    }

    [TestMethod]
    public void Calculate_LeavesUnsupportedProfitAndPaybackUnavailableAndAvoidsZeroDivision()
    {
        var result = MarketingFinancialCalculator.Calculate(Period, [Fact("one", "c1", 12m, true, true)], [],
            Coverage(MarketingReportingSource.Purchases), Coverage(MarketingReportingSource.GoogleAds));
        Assert.IsNull(result.ReturnOnAdSpend.Amount);
        Assert.IsNull(result.CustomerAcquisitionCost.Amount);
        Assert.IsNull(result.ContributionAfterAds.Amount);
        Assert.IsNull(result.CustomerAcquisitionPaybackMonths.Amount);
        Assert.AreEqual(ReportingDataState.Unavailable, result.ContributionAfterAds.State);
    }

    [TestMethod]
    public void CalculateCampaign_UsesOnlyConfiguredThresholdsAndNeverInventsRecommendation()
    {
        var input = new CampaignFinancialInput { CampaignId = "campaign", CampaignName = "Test", AdvertisingSpend = 10m,
            AttributedRevenue = 30m, NewCustomers = 2, State = ReportingDataState.Complete };
        var unavailable = MarketingFinancialCalculator.CalculateCampaign(input, null);
        Assert.AreEqual(CampaignRecommendationAction.Unavailable, unavailable.Recommendation);
        var recommended = MarketingFinancialCalculator.CalculateCampaign(input, new CampaignRecommendationPolicy { TargetRoas = 2m, MinimumRoas = 1m, MinimumNewCustomers = 1 });
        Assert.AreEqual(CampaignRecommendationAction.Scale, recommended.Recommendation);
        Assert.IsNull(recommended.ContributionAfterAds.Amount);
        Assert.IsTrue(recommended.Limitations.Any(x => x.Contains("never changes Ads", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CalculateMarginal_RejectsZeroOrNegativeDenominatorsAndStatesObservationalLimit()
    {
        var current = new MarketingFinancialMeasures { AdvertisingSpend = new() { Amount = 10m, State = ReportingDataState.Complete },
            GoogleAttributedRevenue = new() { Amount = 20m, State = ReportingDataState.Complete }, NewCustomers = new() { Count = 1, State = ReportingDataState.Complete } };
        var previous = current with { AdvertisingSpend = current.AdvertisingSpend with { Amount = 10m }, NewCustomers = current.NewCustomers with { Count = 2 } };
        var marginal = MarketingFinancialCalculator.CalculateMarginal(current, previous);
        Assert.IsNull(marginal.MarginalRoas.Amount);
        Assert.IsNull(marginal.MarginalCac.Amount);
        Assert.IsTrue(marginal.Explanation.Contains("do not establish", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CalculateBusinessProfit_DoesNotDoubleSubtractAdvertisingAndRequiresDeductions()
    {
        var revenue = new ReportingMeasure { Amount = 100m, State = ReportingDataState.Complete };
        var spend = new ReportingMeasure { Amount = 20m, State = ReportingDataState.Complete };
        var expenses = new[]
        {
            new DesignMode.AI.Shared.Models.BusinessExpense { Amount = 10m, Currency = "USD", Category = "software" },
            new DesignMode.AI.Shared.Models.BusinessExpense { Amount = 20m, Currency = "USD", Category = "advertising/marketing" }
        };
        var result = MarketingFinancialCalculator.CalculateBusinessProfit(revenue, expenses, spend, 0m, 0m,
            ReportingDataState.Complete, ReportingDataState.Complete);
        Assert.AreEqual(10m, result.BusinessExpenses.Amount);
        Assert.AreEqual(ReportingDataState.Partial, result.BusinessExpenses.State);
        Assert.IsNull(result.OverallBusinessProfit.Amount, "Unreconciled ad category cannot be counted alongside Ads spend.");

        var withoutAdCategory = MarketingFinancialCalculator.CalculateBusinessProfit(revenue, [expenses[0]], spend, null, 0m,
            ReportingDataState.Complete, ReportingDataState.Unavailable);
        Assert.IsNull(withoutAdCategory.OverallBusinessProfit.Amount, "Unknown adjustment coverage must remain unavailable.");
    }

    [TestMethod]
    public void CalculateMarketingProfit_RequiresAssociatedDeductions()
    {
        var revenue = new ReportingMeasure { Amount = 100m, State = ReportingDataState.Complete };
        var spend = new ReportingMeasure { Amount = 20m, State = ReportingDataState.Complete };
        var missing = MarketingFinancialCalculator.CalculateMarketingProfit(revenue, spend, null, 3m, ReportingDataState.Unavailable);
        Assert.IsNull(missing.MarketingProfit.Amount);
        var complete = MarketingFinancialCalculator.CalculateMarketingProfit(revenue, spend, 2m, 3m, ReportingDataState.Complete);
        Assert.AreEqual(75m, complete.MarketingProfit.Amount);
        Assert.AreEqual(75m, complete.MarketingProfitMarginPercent.Amount);
    }

    [TestMethod]
    public void PeriodResolver_ResolvesDstCalendarDaysAsIndependentUtcBoundaries()
    {
        var spring = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange,
            new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8));
        var fall = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1));
        Assert.AreEqual(TimeSpan.FromHours(23), spring.EndExclusive - spring.StartInclusive);
        Assert.AreEqual(TimeSpan.FromHours(25), fall.EndExclusive - fall.StartInclusive);
        var comparison = MarketingReportingPeriodResolver.WithPreviousEquivalentPeriod(spring);
        Assert.AreEqual(1, comparison.Comparison.EndDate.DayNumber - comparison.Comparison.StartDate.DayNumber + 1);
        Assert.AreEqual(spring.StartDate.AddDays(-1), comparison.Comparison.EndDate);
    }

    private static PurchaseRevenueFact Fact(string transaction, string customer, decimal amount, bool first = false, bool attributed = false) => new()
    {
        TransactionId = transaction, CustomerKey = customer, PaidAt = Start.AddHours(1), RevenueKind = PurchaseRevenueKind.SubscriptionRevenue,
        Amount = amount, Currency = "USD", IsFirstPayment = first, HasGoogleTransactionAttribution = attributed
    };
}
