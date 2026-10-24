using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Server.Services;

/// <summary>Read-only composition of the existing WORK-05 sources and calculation contracts.</summary>
public sealed class MarketingDashboardReportService(
    GoogleAdsReportingSource ads,
    GoogleAdsCampaignReportingSource campaigns,
    PurchaseReportingSource purchases,
    IBusinessExpenseReportingReader expenses)
{
    public async Task<MarketingDashboardData> GetAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken = default)
    {
        var selected = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange, start, end);
        var periods = MarketingReportingPeriodResolver.WithPreviousEquivalentPeriod(selected);
        var comparison = periods.Comparison;
        var selectedAdsTask = ads.GetDailySpendAsync(selected, cancellationToken);
        var comparisonAdsTask = ads.GetDailySpendAsync(comparison, cancellationToken);
        var selectedPurchasesTask = purchases.GetAsync(selected, cancellationToken);
        var campaignPurchasesTask = purchases.GetVerifiedClickAttributionCandidatesAsync(cancellationToken);
        var comparisonPurchasesTask = purchases.GetAsync(comparison, cancellationToken);
        var expensesTask = expenses.ReadAsync(comparison.StartDate, selected.EndDate, cancellationToken);
        await Task.WhenAll(selectedAdsTask, comparisonAdsTask, selectedPurchasesTask, comparisonPurchasesTask, expensesTask);

        var selectedAds = await selectedAdsTask;
        var comparisonAds = await comparisonAdsTask;
        var selectedPurchases = await selectedPurchasesTask;
        var campaignTask = campaigns.GetAsync(selected, (await campaignPurchasesTask).Rows, cancellationToken);
        var comparisonPurchases = await comparisonPurchasesTask;
        var expenseRead = await expensesTask;
        var campaignResult = await campaignTask;
        var selectedMeasures = MarketingFinancialCalculator.Calculate(selected, selectedPurchases.Rows, selectedAds.Rows,
            selectedPurchases.SourceCoverage.First(x => x.Source == MarketingReportingSource.Purchases), selectedAds.Coverage);
        var comparisonMeasures = MarketingFinancialCalculator.Calculate(comparison, comparisonPurchases.Rows, comparisonAds.Rows,
            comparisonPurchases.SourceCoverage.First(x => x.Source == MarketingReportingSource.Purchases), comparisonAds.Coverage);
        var selectedExpenses = expenseRead.Rows.Where(x => DateOnly.TryParseExact(x.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) && date >= selected.StartDate && date <= selected.EndDate).ToArray();
        var comparisonExpenses = expenseRead.Rows.Where(x => DateOnly.TryParseExact(x.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) && date >= comparison.StartDate && date <= comparison.EndDate).ToArray();
        var selectedProfit = MarketingFinancialCalculator.CalculateBusinessProfit(selectedMeasures.TotalCollectedRevenue, selectedExpenses,
            selectedMeasures.AdvertisingSpend, null, null, expenseRead.Coverage.State, ReportingDataState.Unavailable);
        var comparisonProfit = MarketingFinancialCalculator.CalculateBusinessProfit(comparisonMeasures.TotalCollectedRevenue, comparisonExpenses,
            comparisonMeasures.AdvertisingSpend, null, null, expenseRead.Coverage.State, ReportingDataState.Unavailable);
        selectedMeasures = selectedMeasures with { BusinessExpenses = selectedProfit.BusinessExpenses, OverallBusinessProfit = selectedProfit.OverallBusinessProfit };
        comparisonMeasures = comparisonMeasures with { BusinessExpenses = comparisonProfit.BusinessExpenses, OverallBusinessProfit = comparisonProfit.OverallBusinessProfit };
        var coverage = selectedPurchases.SourceCoverage.Concat([selectedAds.Coverage, expenseRead.Coverage]).ToArray();
        var state = coverage.Any(x => x.State == ReportingDataState.Error) ? ReportingDataState.Error
            : coverage.Any(x => x.State == ReportingDataState.Complete) ? ReportingDataState.Partial
            : coverage.Any(x => x.State == ReportingDataState.Partial || x.State == ReportingDataState.Stale) ? ReportingDataState.Partial
            : ReportingDataState.Unavailable;

        var spendByDay = selectedAds.Rows.Where(x => x.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Date).ToDictionary(x => x.Key, x => (decimal?)x.Sum(row => row.Amount));
        var revenueFacts = selectedPurchases.Rows.Where(x => x.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) && x.HasGoogleTransactionAttribution).ToArray();
        var revenueByDay = revenueFacts.GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.PaidAt, TimeZoneInfo.FindSystemTimeZoneById(selected.TimeZoneId)).DateTime))
            .ToDictionary(x => x.Key, x => (decimal?)x.Sum(row => row.Amount));
        var customersByDay = revenueFacts.Where(x => x.RevenueKind == PurchaseRevenueKind.SubscriptionRevenue && x.IsFirstPayment && x.HasGoogleTransactionAttribution)
            .GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.PaidAt, TimeZoneInfo.FindSystemTimeZoneById(selected.TimeZoneId)).DateTime))
            .ToDictionary(x => x.Key, x => (long?)x.Select(row => row.CustomerKey).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).LongCount());
        var observedDays = spendByDay.Keys.Concat(revenueByDay.Keys).Concat(customersByDay.Keys).Distinct().Order().ToArray();
        var daily = observedDays.Select(date =>
        {
            var day = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange, date, date, selected.TimeZoneId);
            var dayMeasures = MarketingFinancialCalculator.Calculate(day, revenueFacts, selectedAds.Rows,
                selectedPurchases.SourceCoverage.First(x => x.Source == MarketingReportingSource.Purchases), selectedAds.Coverage);
            return new MarketingDashboardPoint(date, spendByDay.GetValueOrDefault(date), revenueByDay.GetValueOrDefault(date),
                customersByDay.GetValueOrDefault(date), dayMeasures.ReturnOnAdSpend.Amount);
        }).ToArray();

        return new MarketingDashboardData
        {
            Report = new MarketingReportingResult
            {
                ReportingCurrency = "USD", Periods = periods, SelectedMeasures = selectedMeasures, ComparisonMeasures = comparisonMeasures,
                SelectedSourceCoverage = coverage, ComparisonSourceCoverage = comparisonPurchases.SourceCoverage.Concat([comparisonAds.Coverage, expenseRead.Coverage]).ToArray(),
                Attribution = new AttributionLimitations { State = ReportingDataState.Partial,
                    Explanation = "Revenue attribution uses transaction-level checkout evidence only; it is not campaign-matched.",
                    Limitations = ["Account spend is not split by campaign.", "Purchase history, refunds, fees, taxes, and attribution coverage are not fully validated."] },
                State = state,
                PartialAvailabilityReasons = ["Campaign-level Ads facts and GA4 property reporting are unavailable.", "Contribution and payback require validated cost bases."]
            },
            Marginal = MarketingFinancialCalculator.CalculateMarginal(selectedMeasures, comparisonMeasures),
            Daily = daily,
            ExpenseCoverage = expenseRead.Coverage,
            Campaigns = campaignResult.Campaigns,
            CampaignCoverage = campaignResult.CampaignCoverage,
            CampaignAttributionCoverage = campaignResult.AttributionCoverage,
            RecommendationPolicyCoverage = new ReportingSourceCoverage { Source = MarketingReportingSource.GoogleAds,
                State = campaignResult.Campaigns.Any(x => x.RecommendationPolicyState == "configured") ? ReportingDataState.Partial : ReportingDataState.Unavailable,
                Explanation = campaignResult.Campaigns.Any(x => x.RecommendationPolicyState == "configured")
                    ? "Advisory thresholds are configured; confirm evidence sufficiency for each campaign."
                    : "Recommendation thresholds are incomplete or invalid; campaign changes are not suggested." },
            Ga4Coverage = new ReportingSourceCoverage { Source = MarketingReportingSource.GoogleAnalytics4, State = ReportingDataState.Unavailable,
                Explanation = "The existing GA4 integration delivers server-side events. It does not query GA4 property reports or funnel data." },
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }
}
