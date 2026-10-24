using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Server.Services;

public sealed record GoogleAdsCampaignDailyFact(
    DateOnly Date, string CampaignId, string CampaignName, string Status, string BudgetResourceName,
    decimal Spend, long Clicks, long Impressions, decimal PlatformConversions,
    decimal PlatformConversionValue, string Currency, string TimeZone);

public sealed record GoogleAdsCampaignBudgetFact(
    string ResourceName, decimal Amount, string Period, bool IsShared, long ReferenceCount, string Currency);

public sealed record GoogleAdsGclidCampaignMatch(string Gclid, DateOnly ClickDate, string CampaignId);

public sealed record GoogleAdsCampaignReportingResponse(
    IReadOnlyList<GoogleAdsCampaignDailyFact> DailyFacts,
    IReadOnlyList<GoogleAdsCampaignBudgetFact> Budgets,
    string CustomerId,
    string Currency,
    string TimeZone,
    DateOnly QueryStartDate,
    DateOnly QueryEndDate,
    bool CalendarAligned,
    bool IsPartial);

public sealed record GoogleAdsCampaignReportingResult(
    IReadOnlyList<GoogleAdsCampaignDailyFact> DailyFacts,
    IReadOnlyList<GoogleAdsCampaignBudgetFact> Budgets,
    string CustomerId,
    string Currency,
    string TimeZone,
    DateOnly QueryStartDate,
    DateOnly QueryEndDate,
    bool CalendarAligned,
    ReportingSourceCoverage CampaignCoverage,
    ReportingSourceCoverage BudgetCoverage);

public sealed record CampaignBudgetRecommendationOptions
{
    public decimal? TargetRoas { get; init; }
    public decimal? MinimumRoas { get; init; }
    public decimal? TargetCac { get; init; }
    public long? MinimumNewCustomers { get; init; }
    public long? MinimumClicks { get; init; }
    public decimal? MinimumSpend { get; init; }
    public decimal? MinimumAttributionCoveragePercent { get; init; }
    public decimal? ScaleChangePercent { get; init; }
    public decimal? ReduceChangePercent { get; init; }
    public decimal? PauseChangePercent { get; init; }
    public decimal? MaximumScaleChangePercent { get; init; }
    public decimal? MaximumReductionPercent { get; init; }
    public int? MinimumReportingDays { get; init; }
    public int? ConversionLagDays { get; init; }

    public bool IsConfigured => TargetRoas is > 0 && MinimumRoas is >= 0 && MinimumRoas < TargetRoas &&
        MinimumNewCustomers is > 0 && MinimumClicks is > 0 && MinimumSpend is > 0 &&
        MinimumAttributionCoveragePercent is >= 0 and <= 100 && ScaleChangePercent is > 0 &&
        ReduceChangePercent is > 0 && PauseChangePercent is > 0 && MaximumScaleChangePercent is > 0 &&
        MaximumReductionPercent is > 0 && ScaleChangePercent <= MaximumScaleChangePercent &&
        ReduceChangePercent <= MaximumReductionPercent && PauseChangePercent <= MaximumReductionPercent &&
        MinimumReportingDays is > 0 && ConversionLagDays is >= 0;

