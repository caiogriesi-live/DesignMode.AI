using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Server.Services;

/// <summary>Reporting-only Google Ads credentials. These settings are independent from conversion delivery.</summary>
public sealed class GoogleAdsReportingOptions
{
    public string CustomerId { get; init; } = "";
    public string DeveloperToken { get; init; } = "";
    public string LoginCustomerId { get; init; } = "";
    public string OAuthClientId { get; init; } = "";
    public string OAuthClientSecret { get; init; } = "";
    public string OAuthRefreshToken { get; init; } = "";
    public string ApiVersion { get; init; } = "";

    public bool IsConfigured => System.Text.RegularExpressions.Regex.IsMatch(CustomerId, @"\A[0-9]+\z")
        && System.Text.RegularExpressions.Regex.IsMatch(ApiVersion, @"\Av[0-9]+\z")
        && new[] { DeveloperToken, OAuthClientId, OAuthClientSecret, OAuthRefreshToken }.All(x => !string.IsNullOrWhiteSpace(x));

    public static GoogleAdsReportingOptions FromEnvironment() => new()
    {
        CustomerId = Environment.GetEnvironmentVariable("GOOGLE_ADS_REPORTING_CUSTOMER_ID") ?? "",
        DeveloperToken = Environment.GetEnvironmentVariable("GOOGLE_ADS_REPORTING_DEVELOPER_TOKEN") ?? "",
        LoginCustomerId = Environment.GetEnvironmentVariable("GOOGLE_ADS_REPORTING_LOGIN_CUSTOMER_ID") ?? "",
        OAuthClientId = Environment.GetEnvironmentVariable("GOOGLE_ADS_REPORTING_OAUTH_CLIENT_ID") ?? "",
        OAuthClientSecret = Environment.GetEnvironmentVariable("GOOGLE_ADS_REPORTING_OAUTH_CLIENT_SECRET") ?? "",
        OAuthRefreshToken = Environment.GetEnvironmentVariable("GOOGLE_ADS_REPORTING_OAUTH_REFRESH_TOKEN") ?? "",
        ApiVersion = Environment.GetEnvironmentVariable("GOOGLE_ADS_REPORTING_API_VERSION") ?? ""
    };
}

public sealed record GoogleAdsDailySpend(DateOnly Date, decimal Amount, string Currency, string CustomerId);
public sealed record GoogleAdsReportingResponse(IReadOnlyList<GoogleAdsDailySpend> Rows, bool IsPartial = false,
    DateTimeOffset? FreshThrough = null, bool HasCoverageEvidence = false);

/// <summary>The sole transport operation is a read-only daily reporting query.</summary>
public interface IGoogleAdsReportingTransport
{
    Task<GoogleAdsReportingResponse> QueryDailySpendAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
}

public sealed partial class GoogleAdsReportingHttpTransport(HttpClient http, GoogleAdsReportingOptions options) : IGoogleAdsReportingTransport, IGoogleAdsCampaignReportingTransport
{
    private const string Query = "SELECT segments.date, metrics.cost_micros, customer.currency_code, customer.id " +
        "FROM customer WHERE segments.date BETWEEN '{0}' AND '{1}' ORDER BY segments.date";

    public async Task<GoogleAdsReportingResponse> QueryDailySpendAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) throw new GoogleAdsReportingException(ReportingDataState.Unavailable, "configuration_missing");
        var tokenResponse = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.OAuthClientId, ["client_secret"] = options.OAuthClientSecret,
            ["refresh_token"] = options.OAuthRefreshToken, ["grant_type"] = "refresh_token"
        }), cancellationToken);
        using (tokenResponse)
        {
            if (tokenResponse.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new GoogleAdsReportingException(ReportingDataState.Unavailable, "authorization_unavailable");
            if (!tokenResponse.IsSuccessStatusCode) throw new GoogleAdsReportingException(ReportingDataState.Error, "token_request_failed");
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken)) throw new GoogleAdsReportingException(ReportingDataState.Error, "token_response_invalid");

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://googleads.googleapis.com/{options.ApiVersion}/customers/{options.CustomerId}/googleAds:searchStream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("developer-token", options.DeveloperToken);
            if (!string.IsNullOrWhiteSpace(options.LoginCustomerId)) request.Headers.Add("login-customer-id", options.LoginCustomerId);
            request.Content = System.Net.Http.Json.JsonContent.Create(new { query = string.Format(CultureInfo.InvariantCulture, Query,
                startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) });
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new GoogleAdsReportingException(ReportingDataState.Unavailable, "authorization_unavailable");
            if (!response.IsSuccessStatusCode) throw new GoogleAdsReportingException(ReportingDataState.Error, "report_request_failed");
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var rows = new List<GoogleAdsDailySpend>();
                var partial = false;
                foreach (var batch in json.RootElement.EnumerateArray())
                {
                    if (batch.TryGetProperty("partialFailureError", out var failure) && failure.ValueKind != JsonValueKind.Null) partial = true;
                    if (!batch.TryGetProperty("results", out var results)) continue;
                    foreach (var item in results.EnumerateArray())
                    {
                        var date = DateOnly.Parse(item.GetProperty("segments").GetProperty("date").GetString()!, CultureInfo.InvariantCulture);
                        var micros = decimal.Parse(item.GetProperty("metrics").GetProperty("costMicros").GetString()!, CultureInfo.InvariantCulture);
                        var currency = item.GetProperty("customer").GetProperty("currencyCode").GetString() ?? "";
                        var customerId = item.GetProperty("customer").GetProperty("id").GetString() ?? "";
                        rows.Add(new(date, micros / 1_000_000m, currency, customerId));
                    }
                }
                return new(rows, partial);
            }
            catch (GoogleAdsReportingException) { throw; }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            { throw new GoogleAdsReportingException(ReportingDataState.Error, "report_response_invalid"); }
        }
    }
}

