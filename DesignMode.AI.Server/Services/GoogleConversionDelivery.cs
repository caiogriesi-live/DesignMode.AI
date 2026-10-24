using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

public sealed class GoogleMeasurementOptions
{
    public bool Ga4Enabled { get; init; }
    public string MeasurementId { get; init; } = "";
    public string ApiSecret { get; init; } = "";
    public bool AdsEnabled { get; init; }
    public string CustomerId { get; init; } = "";
    public string ConversionActionId { get; init; } = "";
    public string DeveloperToken { get; init; } = "";
    public string LoginCustomerId { get; init; } = "";
    public string OAuthClientId { get; init; } = "";
    public string OAuthClientSecret { get; init; } = "";
    public string OAuthRefreshToken { get; init; } = "";
    public string AdsApiVersion { get; init; } = "";
    public static GoogleMeasurementOptions FromEnvironment()
    {
        string Get(string name) => Environment.GetEnvironmentVariable(name) ?? "";
        return new()
        {
            Ga4Enabled = Get("GA4_SERVER_ENABLED") == "true", MeasurementId = Get("GA4_MEASUREMENT_ID"), ApiSecret = Get("GA4_API_SECRET"),
            AdsEnabled = Get("GOOGLE_ADS_UPLOAD_ENABLED") == "true", CustomerId = Get("GOOGLE_ADS_CUSTOMER_ID"),
            ConversionActionId = Get("GOOGLE_ADS_CONVERSION_ACTION_ID"), DeveloperToken = Get("GOOGLE_ADS_DEVELOPER_TOKEN"),
            LoginCustomerId = Get("GOOGLE_ADS_LOGIN_CUSTOMER_ID"), OAuthClientId = Get("GOOGLE_ADS_OAUTH_CLIENT_ID"),
            OAuthClientSecret = Get("GOOGLE_ADS_OAUTH_CLIENT_SECRET"), OAuthRefreshToken = Get("GOOGLE_ADS_OAUTH_REFRESH_TOKEN"), AdsApiVersion = Get("GOOGLE_ADS_API_VERSION")
        };
    }
    public bool Ga4Configured => Ga4Enabled && Regex.IsMatch(MeasurementId, @"\AG-[A-Z0-9]+\z") && !string.IsNullOrWhiteSpace(ApiSecret);
    public bool AdsConfigured => AdsEnabled && Regex.IsMatch(CustomerId, @"\A[0-9]+\z") && Regex.IsMatch(ConversionActionId, @"\A[0-9]+\z")
        && Regex.IsMatch(AdsApiVersion, @"\Av[0-9]+\z") && new[] { DeveloperToken, OAuthClientId, OAuthClientSecret, OAuthRefreshToken }.All(x => !string.IsNullOrWhiteSpace(x));
}

public static class GoogleConversionPayloads
{
    public static object? Ga4(PurchaseRecord purchase)
    {
        var consent = purchase.Snapshot.Measurement;
        if (!consent.AnalyticsConsent || !Regex.IsMatch(consent.GaClientId ?? "", @"\A[0-9]{1,20}\.[0-9]{1,20}\z")) return null;
        var plan = new[] { "starter", "plus", "pro" }.Contains(purchase.Plan) ? purchase.Plan : "subscription";
        var parameters = new Dictionary<string, object>
        {
            ["transaction_id"] = purchase.TransactionId, ["value"] = purchase.Amount, ["currency"] = purchase.Currency,
            ["payment_type"] = purchase.IsFirstPayment ? "first" : purchase.IsRenewal ? "renewal" : "additional",
            ["items"] = new[] { new { item_id = plan, item_name = plan, quantity = 1, price = purchase.Amount } }
        };
        // Do not attribute renewals to a stale checkout session.
        if (purchase.IsFirstPayment && purchase.PaidAt - purchase.Snapshot.CreatedAt < TimeSpan.FromHours(24)
            && long.TryParse(consent.GaSessionId, out var session) && session > 0) parameters["session_id"] = session;
        return new
        {
            client_id = consent.GaClientId, timestamp_micros = purchase.PaidAt.ToUnixTimeMilliseconds() * 1000,
            consent = new { ad_user_data = DesignMode.AI.Shared.Models.Attribution.AdvertisingMeasurementPolicy.GoogleConsentGranted(consent) ? "GRANTED" : "DENIED", ad_personalization = "DENIED" },
            events = new[] { new { name = "purchase", @params = parameters } }
        };
    }
    // Deprecated for new production delivery. Retained for migration/unit compatibility only.
    public static object? Ads(PurchaseRecord purchase, GoogleMeasurementOptions options)
    {
        if (!purchase.IsFirstPayment || !DesignMode.AI.Shared.Models.Attribution.AdvertisingMeasurementPolicy.GoogleConsentGranted(purchase.Snapshot.Measurement)) return null;
        var snapshot = purchase.Snapshot;
        var click = !string.IsNullOrWhiteSpace(snapshot.Gclid) ? ("gclid", snapshot.Gclid)
            : !string.IsNullOrWhiteSpace(snapshot.Gbraid) ? ("gbraid", snapshot.Gbraid) : ("wbraid", snapshot.Wbraid);
        if (string.IsNullOrWhiteSpace(click.Item2)) return null;
        var conversion = new Dictionary<string, object>
        {
            [click.Item1] = click.Item2,
            ["conversionAction"] = $"customers/{options.CustomerId}/conversionActions/{options.ConversionActionId}",
            ["conversionDateTime"] = purchase.PaidAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00",
            ["conversionValue"] = purchase.Amount, ["currencyCode"] = purchase.Currency, ["orderId"] = purchase.TransactionId,
            ["consent"] = new { adUserData = "GRANTED", adPersonalization = "DENIED" }
        };
        return new { conversions = new[] { conversion }, partialFailure = true };
    }
}