    public static CampaignBudgetRecommendationOptions FromEnvironment() => new()
    {
        TargetRoas = Decimal("MARKETING_RECOMMENDATIONS_TARGET_ROAS"), MinimumRoas = Decimal("MARKETING_RECOMMENDATIONS_MINIMUM_ROAS"),
        TargetCac = Decimal("MARKETING_RECOMMENDATIONS_TARGET_CAC"), MinimumNewCustomers = Long("MARKETING_RECOMMENDATIONS_MINIMUM_NEW_CUSTOMERS"),
        MinimumClicks = Long("MARKETING_RECOMMENDATIONS_MINIMUM_CLICKS"), MinimumSpend = Decimal("MARKETING_RECOMMENDATIONS_MINIMUM_SPEND"),
        MinimumAttributionCoveragePercent = Decimal("MARKETING_RECOMMENDATIONS_MINIMUM_ATTRIBUTION_COVERAGE_PERCENT"),
        ScaleChangePercent = Decimal("MARKETING_RECOMMENDATIONS_SCALE_CHANGE_PERCENT"), ReduceChangePercent = Decimal("MARKETING_RECOMMENDATIONS_REDUCE_CHANGE_PERCENT"),
        PauseChangePercent = Decimal("MARKETING_RECOMMENDATIONS_PAUSE_CHANGE_PERCENT"), MaximumScaleChangePercent = Decimal("MARKETING_RECOMMENDATIONS_MAX_SCALE_CHANGE_PERCENT"),
        MaximumReductionPercent = Decimal("MARKETING_RECOMMENDATIONS_MAX_REDUCTION_PERCENT"), MinimumReportingDays = Int("MARKETING_RECOMMENDATIONS_MINIMUM_REPORTING_DAYS"),
        ConversionLagDays = Int("MARKETING_RECOMMENDATIONS_CONVERSION_LAG_DAYS")
    };

