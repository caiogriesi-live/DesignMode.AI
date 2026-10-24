using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using DesignMode.AI.Shared.Models.MarketingReporting;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GoogleAdsCampaignReportingTests
{
    [TestMethod]
    public void RecommendationOptions_RequireEveryThresholdAndBoundedChangePercentages()
    {
        Assert.IsFalse(new CampaignBudgetRecommendationOptions().IsConfigured);
        var valid = new CampaignBudgetRecommendationOptions
        {
            TargetRoas = 3, MinimumRoas = 1, MinimumNewCustomers = 10, MinimumClicks = 100,
            MinimumSpend = 200, MinimumAttributionCoveragePercent = 80, ScaleChangePercent = 10,
            ReduceChangePercent = 10, PauseChangePercent = 20, MaximumScaleChangePercent = 15,
            MaximumReductionPercent = 25, MinimumReportingDays = 14, ConversionLagDays = 7
        };
        Assert.IsTrue(valid.IsConfigured);
        Assert.IsFalse((valid with { ScaleChangePercent = 16 }).IsConfigured);
        Assert.IsFalse((valid with { MinimumRoas = 3 }).IsConfigured);
        Assert.IsFalse((valid with { ConversionLagDays = null }).IsConfigured);
    }

    [TestMethod]
    public async Task CampaignTransport_QueriesCustomerLocalCampaignBudgetAndExactClickRowsReadOnly()
    {
        var handler = new CampaignHttpHandler();
        var options = new GoogleAdsReportingOptions
        {
            CustomerId = "1234567890", DeveloperToken = "legacy-token", OAuthClientId = "client",
            OAuthClientSecret = "REDACTED_FOR_GROOMING", OAuthRefreshToken = "refresh", ApiVersion = "v22"
        };
        var transport = new GoogleAdsReportingHttpTransport(new HttpClient(handler), options);
        var period = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2));

        var report = await transport.QueryCampaignReportingAsync(period);
        var clickRows = await transport.QueryGclidCampaignMappingsAsync(new DateOnly(2026, 10, 1), ["test-click-id"]);

        Assert.AreEqual("1234567890", report.CustomerId);
        Assert.AreEqual("USD", report.Currency);
        Assert.AreEqual("America/New_York", report.TimeZone);
        Assert.AreEqual(1, report.DailyFacts.Count);
        Assert.AreEqual(20m, report.DailyFacts[0].Spend);
        Assert.AreEqual(3m, report.DailyFacts[0].PlatformConversions);
        Assert.AreEqual(100m, report.Budgets.Single().Amount);
        Assert.IsFalse(report.Budgets.Single().IsShared);
        Assert.AreEqual("campaign-1", clickRows.Single().CampaignId);
        Assert.AreEqual(1, handler.Requests.Count(x => x.Contains("oauth2.googleapis.com", StringComparison.Ordinal)));
        StringAssert.Contains(handler.QueryBodies.Single(x => x.Contains("click_view", StringComparison.Ordinal)), "segments.date = '2026-10-01'");
        StringAssert.Contains(handler.QueryBodies.Single(x => x.Contains("click_view", StringComparison.Ordinal)), "click_view.gclid IN ('test-click-id')");
    }

    [TestMethod]
    public async Task CampaignSource_UnconfiguredPolicyDoesNotInventSuggestedBudgetChanges()
    {
        var source = new GoogleAdsCampaignReportingSource(new UnusedTransport(),
            new GoogleAdsReportingOptions(), new CampaignBudgetRecommendationOptions());
        var result = await source.GetAsync(new MarketingReportingPeriod(), []);
        Assert.AreEqual(ReportingDataState.Unavailable, result.CampaignCoverage.State);
        Assert.AreEqual(0, result.Campaigns.Count);
        Assert.IsNull(result.Campaigns.FirstOrDefault()?.SuggestedBudgetChangePercent);
    }

    [TestMethod]
    public async Task CampaignSource_ExactGclidMatchCountsCanonicalPurchaseSeparatelyFromPlatformConversions()
    {
        var clickDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddDays(-3).UtcDateTime);
        var clickTime = new DateTimeOffset(clickDate.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
        var period = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange, clickDate, clickDate);
        var purchase = new PurchaseRecord
        {
            TransactionId = "txn-test-1", UserId = "customer-internal", FunnelId = "funnel-test",
            PurchaseType = "Subscription", BillingReason = "subscription_create", PaymentStatus = "Paid",
            IsFirstPayment = true, Amount = 50m, AmountMinor = 5000, Currency = "USD", PaidAt = clickTime.AddHours(1),
            Snapshot = new AttributionSnapshot { TouchId = "touch-test-1", Gclid = "test-click-id" }
        };
        var purchaseSource = new PurchaseReportingSource(new PurchaseReader(purchase), new ClickTimeReader(clickTime));
        var purchaseFacts = await purchaseSource.GetVerifiedClickAttributionCandidatesAsync();
        var transport = new CampaignTransport(period, clickDate);
        var options = new GoogleAdsReportingOptions
        {
            CustomerId = "1234567890", OAuthClientId = "client", OAuthClientSecret = "REDACTED_FOR_GROOMING",
            OAuthRefreshToken = "refresh", ApiVersion = "v22"
        };
        var source = new GoogleAdsCampaignReportingSource(transport, options, new CampaignBudgetRecommendationOptions());

        var result = await source.GetAsync(period, purchaseFacts.Rows);

        var campaign = result.Campaigns.Single();
        Assert.AreEqual("campaign-1", campaign.CampaignId);
        Assert.AreEqual(1L, campaign.VerifiedPurchases.Count);
        Assert.AreEqual(50m, campaign.AttributedRevenue.Amount);
        Assert.AreEqual(3m, campaign.PlatformReportedConversions.Amount);
        Assert.AreNotEqual(campaign.VerifiedPurchases.Count, (long)campaign.PlatformReportedConversions.Amount!.Value);
        Assert.AreEqual(CampaignRecommendationAction.ConfigurationRequired, campaign.Recommendation);
        Assert.IsNull(campaign.SuggestedBudgetChangePercent);
        Assert.IsFalse(campaign.Limitations.Any(x => x.Contains("customer-internal", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CampaignSource_ClickMappingFailureKeepsPlatformFactsButLeavesPurchaseRevenueUnknown()
    {
        var clickDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddDays(-3).UtcDateTime);
        var clickTime = new DateTimeOffset(clickDate.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
        var period = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange, clickDate, clickDate);
        var purchase = new PurchaseRecord
        {
            TransactionId = "txn-test-2", UserId = "private-user", FunnelId = "funnel-test",
            PurchaseType = "Subscription", BillingReason = "subscription_create", PaymentStatus = "Paid",
            IsFirstPayment = true, Amount = 50m, AmountMinor = 5000, Currency = "USD", PaidAt = clickTime.AddHours(1),
            Snapshot = new AttributionSnapshot { TouchId = "touch-test-2", Gclid = "test-click-id" }
        };
        var purchaseFacts = await new PurchaseReportingSource(new PurchaseReader(purchase), new ClickTimeReader(clickTime))
            .GetVerifiedClickAttributionCandidatesAsync();
        var transport = new CampaignTransport(period, clickDate) { MappingFails = true };
        var source = new GoogleAdsCampaignReportingSource(transport, new GoogleAdsReportingOptions
        {
            CustomerId = "1234567890", OAuthClientId = "client", OAuthClientSecret = "REDACTED_FOR_GROOMING",
            OAuthRefreshToken = "refresh", ApiVersion = "v22"
        }, new CampaignBudgetRecommendationOptions());

        var result = await source.GetAsync(period, purchaseFacts.Rows);
        var campaign = result.Campaigns.Single();

        Assert.AreEqual(ReportingDataState.Error, result.AttributionCoverage.State);
        Assert.AreEqual(3m, campaign.PlatformReportedConversions.Amount);
        Assert.IsNull(campaign.AttributedRevenue.Amount);
        Assert.IsNull(campaign.VerifiedPurchases.Count);
        Assert.IsNull(campaign.SuggestedBudgetChangePercent);
    }

    [TestMethod]
    public async Task CampaignSource_ApprovedPolicyAndSufficientEvidenceReturnsOnlyAdvisoryBudgetPercentage()
    {
        var clickDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddDays(-20).UtcDateTime);
        var clickTime = new DateTimeOffset(clickDate.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
        var period = MarketingReportingPeriodResolver.FromDates(MarketingReportingPeriodSelection.CustomRange, clickDate, clickDate);
        var purchase = new PurchaseRecord
        {
            TransactionId = "txn-test-3", UserId = "private-user", FunnelId = "funnel-test",
            PurchaseType = "Subscription", BillingReason = "subscription_create", PaymentStatus = "Paid",
            IsFirstPayment = true, Amount = 50m, AmountMinor = 5000, Currency = "USD", PaidAt = clickTime.AddHours(1),
            Snapshot = new AttributionSnapshot { TouchId = "touch-test-3", Gclid = "test-click-id" }
        };
        var purchaseFacts = await new PurchaseReportingSource(new PurchaseReader(purchase), new ClickTimeReader(clickTime))
            .GetVerifiedClickAttributionCandidatesAsync();
        var policy = new CampaignBudgetRecommendationOptions
        {
            TargetRoas = 2, MinimumRoas = 1, MinimumNewCustomers = 1, MinimumClicks = 1,
            MinimumSpend = 10, MinimumAttributionCoveragePercent = 100, ScaleChangePercent = 10,
            ReduceChangePercent = 10, PauseChangePercent = 20, MaximumScaleChangePercent = 15,
            MaximumReductionPercent = 25, MinimumReportingDays = 1, ConversionLagDays = 5
        };
        var source = new GoogleAdsCampaignReportingSource(new CampaignTransport(period, clickDate),
            new GoogleAdsReportingOptions
            {
                CustomerId = "1234567890", OAuthClientId = "client", OAuthClientSecret = "REDACTED_FOR_GROOMING",
                OAuthRefreshToken = "refresh", ApiVersion = "v22"
            }, policy);

        var result = await source.GetAsync(period, purchaseFacts.Rows);
        var campaign = result.Campaigns.Single();

        Assert.AreEqual(CampaignRecommendationAction.Scale, campaign.Recommendation);
        Assert.AreEqual(10m, campaign.SuggestedBudgetChangePercent);
        Assert.AreEqual(10m, campaign.SuggestedAverageDailyBudgetChange.Amount);
        Assert.AreEqual(100m, campaign.CurrentAverageDailyBudget.Amount);
        Assert.IsTrue(campaign.Limitations.Any(x => x.Contains("never modify", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class PurchaseReader(PurchaseRecord purchase) : IPurchaseReportingReader
    {
        public Task<PurchaseReportingRead> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PurchaseReportingRead([purchase], new() { Source = MarketingReportingSource.Purchases, State = ReportingDataState.Partial }));
    }

    private sealed class ClickTimeReader(DateTimeOffset clickTime) : IPurchaseClickProvenanceReader
    {
        public Task<DateTimeOffset?> GetOriginalClickTimeAsync(PurchaseRecord purchase, CancellationToken cancellationToken = default)
            => Task.FromResult<DateTimeOffset?>(clickTime);
    }

    private sealed class CampaignTransport(MarketingReportingPeriod period, DateOnly clickDate) : IGoogleAdsCampaignReportingTransport
    {
        public bool MappingFails { get; set; }
        public Task<GoogleAdsCampaignReportingResponse> QueryCampaignReportingAsync(MarketingReportingPeriod requested, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(period.StartDate, requested.StartDate);
            return Task.FromResult(new GoogleAdsCampaignReportingResponse(
                [new(clickDate, "campaign-1", "Test Campaign", "ENABLED", "budget-1", 20m, 10, 100, 3m, 75m, "USD", period.TimeZoneId)],
                [new("budget-1", 100m, "DAILY", false, 1, "USD")], "1234567890", "USD", period.TimeZoneId,
                period.StartDate, period.EndDate, true, false));
        }

        public Task<IReadOnlyList<GoogleAdsGclidCampaignMatch>> QueryGclidCampaignMappingsAsync(DateOnly date,
            IReadOnlyList<string> gclids, CancellationToken cancellationToken = default)
        {
            if (MappingFails) throw new HttpRequestException("safe mock failure");
            return Task.FromResult<IReadOnlyList<GoogleAdsGclidCampaignMatch>>(
                gclids.Contains("test-click-id", StringComparer.Ordinal)
                    ? [new("test-click-id", clickDate, "campaign-1")]
                    : []);
        }
    }

    private sealed class UnusedTransport : IGoogleAdsCampaignReportingTransport
    {
        public Task<GoogleAdsCampaignReportingResponse> QueryCampaignReportingAsync(MarketingReportingPeriod period, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unconfigured Ads access must not issue a request.");
        public Task<IReadOnlyList<GoogleAdsGclidCampaignMatch>> QueryGclidCampaignMappingsAsync(DateOnly clickDate, IReadOnlyList<string> gclids, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unconfigured Ads access must not issue a request.");
    }

    private sealed class CampaignHttpHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> QueryBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "oauth2.googleapis.com")
                return Json("{\"access_token\":\"fake-access\",\"expires_in\":3600}");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var query = body.RootElement.GetProperty("query").GetString()!;
            QueryBodies.Add(query);
            var response = query.StartsWith("SELECT customer.id", StringComparison.Ordinal)
                ? """[{"results":[{"customer":{"id":"1234567890","currencyCode":"USD","timeZone":"America/New_York"}}]}]"""
                : query.StartsWith("SELECT campaign_budget.resource_name", StringComparison.Ordinal)
                    ? """[{"results":[{"campaignBudget":{"resourceName":"budget-1","amountMicros":"100000000","period":"DAILY","explicitlyShared":false,"referenceCount":"1","status":"ENABLED"},"customer":{"currencyCode":"USD"}}]}]"""
                    : query.Contains("FROM click_view", StringComparison.Ordinal)
                        ? """[{"results":[{"clickView":{"gclid":"test-click-id"},"segments":{"date":"2026-10-01"},"campaign":{"id":"campaign-1"}}]}]"""
                        : """[{"results":[{"segments":{"date":"2026-10-01"},"campaign":{"id":"campaign-1","name":"Test Campaign","status":"ENABLED","campaignBudget":"budget-1"},"metrics":{"costMicros":"20000000","clicks":"12","impressions":"100","conversionsByConversionDate":"3","conversionsValueByConversionDate":"75"},"customer":{"currencyCode":"USD","timeZone":"America/New_York"}}]}]""";
            return Json(response);
        }

        private static HttpResponseMessage Json(string content)
            => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
