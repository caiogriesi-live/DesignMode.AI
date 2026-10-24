namespace DesignMode.AI.Shared.Models.MarketingReporting;

/// <summary>Privacy-safe aggregate response for the operator-only Marketing Dashboard.</summary>
public sealed record MarketingDashboardData
{
    public MarketingReportingResult Report { get; init; } = new();
    public MarginalPerformanceReport Marginal { get; init; } = new();
    public IReadOnlyList<MarketingDashboardPoint> Daily { get; init; } = Array.Empty<MarketingDashboardPoint>();
    public IReadOnlyList<CampaignFinancialReport> Campaigns { get; init; } = Array.Empty<CampaignFinancialReport>();
    public ReportingSourceCoverage ExpenseCoverage { get; init; } = new() { Source = MarketingReportingSource.BusinessExpenses, State = ReportingDataState.Unavailable };
    public ReportingSourceCoverage CampaignCoverage { get; init; } = new()
    {
        Source = MarketingReportingSource.GoogleAds,
        State = ReportingDataState.Unavailable,
        Explanation = "Campaign-level Google Ads reporting is unavailable or not configured."
    };
    public ReportingSourceCoverage CampaignAttributionCoverage { get; init; } = new()
    {
        Source = MarketingReportingSource.Purchases,
        State = ReportingDataState.Unavailable,
        Explanation = "Canonical purchases require an exact, verified Google click to campaign match."
    };
    public ReportingSourceCoverage RecommendationPolicyCoverage { get; init; } = new()
    {
        Source = MarketingReportingSource.GoogleAds,
        State = ReportingDataState.Unavailable,
        Explanation = "No campaign budget recommendation policy is configured."
    };
    public ReportingSourceCoverage Ga4Coverage { get; init; } = new()
    {
        Source = MarketingReportingSource.GoogleAnalytics4,
        State = ReportingDataState.Unavailable,
        Explanation = "GA4 event delivery is configured separately; property reporting and funnel data are not implemented."
    };
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record MarketingDashboardPoint(DateOnly Date, decimal? SpendUsd, decimal? RevenueUsd, long? NewCustomers, decimal? Roas);