    private static decimal? Decimal(string name) => decimal.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static long? Long(string name) => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static int? Int(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public sealed record CampaignFinancialReportResult(IReadOnlyList<CampaignFinancialReport> Campaigns,
    ReportingSourceCoverage CampaignCoverage, ReportingSourceCoverage AttributionCoverage, ReportingSourceCoverage BudgetCoverage);

/// <summary>Matches only transaction GCLIDs to Google Ads click_view rows and creates advisory reports.</summary>
public sealed class GoogleAdsCampaignReportingSource(IGoogleAdsCampaignReportingTransport transport,
    GoogleAdsReportingOptions adsOptions, CampaignBudgetRecommendationOptions policyOptions)
{
    public async Task<CampaignFinancialReportResult> GetAsync(MarketingReportingPeriod period,
        IReadOnlyList<PurchaseRevenueFact> purchaseFacts, CancellationToken cancellationToken = default)
    {
        if (!adsOptions.IsConfigured)
            return Empty(ReportingDataState.Unavailable, "Google Ads reporting is not configured.");
        GoogleAdsCampaignReportingResponse response;
        try { response = await transport.QueryCampaignReportingAsync(period, cancellationToken); }
        catch (GoogleAdsReportingException ex) { return Empty(ex.State, "Campaign reporting retrieval failed.", ex.Code); }
        catch (HttpRequestException) { return Empty(ReportingDataState.Error, "Campaign reporting retrieval failed.", "transport_error"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Empty(ReportingDataState.Error, "Campaign reporting timed out.", "request_timeout"); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return Empty(ReportingDataState.Error, "Campaign reporting failed.", "reporting_error"); }

        if (response.DailyFacts.Any(x => x.Date < response.QueryStartDate || x.Date > response.QueryEndDate ||
            !x.Currency.Equals(response.Currency, StringComparison.OrdinalIgnoreCase) || x.TimeZone != response.TimeZone))
            return Empty(ReportingDataState.Error, "Google Ads returned campaign facts outside the requested account-local bounds.", "campaign_response_out_of_range");
        var rows = response.DailyFacts.ToArray();
        var adsZone = TimeZoneInfo.FindSystemTimeZoneById(response.TimeZone);
        var candidateFacts = purchaseFacts.Where(x => x.GoogleClickIdType == "gclid" && !string.IsNullOrWhiteSpace(x.GoogleClickId) &&
            x.GoogleClickCapturedAt is not null && x.GoogleClickCapturedAt <= x.PaidAt &&
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.GoogleClickCapturedAt.Value, adsZone).DateTime) >= response.QueryStartDate &&
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.GoogleClickCapturedAt.Value, adsZone).DateTime) <= response.QueryEndDate &&
            x.Currency.Equals(response.Currency, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matched = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var mappingComplete = 1;
        var recentCutoff = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(response.TimeZone)).DateTime).AddDays(-89);
        var clickGroups = candidateFacts.GroupBy(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.GoogleClickCapturedAt!.Value, adsZone).DateTime))
            .Where(group => group.Key >= recentCutoff && group.Key <= DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, adsZone).DateTime))
            .ToArray();
        await Parallel.ForEachAsync(clickGroups, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            async (group, token) =>
        {
            try
            {
                var mappingRows = await transport.QueryGclidCampaignMappingsAsync(group.Key,
                    group.Select(x => x.GoogleClickId!).Distinct(StringComparer.Ordinal).ToArray(), token);
                foreach (var mapping in mappingRows)
                    if (mapping.ClickDate == group.Key && !string.IsNullOrWhiteSpace(mapping.CampaignId))
                        matched[mapping.Gclid] = mapping.CampaignId;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { Interlocked.Exchange(ref mappingComplete, 0); }
        });

        var duplicateIds = purchaseFacts.GroupBy(x => x.TransactionId, StringComparer.Ordinal)
            .Where(x => x.Select(y => (y.AmountMinor, y.Currency, y.PaidAt, y.GoogleClickId)).Distinct().Count() > 1)
            .Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var proven = candidateFacts.Where(x => !duplicateIds.Contains(x.TransactionId) && x.GoogleClickId is not null && matched.ContainsKey(x.GoogleClickId))
            .GroupBy(x => x.TransactionId, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var attributionState = mappingComplete == 1 ? ReportingDataState.Partial : ReportingDataState.Error;
        var attributionCoverage = new ReportingSourceCoverage { Source = MarketingReportingSource.Purchases, State = attributionState,
            RecordsObserved = mappingComplete == 1 ? proven.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).LongCount() : null,
            RecordsExpected = candidateFacts.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).LongCount(),
            Explanation = mappingComplete == 1
                ? "Campaign purchases are matched only by exact transaction GCLID, click date, and Google Ads click_view campaign. Browser-supplied campaign IDs, GBRAID/WBRAID, unmatched clicks, conflicts, and clicks older than 90 days are excluded."
                : "Google Ads click identity lookup failed; canonical purchases remain unattributed to campaigns." ,
            ErrorCode = mappingComplete == 1 ? null : "click_mapping_failed" };

        var adsState = response.IsPartial || rows.Length == 0 ? ReportingDataState.Partial : ReportingDataState.Partial;
        var campaignCoverage = new ReportingSourceCoverage { Source = MarketingReportingSource.GoogleAds, State = adsState,
            RecordsObserved = rows.Select(x => x.CampaignId).Distinct(StringComparer.Ordinal).LongCount(),
            Explanation = "Campaign-level Google Ads facts are read-only; API completeness and freshness are not independently proven." };
        var budgetCoverage = new ReportingSourceCoverage { Source = MarketingReportingSource.GoogleAds, State = response.IsPartial ? ReportingDataState.Partial : ReportingDataState.Partial,
            RecordsObserved = response.Budgets.Count, Explanation = "Budget facts are read-only. Only unshared daily budgets with one campaign reference support a suggested amount." };

        var reports = new List<CampaignFinancialReport>();
        foreach (var group in rows.GroupBy(x => x.CampaignId, StringComparer.Ordinal))
        {
            var facts = group.ToArray();
            var campaignPurchases = proven.Where(x => matched.GetValueOrDefault(x.GoogleClickId ?? "") == group.Key).ToArray();
            var purchasesDeduped = campaignPurchases.GroupBy(x => x.TransactionId, StringComparer.Ordinal).Select(x => x.First()).ToArray();
            var uniqueNewCustomers = purchasesDeduped.Where(x => x.RevenueKind == PurchaseRevenueKind.SubscriptionRevenue && x.IsFirstPayment)
                .Select(x => x.CustomerKey).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).LongCount();
            var current = facts[0];
            var budget = response.Budgets.FirstOrDefault(x => x.ResourceName == current.BudgetResourceName && !x.IsShared && x.ReferenceCount == 1 &&
                x.Period == "DAILY" && x.Currency.Equals(response.Currency, StringComparison.OrdinalIgnoreCase));
            var attributedRevenue = purchasesDeduped.Sum(x => x.Amount);
            var campaignSpend = facts.Sum(x => x.Spend);
            var clicks = facts.Sum(x => x.Clicks);
            var coveragePercent = candidateFacts.Length == 0 ? 0m : 100m * proven.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).LongCount() /
                candidateFacts.Select(x => x.TransactionId).Distinct(StringComparer.Ordinal).LongCount();
            var usdComparable = response.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase);
            var periodOldEnough = policyOptions.ConversionLagDays is >= 0 && period.EndDate <= DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(response.TimeZone)).DateTime).AddDays(-policyOptions.ConversionLagDays.Value);
            var enoughEvidence = policyOptions.IsConfigured && periodOldEnough && facts.Select(x => x.Date).Distinct().Count() >= policyOptions.MinimumReportingDays &&
                uniqueNewCustomers >= policyOptions.MinimumNewCustomers && clicks >= policyOptions.MinimumClicks && campaignSpend >= policyOptions.MinimumSpend &&
                coveragePercent >= policyOptions.MinimumAttributionCoveragePercent && response.CalendarAligned && !response.IsPartial && budget is not null && usdComparable && mappingComplete == 1;
            var calculatorInput = new CampaignFinancialInput { CampaignId = group.Key, CampaignName = current.CampaignName, Currency = response.Currency,
                AdvertisingSpend = usdComparable ? campaignSpend : null, AttributedRevenue = usdComparable && mappingComplete == 1 ? attributedRevenue : null,
                NewCustomers = uniqueNewCustomers, State = enoughEvidence ? ReportingDataState.Partial : ReportingDataState.Unavailable };
            var report = MarketingFinancialCalculator.CalculateCampaign(calculatorInput, enoughEvidence ? new CampaignRecommendationPolicy
            { TargetRoas = policyOptions.TargetRoas, MinimumRoas = policyOptions.MinimumRoas, TargetCac = policyOptions.TargetCac, MinimumNewCustomers = policyOptions.MinimumNewCustomers!.Value } : null);
            var percent = enoughEvidence ? report.Recommendation switch
            { CampaignRecommendationAction.Scale => policyOptions.ScaleChangePercent, CampaignRecommendationAction.Reduce => -policyOptions.ReduceChangePercent,
              CampaignRecommendationAction.Pause => -policyOptions.PauseChangePercent, _ => 0m } : null;
            report = report with
            {
                VerifiedPurchases = new ReportingCount { Count = mappingComplete == 1 ? purchasesDeduped.LongCount() : null,
                    State = mappingComplete == 1 ? ReportingDataState.Partial : ReportingDataState.Error,
                    Explanation = mappingComplete == 1 ? "Distinct canonical paid transactions exact-matched through Google Ads click_view." : "Click identity lookup failed; verified purchase count is unknown." },
                PlatformReportedConversions = new ReportingMeasure { Amount = facts.Sum(x => x.PlatformConversions), Currency = response.Currency, State = campaignCoverage.State,
                    Explanation = "Google Ads platform conversions by conversion date; distinct from canonical verified purchases." },
                PlatformReportedConversionValue = new ReportingMeasure { Amount = facts.Sum(x => x.PlatformConversionValue), Currency = response.Currency, State = campaignCoverage.State,
                    Explanation = "Google Ads platform conversion value; not canonical Stripe revenue." },
                SuggestedBudgetChangePercent = percent,
                CurrentAverageDailyBudget = budget is null ? new ReportingMeasure { Currency = response.Currency, Explanation = "Current budget is shared, non-daily, or not uniquely assigned." }
                    : new ReportingMeasure { Amount = budget.Amount, Currency = response.Currency, State = ReportingDataState.Partial },
                SuggestedAverageDailyBudgetChange = percent is not null && budget is not null
                    ? new ReportingMeasure { Amount = decimal.Round(budget.Amount * percent.Value / 100m, 2), Currency = response.Currency, State = ReportingDataState.Partial,
                        Explanation = "Advisory estimate only. No Google Ads setting is changed." }
                    : new ReportingMeasure { Currency = response.Currency, Explanation = "Unavailable until policy, campaign evidence, and a safe daily budget are verified." },
                Recommendation = !policyOptions.IsConfigured ? CampaignRecommendationAction.ConfigurationRequired : enoughEvidence ? report.Recommendation : CampaignRecommendationAction.InsufficientData,
                RecommendationConfidence = enoughEvidence ? "Evidence threshold met; observational, not causal" : "Insufficient evidence",
                RecommendationPolicyState = policyOptions.IsConfigured ? "configured" : "configuration_required",
                ReportingTimeZone = response.TimeZone, AttributionBasis = "Exact transaction GCLID to Google Ads click_view campaign mapping",
                Limitations = report.Limitations.Concat(["Google Ads platform conversions are separate from canonical verified purchases.",
                    "No campaign-level refunds, fees, variable costs, or profitability allocation is available.",
                    "Recommendations are advisory and never modify campaign budgets, bids, status, or targeting."]).Distinct().ToArray()
            };
            reports.Add(report);
        }
        return new(reports, campaignCoverage, attributionCoverage, budgetCoverage);
    }

    private static CampaignFinancialReportResult Empty(ReportingDataState state, string explanation, string? error = null)
    {
        var coverage = new ReportingSourceCoverage { Source = MarketingReportingSource.GoogleAds, State = state, Explanation = explanation, ErrorCode = error };
        return new([], coverage, new ReportingSourceCoverage { Source = MarketingReportingSource.Purchases, State = state, Explanation = explanation }, coverage);
    }
}

