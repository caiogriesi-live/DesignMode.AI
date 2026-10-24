namespace DesignMode.AI.Shared.Models.MarketingReporting;

/// <summary>The user-facing date selection. Date resolution uses America/New_York calendar days.</summary>
public enum MarketingReportingPeriodSelection
{
    Today, Last7Days, Last30Days, MonthToDate, PreviousMonth, YearToDate, CustomRange
}

/// <summary>
/// Inclusive local calendar dates with corresponding instant bounds. StartInclusive is midnight at
/// StartDate in America/New_York; EndExclusive is midnight after EndDate in that zone. Resolve each
/// local midnight independently so DST days may be 23 or 25 hours while preserving calendar days.
/// </summary>
public sealed record MarketingReportingPeriod
{
    public MarketingReportingPeriodSelection Selection { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public DateTimeOffset StartInclusive { get; init; }
    public DateTimeOffset EndExclusive { get; init; }
    public string TimeZoneId { get; init; } = "America/New_York";
}

/// <summary>
/// The immediately preceding equivalent interval has the same count of New York calendar days and
/// ends the day before Selected. Presets resolve against the report's captured current date; this
/// contract does not prescribe preset date mappings.
/// </summary>
public sealed record MarketingReportingComparison
{
    public MarketingReportingPeriod Selected { get; init; } = new();
    public MarketingReportingPeriod Comparison { get; init; } = new();
}

public enum ReportingDataState { Complete, Partial, Unavailable, Error, Stale }
public enum MarketingReportingSource { Purchases, Refunds, Chargebacks, StripeFees, Taxes, GoogleAds, BusinessExpenses, GoogleAnalytics4 }

/// <summary>Null counts mean the source cannot establish its coverage.</summary>
public sealed record ReportingSourceCoverage
{
    public MarketingReportingSource Source { get; init; }
    public ReportingDataState State { get; init; } = ReportingDataState.Unavailable;
    public DateTimeOffset? FreshThrough { get; init; }
    public long? RecordsObserved { get; init; }
    public long? RecordsExpected { get; init; }
    public string? Explanation { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>A nullable amount prevents an absent/unsupported value from being mistaken for zero.</summary>
public sealed record ReportingMeasure
{
    public decimal? Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public ReportingDataState State { get; init; } = ReportingDataState.Unavailable;
    public string? Explanation { get; init; }
}

/// <summary>Nullable count for inputs whose source coverage is unknown.</summary>
public sealed record ReportingCount
{
    public long? Count { get; init; }
    public ReportingDataState State { get; init; } = ReportingDataState.Unavailable;
    public string? Explanation { get; init; }
}

/// <summary>Original-currency amount, retained separately without conversion.</summary>
public sealed record CurrencyAmount
{
    public string Currency { get; init; } = "USD";
    public decimal Amount { get; init; }
    public long? RecordCount { get; init; }
}

/// <summary>Unsupported inputs remain null/unavailable or partial.</summary>
public sealed record MarketingFinancialMeasures
{
    public ReportingMeasure AdvertisingSpend { get; init; } = new();
    public ReportingMeasure EligibleRevenue { get; init; } = new();
    public ReportingCount EligibleCustomers { get; init; } = new();
    public ReportingCount NewCustomers { get; init; } = new();
    public ReportingMeasure AverageRevenuePerCustomer { get; init; } = new();
    public ReportingMeasure ContributionBeforeAds { get; init; } = new();
    public ReportingMeasure ContributionAfterAds { get; init; } = new();
    public ReportingMeasure ContributionPerCustomer { get; init; } = new();
    public ReportingMeasure CustomerAcquisitionPaybackMonths { get; init; } = new();
    public ReportingMeasure SubscriptionRevenue { get; init; } = new();
    public ReportingMeasure AICreditRevenue { get; init; } = new();
    public ReportingMeasure TotalCollectedRevenue { get; init; } = new();
    public ReportingMeasure GoogleAttributedRevenue { get; init; } = new();
    public ReportingMeasure RefundsAndChargebacks { get; init; } = new();
    public ReportingMeasure StripeFees { get; init; } = new();
    public ReportingMeasure GoogleAdsSpend { get; init; } = new();
    public ReportingMeasure BusinessExpenses { get; init; } = new();
    public ReportingMeasure OverallBusinessProfit { get; init; } = new();
    public ReportingMeasure MarketingProfit { get; init; } = new();
    public ReportingMeasure MarketingProfitMarginPercent { get; init; } = new();
    public ReportingMeasure CustomerAcquisitionCost { get; init; } = new();
    public ReportingMeasure ReturnOnAdSpend { get; init; } = new();
    public IReadOnlyList<CurrencyAmount> NonUsdAmounts { get; init; } = Array.Empty<CurrencyAmount>();
}

/// <summary>Comparable campaign facts supplied by a read-only source.</summary>
public sealed record CampaignFinancialInput
{
    public string CampaignId { get; init; } = "";
    public string CampaignName { get; init; } = "";
    public string Currency { get; init; } = "USD";
    public decimal? AdvertisingSpend { get; init; }
    public decimal? AttributedRevenue { get; init; }
    public long? NewCustomers { get; init; }
    public decimal? VariableCostsExcludingAds { get; init; }
    public ReportingDataState State { get; init; } = ReportingDataState.Unavailable;
}

public enum CampaignRecommendationAction { Scale, Hold, Reduce, Pause, InsufficientData, ConfigurationRequired, Unavailable }

/// <summary>Explicit caller-supplied thresholds; no universal defaults are assumed.</summary>
public sealed record CampaignRecommendationPolicy
{
    public decimal? TargetRoas { get; init; }
    public decimal? MinimumRoas { get; init; }
    public decimal? TargetCac { get; init; }
    public long MinimumNewCustomers { get; init; }
}

public sealed record CampaignFinancialReport
{
    public string CampaignId { get; init; } = "";
    public string CampaignName { get; init; } = "";
    public ReportingMeasure AdvertisingSpend { get; init; } = new();
    public ReportingMeasure AttributedRevenue { get; init; } = new();
    public ReportingMeasure Roas { get; init; } = new();
    public ReportingMeasure CustomerAcquisitionCost { get; init; } = new();
    public ReportingMeasure ContributionAfterAds { get; init; } = new();
    public ReportingCount NewCustomers { get; init; } = new();
    public ReportingCount VerifiedPurchases { get; init; } = new();
    public ReportingMeasure PlatformReportedConversions { get; init; } = new();
    public ReportingMeasure PlatformReportedConversionValue { get; init; } = new();
    public decimal? SuggestedBudgetChangePercent { get; init; }
    public ReportingMeasure CurrentAverageDailyBudget { get; init; } = new();
    public ReportingMeasure SuggestedAverageDailyBudgetChange { get; init; } = new();
    public string RecommendationConfidence { get; init; } = "Unavailable";
    public string? RecommendationPolicyState { get; init; }
    public string ReportingTimeZone { get; init; } = "";
    public string AttributionBasis { get; init; } = "";
    public CampaignRecommendationAction Recommendation { get; init; } = CampaignRecommendationAction.Unavailable;
    public string RecommendationRationale { get; init; } = "";
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}

public sealed record MarginalPerformanceReport
{
    public ReportingMeasure AdvertisingSpendChange { get; init; } = new();
    public ReportingMeasure AttributedRevenueChange { get; init; } = new();
    public ReportingCount NewCustomersChange { get; init; } = new();
    public ReportingMeasure MarginalRoas { get; init; } = new();
    public ReportingMeasure MarginalCac { get; init; } = new();
    public ReportingMeasure IncrementalContribution { get; init; } = new();
    public ReportingDataState State { get; init; } = ReportingDataState.Unavailable;
    public string Explanation { get; init; } = "";
}

/// <summary>Qualifies attribution measures and claims with explicit evidence limitations.</summary>
public sealed record AttributionLimitations
{
    public ReportingDataState State { get; init; } = ReportingDataState.Unavailable;
    public string Explanation { get; init; } = "Google attribution requires approved transaction-level evidence.";
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}

/// <summary>Selected and comparison report with explicit source, attribution, error, and partial states.</summary>
public sealed record MarketingReportingResult
{
    public string ReportingCurrency { get; init; } = "USD";
    public MarketingReportingComparison Periods { get; init; } = new();
    public MarketingFinancialMeasures SelectedMeasures { get; init; } = new();
    public MarketingFinancialMeasures ComparisonMeasures { get; init; } = new();
    public IReadOnlyList<ReportingSourceCoverage> SelectedSourceCoverage { get; init; } = Array.Empty<ReportingSourceCoverage>();
    public IReadOnlyList<ReportingSourceCoverage> ComparisonSourceCoverage { get; init; } = Array.Empty<ReportingSourceCoverage>();
    public AttributionLimitations Attribution { get; init; } = new();
    public ReportingDataState State { get; init; } = ReportingDataState.Unavailable;
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PartialAvailabilityReasons { get; init; } = Array.Empty<string>();
}