public sealed class GoogleAdsReportingException(ReportingDataState state, string code) : Exception(code)
{
    public ReportingDataState State { get; } = state;
    public string Code { get; } = code;
}

public sealed record GoogleAdsDailySpendReport(IReadOnlyList<GoogleAdsDailySpend> Rows, ReportingSourceCoverage Coverage);

/// <summary>Internal source adapter; deliberately not exposed as an HTTP function.</summary>
public sealed class GoogleAdsReportingSource(IGoogleAdsReportingTransport transport, GoogleAdsReportingOptions options)
{
    public async Task<GoogleAdsDailySpendReport> GetDailySpendAsync(MarketingReportingPeriod period, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return Result([], ReportingDataState.Unavailable, "Reporting credentials or account identity are not configured.", "configuration_missing");
        if (period.EndDate < period.StartDate)
            return Result([], ReportingDataState.Error, "Requested reporting period is invalid.", "invalid_period");
        try
        {
            var response = await transport.QueryDailySpendAsync(period.StartDate, period.EndDate, cancellationToken);
            if (response.Rows.Any(x => x.Date < period.StartDate || x.Date > period.EndDate))
                return Result([], ReportingDataState.Error, "Google Ads returned rows outside the requested reporting period.", "response_out_of_range");
            var ordered = response.Rows.OrderBy(x => x.Date).ToArray();
            var expectedDays = (long)(period.EndDate.DayNumber - period.StartDate.DayNumber + 1);
            var observedDays = ordered.Select(x => x.Date).Distinct().LongCount();
            var incomplete = response.IsPartial || !response.HasCoverageEvidence || observedDays < expectedDays;
            var stale = response.FreshThrough is not null && response.FreshThrough < period.EndExclusive;
            var state = incomplete ? ReportingDataState.Partial : stale ? ReportingDataState.Stale : ReportingDataState.Complete;
            var explanation = incomplete ? stale
                ? "Google Ads daily results do not establish verified coverage for every requested date, and FreshThrough is earlier than the requested period end."
                : "Google Ads daily results do not establish verified coverage for every requested date." : stale
                ? "Google Ads completed the query, but freshness does not extend through the requested interval."
                : response.FreshThrough is null ? "Google Ads completed the requested daily reporting query; source freshness is unknown."
                : "Google Ads completed the requested daily reporting query.";
            return Result(ordered, state, explanation, null, observedDays, response.FreshThrough, expectedDays);
        }
        catch (GoogleAdsReportingException ex)
        {
            return Result([], ex.State, ex.State == ReportingDataState.Unavailable ? "Google Ads reporting access is unavailable." : "Google Ads reporting retrieval failed.", ex.Code);
        }
        catch (HttpRequestException)
        {
            return Result([], ReportingDataState.Error, "Google Ads reporting retrieval failed.", "transport_error");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result([], ReportingDataState.Error, "Google Ads reporting retrieval timed out.", "request_timeout");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Result([], ReportingDataState.Error, "Google Ads reporting retrieval failed.", "reporting_error");
        }
    }

    private static GoogleAdsDailySpendReport Result(IReadOnlyList<GoogleAdsDailySpend> rows, ReportingDataState state,
        string explanation, string? errorCode, long? observed = null, DateTimeOffset? freshThrough = null, long? expected = null)
    {
        if (freshThrough is null && !explanation.Contains("freshness is unknown", StringComparison.OrdinalIgnoreCase))
            explanation += " Source freshness is unknown.";
        return new(rows, new ReportingSourceCoverage { Source = MarketingReportingSource.GoogleAds, State = state,
            FreshThrough = freshThrough, RecordsObserved = observed, RecordsExpected = expected, Explanation = explanation, ErrorCode = errorCode });
    }
}
