using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.MarketingReporting;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GoogleAdsReportingTests
{
    private static readonly GoogleAdsReportingOptions Options = new()
    {
        CustomerId = "9876543210", DeveloperToken = "report-token", LoginCustomerId = "1234567890",
        OAuthClientId = "report-client", OAuthClientSecret = "REDACTED_FOR_GROOMING", OAuthRefreshToken = "report-refresh", ApiVersion = "v22"
    };

    private static MarketingReportingPeriod Period => new()
    {
        StartDate = new DateOnly(2026, 3, 7), EndDate = new DateOnly(2026, 3, 9),
        StartInclusive = new DateTimeOffset(2026, 3, 7, 5, 0, 0, TimeSpan.Zero),
        EndExclusive = new DateTimeOffset(2026, 3, 10, 4, 0, 0, TimeSpan.Zero)
    };

    [TestMethod]
    public async Task ReportingTransport_UsesOnlyOauthAndReportingReadRequest_PreservesRowsAndBoundaries()
    {
        var handler = new ReportingHandler();
        var transport = new GoogleAdsReportingHttpTransport(new HttpClient(handler), Options);

        var result = await transport.QueryDailySpendAsync(Period.StartDate, Period.EndDate);

        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("oauth2.googleapis.com", handler.Requests[0].Uri!.Host);
        Assert.AreEqual("POST", handler.Requests[0].Method);
        var queryRequest = handler.Requests[1];
        Assert.AreEqual("POST", queryRequest.Method);
        Assert.AreEqual("googleads.googleapis.com", queryRequest.Uri!.Host);
        Assert.AreEqual("/v22/customers/9876543210/googleAds:searchStream", queryRequest.Uri.AbsolutePath);
        using var requestJson = JsonDocument.Parse(queryRequest.Body);
        var gaql = requestJson.RootElement.GetProperty("query").GetString()!;
        StringAssert.Contains(gaql, "segments.date BETWEEN '2026-03-07' AND '2026-03-09'");
        StringAssert.Contains(gaql, "metrics.cost_micros");
        StringAssert.Contains(gaql, "customer.currency_code");
        Assert.AreEqual("1234567890", queryRequest.LoginCustomerId);
        Assert.AreEqual(3, result.Rows.Count);
        Assert.AreEqual(1.25m, result.Rows[0].Amount);
        Assert.AreEqual("CAD", result.Rows[0].Currency);
        Assert.AreEqual("9876543210", result.Rows[0].CustomerId);
        Assert.AreEqual(new DateOnly(2026, 3, 7), result.Rows[0].Date);
        Assert.IsFalse(queryRequest.Uri.AbsolutePath.Contains("upload", StringComparison.OrdinalIgnoreCase));
        var sourceParameters = typeof(GoogleAdsReportingSource).GetConstructors().Single().GetParameters();
        Assert.AreEqual("IGoogleAdsReportingTransport", sourceParameters[0].ParameterType.Name);
        Assert.IsFalse(sourceParameters.Any(x => x.ParameterType == typeof(IGoogleConversionTransport)));
    }

    [TestMethod]
    public async Task Source_ReportsCompletePartialStaleUnavailableAndErrorWithoutZeroSubstitution()
    {
        var row = new GoogleAdsDailySpend(Period.StartDate, 1.25m, "CAD", Options.CustomerId);
        var coveredRows = new[]
        {
            row,
            new GoogleAdsDailySpend(Period.StartDate.AddDays(1), 0m, "CAD", Options.CustomerId),
            new GoogleAdsDailySpend(Period.EndDate, 2.75m, "CAD", Options.CustomerId)
        };
        var transport = new StubReportingTransport { Response = new(coveredRows, HasCoverageEvidence: true) };
        var source = new GoogleAdsReportingSource(transport, Options);
        var complete = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Complete, complete.Coverage.State);
        Assert.IsNull(complete.Coverage.FreshThrough);
        Assert.AreEqual(3, complete.Coverage.RecordsObserved);
        Assert.AreEqual(3, complete.Coverage.RecordsExpected);
        StringAssert.Contains(complete.Coverage.Explanation!, "freshness is unknown");
        Assert.AreEqual(1.25m, complete.Rows[0].Amount);

        transport.Response = new([row], FreshThrough: Period.EndExclusive.AddDays(-1), HasCoverageEvidence: true);
        var partial = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Partial, partial.Coverage.State);
        Assert.AreEqual(Period.EndExclusive.AddDays(-1), partial.Coverage.FreshThrough);
        Assert.AreEqual(1, partial.Coverage.RecordsObserved);
        Assert.AreEqual(3, partial.Coverage.RecordsExpected);
        StringAssert.Contains(partial.Coverage.Explanation!, "verified coverage for every requested date");
        StringAssert.Contains(partial.Coverage.Explanation!, "earlier than the requested period end");

        transport.Response = new(coveredRows, FreshThrough: Period.EndExclusive.AddDays(-1), HasCoverageEvidence: true);
        var stale = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Stale, stale.Coverage.State);

        transport.Response = new([row], IsPartial: true);
        var reportedPartial = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Partial, reportedPartial.Coverage.State);
        StringAssert.Contains(reportedPartial.Coverage.Explanation!, "freshness is unknown");

        transport.Response = new(coveredRows);
        var unverifiedCoverage = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Partial, unverifiedCoverage.Coverage.State);
        StringAssert.Contains(unverifiedCoverage.Coverage.Explanation!, "verified coverage");

        transport.Failure = new(ReportingDataState.Unavailable, "authorization_unavailable");
        var unavailable = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Unavailable, unavailable.Coverage.State);
        Assert.AreEqual(0, unavailable.Rows.Count);
        StringAssert.Contains(unavailable.Coverage.Explanation!, "freshness is unknown");

        transport.Failure = new(ReportingDataState.Error, "report_request_failed");
        var error = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Error, error.Coverage.State);
        Assert.IsNull(error.Coverage.RecordsObserved);
        Assert.AreEqual(0, error.Rows.Count);
        StringAssert.Contains(error.Coverage.Explanation!, "freshness is unknown");

        var unconfigured = await new GoogleAdsReportingSource(transport, new()).GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Unavailable, unconfigured.Coverage.State);
        Assert.AreEqual("configuration_missing", unconfigured.Coverage.ErrorCode);
        StringAssert.Contains(unconfigured.Coverage.Explanation!, "freshness is unknown");
    }

    [TestMethod]
    public async Task Source_RejectsOutOfRangeRowsAsError()
    {
        var outside = new GoogleAdsDailySpend(Period.EndDate.AddDays(1), 8m, "USD", Options.CustomerId);
        var source = new GoogleAdsReportingSource(new StubReportingTransport { Response = new([outside]) }, Options);
        var result = await source.GetDailySpendAsync(Period);
        Assert.AreEqual(ReportingDataState.Error, result.Coverage.State);
        Assert.AreEqual(0, result.Rows.Count);
    }

    [TestMethod]
    public async Task ReportingTransport_MapsAuthorizationFailureWithoutReturningSensitiveResponse()
    {
        var handler = new ReportingHandler { AdsStatus = HttpStatusCode.Forbidden };
        var transport = new GoogleAdsReportingHttpTransport(new HttpClient(handler), Options);
        var exception = await Assert.ThrowsExceptionAsync<GoogleAdsReportingException>(() => transport.QueryDailySpendAsync(Period.StartDate, Period.EndDate));
        Assert.AreEqual(ReportingDataState.Unavailable, exception.State);
        Assert.AreEqual("authorization_unavailable", exception.Code);
        Assert.IsFalse(exception.Message.Contains("report-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReportingTransport_RequiresDeveloperTokenBeforeCallingGoogleAds()
    {
        var handler = new ReportingHandler();
        var transport = new GoogleAdsReportingHttpTransport(new HttpClient(handler), new GoogleAdsReportingOptions
        {
            CustomerId = Options.CustomerId,
            DeveloperToken = "",
            LoginCustomerId = Options.LoginCustomerId,
            OAuthClientId = Options.OAuthClientId,
            OAuthClientSecret = Options.OAuthClientSecret,
            OAuthRefreshToken = Options.OAuthRefreshToken,
            ApiVersion = Options.ApiVersion
        });

        var exception = await Assert.ThrowsExceptionAsync<GoogleAdsReportingException>(() => transport.QueryDailySpendAsync(Period.StartDate, Period.EndDate));

        Assert.AreEqual("configuration_missing", exception.Code);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private sealed class StubReportingTransport : IGoogleAdsReportingTransport
    {
        public GoogleAdsReportingResponse Response { get; set; } = new([]);
        public GoogleAdsReportingException? Failure { get; set; }
        public Task<GoogleAdsReportingResponse> QueryDailySpendAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(Period.StartDate, startDate);
            Assert.AreEqual(Period.EndDate, endDate);
            return Failure is null ? Task.FromResult(Response) : Task.FromException<GoogleAdsReportingResponse>(Failure);
        }
    }

    private sealed class ReportingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public HttpStatusCode AdsStatus { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method.Method, request.RequestUri, body, request.Headers.TryGetValues("login-customer-id", out var values) ? values.Single() : null));
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"fake-access\"}");
            if (AdsStatus != HttpStatusCode.OK) return Json(AdsStatus, "{\"error\":\"sensitive-provider-detail\"}");
            return Json(HttpStatusCode.OK, """[{"results":[{"segments":{"date":"2026-03-07"},"metrics":{"costMicros":"1250000"},"customer":{"currencyCode":"CAD","id":"9876543210"}},{"segments":{"date":"2026-03-08"},"metrics":{"costMicros":"0"},"customer":{"currencyCode":"CAD","id":"9876543210"}},{"segments":{"date":"2026-03-09"},"metrics":{"costMicros":"2750000"},"customer":{"currencyCode":"CAD","id":"9876543210"}}]}]""");
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record CapturedRequest(string Method, Uri? Uri, string Body, string? LoginCustomerId);
}