public interface IGoogleConversionTransport
{
    Task<string> SendAsync(string channel, object payload);
}

public sealed class GoogleConversionTransport(HttpClient http, GoogleMeasurementOptions options) : IGoogleConversionTransport
{
    public async Task<string> SendAsync(string channel, object payload)
    {
        if (channel == "Ga4")
        {
            if (!options.Ga4Configured) return "Pending";
            using var response = await http.PostAsJsonAsync("https://www.google-analytics.com/mp/collect?measurement_id=" + Uri.EscapeDataString(options.MeasurementId) + "&api_secret=" + Uri.EscapeDataString(options.ApiSecret), payload);
            // HTTP acceptance is not proof of GA ingestion; validate payloads in GA before enabling.
            return response.IsSuccessStatusCode ? "Submitted" : "Rejected";
        }
        // Legacy UploadClickConversions: no production dispatcher calls this branch during migration.
        if (!options.AdsConfigured) return "Pending";
        using var tokenResponse = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.OAuthClientId, ["client_secret"] = options.OAuthClientSecret,
            ["refresh_token"] = options.OAuthRefreshToken, ["grant_type"] = "refresh_token"
        }));
        if (!tokenResponse.IsSuccessStatusCode) return "Rejected";
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://googleads.googleapis.com/{options.AdsApiVersion}/customers/{options.CustomerId}:uploadClickConversions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("access_token").GetString());
        request.Headers.Add("developer-token", options.DeveloperToken);
        if (!string.IsNullOrWhiteSpace(options.LoginCustomerId)) request.Headers.Add("login-customer-id", options.LoginCustomerId);
        request.Content = JsonContent.Create(payload);
        using var result = await http.SendAsync(request);
        if (!result.IsSuccessStatusCode) return "Rejected";
        using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        if (body.RootElement.TryGetProperty("partialFailureError", out var error) && error.TryGetProperty("code", out var code) && code.GetInt32() != 0) return "Rejected";
        return body.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() == 1 ? "Submitted" : "Rejected";
    }
}

public sealed class PurchaseConversionDispatcher(PurchaseMeasurementStore store, IGoogleConversionTransport transport,
    GoogleMeasurementOptions options, ILogger<PurchaseConversionDispatcher> logger)
{
    public async Task DispatchAsync(PurchaseRecord purchase)
    {
        // Ads is disconnected, even if the legacy flag is accidentally true. GA4 path is unchanged.
        foreach (var channel in new[] { "Ga4" })
        {
            // Do not acquire a lease or make any Google call without explicit configuration.
            if (channel == "Ga4" ? !options.Ga4Configured : !options.AdsConfigured) continue;
            if (!await store.AcquireAsync(purchase.TransactionId, channel)) continue;
            var payload = channel == "Ga4" ? GoogleConversionPayloads.Ga4(purchase) : GoogleConversionPayloads.Ads(purchase, options);
            var state = payload is null ? "NotEligible" : "Pending";
            if (channel == "Ga4" && DateTimeOffset.UtcNow - purchase.PaidAt > TimeSpan.FromHours(72)) state = "Expired";
            if (state == "Pending")
            {
                try { state = await transport.SendAsync(channel, payload!); }
                catch { state = "Unknown"; } // Uncertain delivery needs reconciliation, not blind replay.
            }
            await store.FinishAsync(purchase.TransactionId, channel, state);
            logger.LogInformation("[MEASUREMENT] channel {Channel}, delivery {State}", channel, state);
        }
    }
}
