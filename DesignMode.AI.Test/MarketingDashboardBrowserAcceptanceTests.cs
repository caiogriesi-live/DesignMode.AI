using System.Text.Json;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.MarketingReporting;
using Microsoft.Playwright;

namespace DesignMode.AI.Test;

[TestClass, DoNotParallelize]
public sealed class MarketingDashboardBrowserAcceptanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string MockToken = "eyJhbGciOiJub25lIn0.eyJuYW1lIjoib3BlcmF0b3JAZXhhbXBsZS50ZXN0Iiwic3ViIjoidGVzdCIsImV4cCI6MjAwMDAwMDAwMH0.";

    [TestMethod, TestCategory("BrowserAcceptance")]
    public async Task Dashboard_ChromiumDesktop_RendersMockedReportAndDateFilter()
    {
        var baseUrl = Environment.GetEnvironmentVariable("WORK06_CLIENT_URL");
        if (string.IsNullOrWhiteSpace(baseUrl)) Assert.Inconclusive("Set WORK06_CLIENT_URL to the running local Blazor client URL.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        await context.AddInitScriptAsync($"localStorage.setItem('authToken',JSON.stringify('{MockToken}'));localStorage.setItem('authTokenExpiration',JSON.stringify(new Date(Date.now()+3600000).toISOString()));");
        var requests = new List<string>();
        await context.RouteAsync("**/api/**", async route =>
        {
            if (route.Request.Url.Contains("/api/marketing-dashboard", StringComparison.Ordinal))
            {
                requests.Add(route.Request.Url);
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = JsonSerializer.Serialize(Fixture(), JsonOptions) });
            }
            else await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "[]" });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(new Uri(baseUrl), "/init/marketing-dashboard").ToString());
        await page.GetByRole(AriaRole.Heading, new() { Name = "Marketing Dashboard", Level = 1 }).WaitForAsync();
        await Assertions.Expect(page.GetByText("USD 1,250.00", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Mocked browser fixture; not live Google Ads.")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Test Campaign", new() { Exact = true })).ToBeVisibleAsync();
        var campaignTableText = await page.Locator(".campaign-table").InnerTextAsync();
        StringAssert.Contains(campaignTableText, "10%", campaignTableText);
        await Assertions.Expect(page.GetByText("Scale", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("GA4 event delivery is configured separately; property reporting and funnel data are not implemented.")).ToBeVisibleAsync();
        var screenshotDirectory = Path.Combine(Path.GetTempPath(), "marketing-dashboard-work-07");
        Directory.CreateDirectory(screenshotDirectory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "chromium-1440x900.png"), FullPage = true });
        await page.Locator(".campaign-panel").ScrollIntoViewIfNeededAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "chromium-campaign-table.png"), FullPage = false });
        await page.GetByLabel("Reporting date preset").SelectOptionAsync("last7");
        await page.GetByRole(AriaRole.Button, new() { Name = "↻ Refresh" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Marketing Dashboard", Level = 1 })).ToBeVisibleAsync();
        Assert.IsTrue(requests.Count >= 2, "Preset and refresh must cause a second report request.");
        Assert.IsTrue(requests[^1].Contains("start=", StringComparison.Ordinal) && requests[^1].Contains("end=", StringComparison.Ordinal));
        await context.CloseAsync();
    }

    [TestMethod, TestCategory("BrowserAcceptance")]
    public async Task Dashboard_WebKitMobile_RendersWithoutHorizontalOverflow()
    {
        var baseUrl = Environment.GetEnvironmentVariable("WORK06_CLIENT_URL");
        if (string.IsNullOrWhiteSpace(baseUrl)) Assert.Inconclusive("Set WORK06_CLIENT_URL to the running local Blazor client URL.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Webkit.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 }, IsMobile = true, HasTouch = true });
        await context.AddInitScriptAsync($"localStorage.setItem('authToken',JSON.stringify('{MockToken}'));localStorage.setItem('authTokenExpiration',JSON.stringify(new Date(Date.now()+3600000).toISOString()));");
        await context.RouteAsync("**/api/**", async route =>
        {
            if (route.Request.Url.Contains("/api/marketing-dashboard", StringComparison.Ordinal))
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = JsonSerializer.Serialize(Fixture(), JsonOptions) });
            else await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = "[]" });
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(new Uri(baseUrl), "/init/marketing-dashboard").ToString());
        await page.GetByRole(AriaRole.Heading, new() { Name = "Marketing Dashboard", Level = 1 }).WaitForAsync();
        await Assertions.Expect(page.GetByText("USD 1,250.00", new() { Exact = true })).ToBeVisibleAsync();
        var dimensions = await page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, document.documentElement.clientWidth]");
        Assert.IsTrue(dimensions[0] <= dimensions[1], $"Unexpected horizontal overflow: scrollWidth={dimensions[0]}, clientWidth={dimensions[1]}.");
        await Assertions.Expect(page.GetByText("Test Campaign", new() { Exact = true })).ToBeVisibleAsync();
        var screenshotDirectory = Path.Combine(Path.GetTempPath(), "marketing-dashboard-work-07");
        Directory.CreateDirectory(screenshotDirectory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "webkit-390x844.png"), FullPage = true });
        await page.Locator(".campaign-panel").ScrollIntoViewIfNeededAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDirectory, "webkit-campaign-table.png"), FullPage = false });
        await context.CloseAsync();
    }

    private static MarketingDashboardData Fixture()
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        var period = new MarketingReportingPeriod { Selection = MarketingReportingPeriodSelection.CustomRange, StartDate = date.AddDays(-29), EndDate = date,
            StartInclusive = new DateTimeOffset(date.AddDays(-29).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), EndExclusive = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) };
        var comparison = MarketingReportingPeriodResolver.WithPreviousEquivalentPeriod(period).Comparison;
        var measures = new MarketingFinancialMeasures
        {
            AdvertisingSpend = Measure(1250, ReportingDataState.Partial), GoogleAttributedRevenue = Measure(3750, ReportingDataState.Partial),
            ReturnOnAdSpend = Measure(3, ReportingDataState.Partial), CustomerAcquisitionCost = Measure(125, ReportingDataState.Partial),
            NewCustomers = new() { Count = 10, State = ReportingDataState.Partial }, EligibleCustomers = new() { Count = 30, State = ReportingDataState.Partial },
            EligibleRevenue = Measure(5000, ReportingDataState.Partial), AverageRevenuePerCustomer = Measure(166.67m, ReportingDataState.Partial),
            SubscriptionRevenue = Measure(5000, ReportingDataState.Partial), TotalCollectedRevenue = Measure(5000, ReportingDataState.Partial),
            ContributionBeforeAds = Unavailable(), ContributionAfterAds = Unavailable(), ContributionPerCustomer = Unavailable(), CustomerAcquisitionPaybackMonths = Unavailable(),
            BusinessExpenses = Measure(250, ReportingDataState.Partial), OverallBusinessProfit = Unavailable(), MarketingProfit = Unavailable(), MarketingProfitMarginPercent = Unavailable()
        };
        return new MarketingDashboardData
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Report = new MarketingReportingResult { ReportingCurrency = "USD", Periods = new() { Selected = period, Comparison = comparison },
                SelectedMeasures = measures, ComparisonMeasures = measures, State = ReportingDataState.Partial,
                SelectedSourceCoverage = [new() { Source = MarketingReportingSource.Purchases, State = ReportingDataState.Partial }],
                Attribution = new() { State = ReportingDataState.Partial } },
            Daily = [new(date.AddDays(-1), 42, 125, 1, 2.98m), new(date, 55, 145, 2, 2.64m)],
            Marginal = MarketingFinancialCalculatorForBrowser(),
            Campaigns = [new CampaignFinancialReport
            {
                CampaignId = "123456", CampaignName = "Test Campaign",
                AdvertisingSpend = Measure(200, ReportingDataState.Partial), AttributedRevenue = Measure(600, ReportingDataState.Partial),
                Roas = Measure(3, ReportingDataState.Partial), CustomerAcquisitionCost = Measure(50, ReportingDataState.Partial),
                NewCustomers = new() { Count = 4, State = ReportingDataState.Partial },
                VerifiedPurchases = new() { Count = 4, State = ReportingDataState.Partial },
                PlatformReportedConversions = Measure(7, ReportingDataState.Partial),
                SuggestedBudgetChangePercent = 10, CurrentAverageDailyBudget = Measure(200, ReportingDataState.Partial),
                SuggestedAverageDailyBudgetChange = Measure(20, ReportingDataState.Partial),
                Recommendation = CampaignRecommendationAction.Scale,
                RecommendationRationale = "Test-only advisory fixture."
            }],
            CampaignCoverage = new() { Source = MarketingReportingSource.GoogleAds, State = ReportingDataState.Partial, Explanation = "Mocked browser fixture; not live Google Ads." },
            Ga4Coverage = new() { Source = MarketingReportingSource.GoogleAnalytics4, State = ReportingDataState.Unavailable, Explanation = "GA4 event delivery is configured separately; property reporting and funnel data are not implemented." }
        };
    }

    private static ReportingMeasure Measure(decimal amount, ReportingDataState state) => new() { Amount = amount, Currency = "USD", State = state };
    private static ReportingMeasure Unavailable() => new() { State = ReportingDataState.Unavailable, Explanation = "Not available in safe browser fixture." };
    private static MarginalPerformanceReport MarketingFinancialCalculatorForBrowser() => new() { State = ReportingDataState.Partial,
        AdvertisingSpendChange = Measure(120, ReportingDataState.Partial), AttributedRevenueChange = Measure(300, ReportingDataState.Partial),
        NewCustomersChange = new() { Count = 2, State = ReportingDataState.Partial }, MarginalRoas = Measure(2.5m, ReportingDataState.Partial),
        MarginalCac = Measure(60, ReportingDataState.Partial), IncrementalContribution = Unavailable(),
        Explanation = "Observed period-over-period changes are not causal." };
}