public interface IGoogleAdsCampaignReportingTransport
{
    Task<GoogleAdsCampaignReportingResponse> QueryCampaignReportingAsync(
        MarketingReportingPeriod period, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoogleAdsGclidCampaignMatch>> QueryGclidCampaignMappingsAsync(
        DateOnly clickDate, IReadOnlyList<string> gclids, CancellationToken cancellationToken = default);
}

/// <summary>Read-only GAQL campaign, budget, and click-identity reports using the existing reporting OAuth settings.</summary>
public sealed partial class GoogleAdsReportingHttpTransport
{
    private readonly SemaphoreSlim accessTokenGate = new(1, 1);
    private string? cachedAccessToken;
    private DateTimeOffset refreshAccessTokenAfter;
    private const string CustomerContextQuery =
        "SELECT customer.id, customer.currency_code, customer.time_zone FROM customer";

    public async Task<GoogleAdsCampaignReportingResponse> QueryCampaignReportingAsync(
        MarketingReportingPeriod period, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            throw new GoogleAdsReportingException(ReportingDataState.Unavailable, "configuration_missing");
        if (period.EndExclusive <= period.StartInclusive)
            throw new GoogleAdsReportingException(ReportingDataState.Error, "invalid_period");

        var customerJson = await ExecuteGaqlQueryAsync(CustomerContextQuery, cancellationToken);
        using var customerDocument = JsonDocument.Parse(customerJson);
        var customer = Results(customerDocument.RootElement).Select(x => (JsonElement?)x).FirstOrDefault();
        if (customer is null)
            throw new GoogleAdsReportingException(ReportingDataState.Error, "customer_context_missing");
        var customerNode = customer.Value.GetProperty("customer");
        var customerId = StringValue(customerNode, "id");
        var currency = StringValue(customerNode, "currencyCode");
        var timeZoneId = StringValue(customerNode, "timeZone");
        if (!string.Equals(customerId, options.CustomerId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(currency) || string.IsNullOrWhiteSpace(timeZoneId))
            throw new GoogleAdsReportingException(ReportingDataState.Error, "customer_context_invalid");

        TimeZoneInfo adsTimeZone;
        try { adsTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { throw new GoogleAdsReportingException(ReportingDataState.Error, "customer_timezone_unknown"); }
        catch (InvalidTimeZoneException) { throw new GoogleAdsReportingException(ReportingDataState.Error, "customer_timezone_invalid"); }

        var startLocal = TimeZoneInfo.ConvertTime(period.StartInclusive, adsTimeZone);
        var endLocal = TimeZoneInfo.ConvertTime(period.EndExclusive.AddTicks(-1), adsTimeZone);
        var queryStart = DateOnly.FromDateTime(startLocal.DateTime);
        var queryEnd = DateOnly.FromDateTime(endLocal.DateTime);
        var campaignQuery = string.Format(CultureInfo.InvariantCulture,
            "SELECT segments.date, campaign.id, campaign.name, campaign.status, campaign.campaign_budget, " +
            "metrics.cost_micros, metrics.clicks, metrics.impressions, metrics.conversions_by_conversion_date, " +
            "metrics.conversions_value_by_conversion_date, customer.currency_code, customer.time_zone " +
            "FROM campaign WHERE segments.date BETWEEN '{0}' AND '{1}' AND campaign.status != 'REMOVED' " +
            "ORDER BY campaign.id, segments.date",
            queryStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), queryEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var campaignJson = await ExecuteGaqlQueryAsync(campaignQuery, cancellationToken);
        using var campaignDocument = JsonDocument.Parse(campaignJson);
        var daily = new List<GoogleAdsCampaignDailyFact>();
        var partial = HasPartialFailure(campaignDocument.RootElement);
        foreach (var result in Results(campaignDocument.RootElement))
        {
            var segments = result.GetProperty("segments");
            var campaign = result.GetProperty("campaign");
            var budgetResource = StringValue(campaign, "campaignBudget");
            var metrics = result.GetProperty("metrics");
            daily.Add(new GoogleAdsCampaignDailyFact(
                DateOnly.Parse(StringValue(segments, "date"), CultureInfo.InvariantCulture),
                StringValue(campaign, "id"), StringValue(campaign, "name"), StringValue(campaign, "status"),
                budgetResource, Micros(metrics, "costMicros"), LongValue(metrics, "clicks"),
                LongValue(metrics, "impressions"), DecimalValue(metrics, "conversionsByConversionDate"),
                DecimalValue(metrics, "conversionsValueByConversionDate"),
                StringValue(result.GetProperty("customer"), "currencyCode"),
                StringValue(result.GetProperty("customer"), "timeZone")));
        }

        const string budgetsQuery = "SELECT campaign_budget.resource_name, campaign_budget.amount_micros, " +
            "campaign_budget.period, campaign_budget.explicitly_shared, campaign_budget.reference_count, " +
            "campaign_budget.status, customer.currency_code FROM campaign_budget " +
            "WHERE campaign_budget.status != 'REMOVED'";
        var budgetRows = new List<GoogleAdsCampaignBudgetFact>();
        try
        {
            var budgetJson = await ExecuteGaqlQueryAsync(budgetsQuery, cancellationToken);
            using var budgetDocument = JsonDocument.Parse(budgetJson);
            partial |= HasPartialFailure(budgetDocument.RootElement);
            foreach (var result in Results(budgetDocument.RootElement))
            {
                var budget = result.GetProperty("campaignBudget");
                budgetRows.Add(new GoogleAdsCampaignBudgetFact(
                    StringValue(budget, "resourceName"), Micros(budget, "amountMicros"),
                    StringValue(budget, "period"), BoolValue(budget, "explicitlyShared"),
                    LongValue(budget, "referenceCount"), StringValue(result.GetProperty("customer"), "currencyCode")));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { partial = true; }

        return new(daily, budgetRows, customerId, currency, timeZoneId, queryStart, queryEnd,
            string.Equals(timeZoneId, period.TimeZoneId, StringComparison.OrdinalIgnoreCase), partial);
    }

    public async Task<IReadOnlyList<GoogleAdsGclidCampaignMatch>> QueryGclidCampaignMappingsAsync(
        DateOnly clickDate, IReadOnlyList<string> gclids, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            throw new GoogleAdsReportingException(ReportingDataState.Unavailable, "configuration_missing");
        var validIds = gclids.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 512 &&
            Regex.IsMatch(x, @"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal).ToArray();
        var mapped = new List<GoogleAdsGclidCampaignMatch>();
        foreach (var batch in validIds.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inClause = string.Join(", ", batch.Select(x => $"'{x}'"));
            var query = string.Format(CultureInfo.InvariantCulture,
                "SELECT click_view.gclid, campaign.id, segments.date FROM click_view " +
                "WHERE segments.date = '{0}' AND click_view.gclid IN ({1})",
                clickDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), inClause);
            var json = await ExecuteGaqlQueryAsync(query, cancellationToken);
            using var document = JsonDocument.Parse(json);
            foreach (var result in Results(document.RootElement))
            {
                mapped.Add(new GoogleAdsGclidCampaignMatch(
                    StringValue(result.GetProperty("clickView"), "gclid"),
                    DateOnly.Parse(StringValue(result.GetProperty("segments"), "date"), CultureInfo.InvariantCulture),
                    StringValue(result.GetProperty("campaign"), "id")));
            }
        }
        return mapped;
    }

    private async Task<string> ExecuteGaqlQueryAsync(string query, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        using (var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://googleads.googleapis.com/{options.ApiVersion}/customers/{options.CustomerId}/googleAds:searchStream"))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            if (!string.IsNullOrWhiteSpace(options.DeveloperToken)) request.Headers.Add("developer-token", options.DeveloperToken);
            if (!string.IsNullOrWhiteSpace(options.LoginCustomerId)) request.Headers.Add("login-customer-id", options.LoginCustomerId);
            request.Content = System.Net.Http.Json.JsonContent.Create(new { query });
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new GoogleAdsReportingException(ReportingDataState.Unavailable, "authorization_unavailable");
            if (!response.IsSuccessStatusCode)
                throw new GoogleAdsReportingException(ReportingDataState.Error, "report_request_failed");
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await accessTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(cachedAccessToken) && DateTimeOffset.UtcNow < refreshAccessTokenAfter)
                return cachedAccessToken;
            using var tokenResponse = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.OAuthClientId, ["client_secret"] = options.OAuthClientSecret,
                ["refresh_token"] = options.OAuthRefreshToken, ["grant_type"] = "refresh_token"
            }), cancellationToken);
            if (tokenResponse.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new GoogleAdsReportingException(ReportingDataState.Unavailable, "authorization_unavailable");
            if (!tokenResponse.IsSuccessStatusCode)
                throw new GoogleAdsReportingException(ReportingDataState.Error, "token_request_failed");
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            cachedAccessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(cachedAccessToken))
                throw new GoogleAdsReportingException(ReportingDataState.Error, "token_response_invalid");
            var expiresSeconds = tokenJson.RootElement.TryGetProperty("expires_in", out var expires)
                && int.TryParse(expires.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 3600;
            refreshAccessTokenAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresSeconds - 60));
            return cachedAccessToken!;
        }
        finally { accessTokenGate.Release(); }
    }

    private static IEnumerable<JsonElement> Results(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var batch in root.EnumerateArray())
            if (batch.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var result in results.EnumerateArray()) yield return result;
    }

    private static bool HasPartialFailure(JsonElement root)
        => root.ValueKind == JsonValueKind.Array && root.EnumerateArray().Any(batch =>
            batch.TryGetProperty("partialFailureError", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);

    private static string StringValue(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";

    private static decimal DecimalValue(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0m;

    private static decimal Micros(JsonElement parent, string name) => DecimalValue(parent, name) / 1_000_000m;

    private static long LongValue(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0L;

    private static bool BoolValue(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || bool.TryParse(value.ToString(), out var result) && result);
}
