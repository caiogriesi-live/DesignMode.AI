using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DesignMode.AI.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PurchaseMeasurementTests
{
    private static GoogleMeasurementOptions Options => new()
    {
        Ga4Enabled = true, MeasurementId = "G-TEST123", ApiSecret = "test-secret", AdsEnabled = true,
        CustomerId = "123", ConversionActionId = "456", DeveloperToken = "test", OAuthClientId = "test",
        OAuthClientSecret = "REDACTED_FOR_GROOMING", OAuthRefreshToken = "test", AdsApiVersion = "v22"
    };
    private static PurchaseRecord Example(string? gclid = "test-click", string? gbraid = null, string? wbraid = null) => new()
    {
        TransactionId = Guid.NewGuid().ToString(), UserId = "internal-user", ProjectId = "internal-project", FunnelId = "internal-funnel",
        StripeTransactionId = "in_internal", Amount = 49m, Currency = "USD", Plan = "plus", IsFirstPayment = true, PaidAt = DateTimeOffset.UtcNow,
        Snapshot = new() { Gclid = gclid, Gbraid = gbraid, Wbraid = wbraid, Measurement = new() { AnalyticsConsent = true, AdvertisingConsent = true, GaClientId = "123.456", GaSessionId = "123456" } }
    };

    [TestMethod]
    public void Ga4Payload_ContainsOnlySafeCommerceAndGoogleIdentifiers()
    {
        var purchase = Example(); var json = JsonSerializer.Serialize(GoogleConversionPayloads.Ga4(purchase));
        StringAssert.Contains(json, "purchase"); StringAssert.Contains(json, purchase.TransactionId);
        foreach (var forbidden in new[] { "internal-user", "internal-project", "internal-funnel", "in_internal", "test-click", "email", "AI" }) Assert.IsFalse(json.Contains(forbidden));
        Assert.IsNull(GoogleConversionPayloads.Ga4(purchase with { Snapshot = purchase.Snapshot with { Measurement = new() } }));
    }
    [TestMethod]
    [DataRow("gclid")]
    [DataRow("gbraid")]
    [DataRow("wbraid")]
    public void AdsPayload_ResolvesClickTypeAndRejectsRenewals(string click)
    {
        var purchase = Example(click == "gclid" ? "test-click" : null, click == "gbraid" ? "test-click" : null, click == "wbraid" ? "test-click" : null);
        var json = JsonSerializer.Serialize(GoogleConversionPayloads.Ads(purchase, Options));
        StringAssert.Contains(json, click); StringAssert.Contains(json, purchase.TransactionId);
        StringAssert.Contains(json, "customers/123/conversionActions/456");
        Assert.IsNull(GoogleConversionPayloads.Ads(purchase with { IsFirstPayment = false, IsRenewal = true }, Options));
        Assert.IsNull(GoogleConversionPayloads.Ads(purchase with { Snapshot = purchase.Snapshot with { Measurement = new() } }, Options));
    }
    [TestMethod]
    public void StripeTransactionIdentityAndCurrency_AreStable()
    {
        Assert.AreEqual(PurchaseMeasurementStore.TransactionId("in_same"), PurchaseMeasurementStore.TransactionId("in_same"));
        Assert.AreNotEqual(PurchaseMeasurementStore.TransactionId("in_other"), PurchaseMeasurementStore.TransactionId("in_same"));
        Assert.AreEqual(49m, MeasurementMoney.FromMinor(4900, "usd"));
        Assert.AreEqual(4900m, MeasurementMoney.FromMinor(4900, "jpy"));
    }
    [TestMethod]
    public async Task GoogleTransport_UsesMockHttpAndChecksPartialFailures()
    {
        var handler = new StubHttp(); var transport = new GoogleConversionTransport(new HttpClient(handler), Options);
        Assert.AreEqual("Submitted", await transport.SendAsync("Ga4", GoogleConversionPayloads.Ga4(Example())!));
        Assert.AreEqual("Submitted", await transport.SendAsync("Ads", GoogleConversionPayloads.Ads(Example(), Options)!));
        handler.PartialFailure = true;
        Assert.AreEqual("Rejected", await transport.SendAsync("Ads", GoogleConversionPayloads.Ads(Example(), Options)!));
        Assert.AreEqual(5, handler.Calls);
    }
    private sealed class StubHttp : HttpMessageHandler
    {
        public int Calls; public bool PartialFailure;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var json = request.RequestUri!.Host == "oauth2.googleapis.com" ? "{\"access_token\":\"fake\"}"
                : PartialFailure ? "{\"partialFailureError\":{\"code\":3}}" : "{\"results\":[{}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [TestMethod]
    [TestCategory("LocalIntegration")]
    [DataRow("Gclid")]
    [DataRow("Gbraid")]
    [DataRow("Wbraid")]
    public async Task SignedWebhook_PersistsOnceRejectsTamperingAndPreservesReplayDelivery(string clickField)
    {
        if (Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS") != "true") Assert.Inconclusive("Opt into DynamoDB Local tests.");
        LocalDynamoDbTestSafety.RequireLoopback();
        var clients = new AttributionStore(NullLogger<AttributionStore>.Instance);
        using var db = clients.CreateClient();
        foreach (var table in new[] { "Projects", "AttributionTouches", "CheckoutAttempts", "AttributionSnapshots", "Purchases", "PurchaseCustomers", "AdsDeliveries" })
        {
            try { await db.DescribeTableAsync(table); }
            catch (ResourceNotFoundException) { await db.CreateTableAsync(new CreateTableRequest { TableName = table, BillingMode = BillingMode.PAY_PER_REQUEST, KeySchema = [new("Id", KeyType.HASH)], AttributeDefinitions = [new("Id", ScalarAttributeType.S)] }); }
        }
        var user = Guid.NewGuid().ToString(); var project = Guid.NewGuid().ToString(); var funnel = Guid.NewGuid().ToString(); var acquisition = Guid.NewGuid().ToString();
        var cleanup = new List<(string, string)> { ("Projects", project), ("AttributionTouches", acquisition), ("PurchaseCustomers", user) };
        Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = new() { S = id } };
        try
        {
            var projectRow = Key(project); projectRow["UserId"] = new() { S = user }; projectRow["OriginatingFunnelId"] = new() { S = funnel }; projectRow["AcquisitionId"] = new() { S = acquisition };
            await db.PutItemAsync("Projects", projectRow);
            var touch = Key(acquisition); touch["FunnelId"] = new() { S = funnel }; touch[clickField] = new() { S = "original-click" }; touch["CreatedAt"] = new() { S = DateTimeOffset.UtcNow.ToString("O") };
            await db.PutItemAsync("AttributionTouches", touch);
            var attempt = await clients.CreateCheckoutAttemptAsync(user, project, funnel, "pro", 59m, "usd", new MeasurementContext { AnalyticsConsent = true, AdvertisingConsent = true, GaClientId = "123.456", GaSessionId = "123456" }, "monthly");
            Assert.IsNotNull(attempt); cleanup.Add(("CheckoutAttempts", attempt.CheckoutAttemptId)); cleanup.Add(("AttributionSnapshots", attempt.AttributionSnapshotId));
            touch[clickField] = new() { S = "later-click" }; await db.PutItemAsync("AttributionTouches", touch);
            var storeLog = new RecordingLogger<PurchaseMeasurementStore>();
            var store = new PurchaseMeasurementStore(clients, storeLog);
            var payment = new VerifiedPayment("in_" + Guid.NewGuid(), "evt_test", attempt.CheckoutAttemptId, "sub_test", 5900, "usd", DateTimeOffset.UtcNow, true, false, "subscription_create");
            var oldEnabled = Environment.GetEnvironmentVariable("MEASUREMENT_LEDGER_ENABLED");
            var oldSecret = Environment.GetEnvironmentVariable("StripeWebhookSecret");
            var oldApiKey = Environment.GetEnvironmentVariable("StripeSecretKey");
            const string signingSecret = "offline-webhook-test-secret";
            var stripeHttp = new InvoiceHistoryHttp();
            var resolver = new StripeVerifiedPaymentResolver(NullLogger<StripeVerifiedPaymentResolver>.Instance,
                new Stripe.StripeClient("sk_test_offline", httpClient: new Stripe.SystemNetHttpClient(new HttpClient(stripeHttp))));
            var adsStore = new AdsDeliveryStore(clients, store);
            var adsEnvironment = new Dictionary<string, string>
            {
                ["ADS_DELIVERY_MODE"] = "dry-run",
                ["ADS_ELIGIBILITY_VERSION"] = "local-sidecar-v1",
                ["ADS_ACTIVATION_CUTOFF"] = "2026-09-01T00:00:00Z",
                ["ADS_MEASUREMENT_ENVIRONMENT"] = "Local",
                ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development",
                ["USE_LOCAL_DYNAMODB"] = "true"
            };
            var adsOptions = AdsDeliveryOptions.FromEnvironment(key => adsEnvironment.GetValueOrDefault(key));
            Assert.IsTrue(adsOptions.CanCaptureSidecar);
            Assert.IsFalse(adsOptions.CanDeliverToGoogle);
            var provenance = new AdsVerifiedProvenance(adsStore, adsOptions, NullLogger<AdsVerifiedProvenance>.Instance);
            var webhook = new DesignModeFunctions(NullLogger<DesignModeFunctions>.Instance,
                null!, clients, null!, null!, null!, null!, new StripeVerifiedPurchaseHandler(store, resolver, adsProvenance: provenance));
            try
            {
                Environment.SetEnvironmentVariable("MEASUREMENT_LEDGER_ENABLED", "true");
                Environment.SetEnvironmentVariable("StripeWebhookSecret", signingSecret);
                Environment.SetEnvironmentVariable("StripeSecretKey", "sk_test_offline");
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var json = JsonSerializer.Serialize(new { id = "evt_test", @object = "event", livemode = false, api_version = "2026-04-22.dahlia", type = "invoice.paid", created = timestamp,
                    data = new { @object = new { id = payment.ProviderTransactionId, @object = "invoice", status = "paid",
                        amount_paid = 5900, currency = "usd", paid_out_of_band = false, created = timestamp, metadata = new { },
                        lines = new { data = new[] { new { metadata = new { checkoutAttemptId = "untrusted_line_attempt" } } } },
                        billing_reason = "subscription_create", parent = new { type = "subscription_details", subscription_details = new {
                            subscription = "sub_test", metadata = new { checkoutAttemptId = attempt.CheckoutAttemptId } } } } } });
                var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes($"{timestamp}.{json}"))).ToLowerInvariant();
                HttpRequest Request(string body)
                {
                    var request = new DefaultHttpContext().Request;
                    request.Method = "POST"; request.Path = "/api/StripeWebhook";
                    request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
                    request.Headers["Stripe-Signature"] = $"t={timestamp},v1={hash}";
                    return request;
                }
                // Call the registered function method itself, not just the purchase service.
                Assert.IsInstanceOfType(await webhook.StripeWebhook(Request(json + " ")), typeof(BadRequestObjectResult));
                Assert.AreEqual(0, stripeHttp.Calls);
                Assert.IsNull(await store.FindAsync(PurchaseMeasurementStore.TransactionId(payment.ProviderTransactionId)));
                Assert.IsInstanceOfType(await webhook.StripeWebhook(Request(json)), typeof(OkObjectResult));
                Assert.IsInstanceOfType(await webhook.StripeWebhook(Request(json)), typeof(OkObjectResult));
                Assert.AreEqual(1, stripeHttp.Calls, "Replay must not requalify or call Stripe.");
                Assert.AreEqual(1, storeLog.Messages.Count(x => x == "[STRIPE PURCHASE] persistence invoked"));
                Assert.AreEqual(1, storeLog.Messages.Count(x => x == "[STRIPE PURCHASE] canonical purchase created"));
                Assert.AreEqual(1, storeLog.Messages.Count(x => x == "[STRIPE PURCHASE] GA4 purchase delivery queued"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("MEASUREMENT_LEDGER_ENABLED", oldEnabled);
                Environment.SetEnvironmentVariable("StripeWebhookSecret", oldSecret);
                Environment.SetEnvironmentVariable("StripeSecretKey", oldApiKey);
            }
            var purchase = (await store.FindAsync(PurchaseMeasurementStore.TransactionId(payment.ProviderTransactionId)))!; cleanup.Add(("Purchases", purchase.TransactionId));
            cleanup.Add(("AdsDeliveries", purchase.TransactionId));
            var provenanceRow = await adsStore.FindAsync(purchase.TransactionId);
            Assert.IsNotNull(provenanceRow); Assert.AreEqual(false, provenanceRow.StripeLivemode);
            Assert.AreEqual("Development", provenanceRow.DeploymentEnvironment);
            Assert.AreEqual(0L, provenanceRow.Revision, "Duplicate webhook must not replace provenance.");
            Assert.AreEqual(TimeSpan.Zero, adsOptions.ActivationCutoff.Offset);
            Assert.AreEqual(TimeSpan.Zero, purchase.PaidAt.Offset);
            Assert.AreEqual(TimeSpan.Zero, provenanceRow.ProvenanceCapturedAt.Offset);
            Assert.IsTrue(purchase.PaidAt >= adsOptions.ActivationCutoff);
            Assert.IsTrue(provenanceRow.ProvenanceCapturedAt >= adsOptions.ActivationCutoff);
            var sidecars = await db.ScanAsync(new ScanRequest { TableName = AdsDeliveryStore.TableName });
            Assert.AreEqual(1, sidecars.Items.Count(x => x["Id"].S == purchase.TransactionId));
            var dryRun = new AdsDryRunService(adsStore, new AdsEligibilityService(adsOptions), adsOptions,
                NullLogger<AdsDryRunService>.Instance);
            var dryRunResults = await dryRun.RunAsync();
            Assert.AreEqual(1, dryRunResults["NotEligible:STRIPE_TEST_MODE"]);
            provenanceRow = await adsStore.FindAsync(purchase.TransactionId);
            Assert.AreEqual(AdsDeliveryStatus.NotEligible, provenanceRow!.AdsStatus);
            Assert.AreEqual("STRIPE_TEST_MODE", provenanceRow.AdsEligibilityReason);
            var queuedRow = await db.GetItemAsync("Purchases", Key(purchase.TransactionId));
            Assert.AreEqual(attempt.CheckoutAttemptId, purchase.CheckoutAttemptId);
            Assert.AreEqual("Pending", queuedRow.Item["Ga4Status"].S);
            var duplicates = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.RecordAsync(payment with { StripeEventId = "evt_retry" })));
            Assert.IsTrue(duplicates.All(x => x.TransactionId == purchase.TransactionId));
            Assert.IsTrue(purchase.IsFirstPayment); Assert.IsFalse(purchase.IsRenewal);
            Assert.AreEqual(project, purchase.ProjectId); Assert.AreEqual(funnel, purchase.FunnelId); Assert.AreEqual(acquisition, purchase.AcquisitionId);
            StringAssert.Contains(JsonSerializer.Serialize(purchase.Snapshot), "original-click"); Assert.IsFalse(JsonSerializer.Serialize(purchase.Snapshot).Contains("later-click"));
            var renewal = await store.RecordAsync(payment with { ProviderTransactionId = "in_" + Guid.NewGuid(), FirstPaymentCandidate = false, Renewal = true, BillingReason = "subscription_cycle" });
            cleanup.Add(("Purchases", renewal.TransactionId)); Assert.IsFalse(renewal.IsFirstPayment); Assert.IsTrue(renewal.IsRenewal);
            Assert.IsNull(await adsStore.FindAsync(renewal.TransactionId), "Recording Purchases directly must not manufacture verified provenance.");
            var fake = new StubTransport();
            var dispatcher = new PurchaseConversionDispatcher(store, fake, Options, NullLogger<PurchaseConversionDispatcher>.Instance);
            await Task.WhenAll(dispatcher.DispatchAsync(purchase), dispatcher.DispatchAsync(purchase));
            await dispatcher.DispatchAsync(purchase);
            // Legacy AdsEnabled is true in Options: it must no longer activate production dispatch.
            Assert.AreEqual(0, fake.AdsCalls); Assert.AreEqual(1, fake.GaCalls);
            await dispatcher.DispatchAsync(renewal); Assert.AreEqual(0, fake.AdsCalls); Assert.AreEqual(2, fake.GaCalls);
            var additional = await store.RecordAsync(payment with { ProviderTransactionId = "in_" + Guid.NewGuid(), SubscriptionId = "sub_second" });
            cleanup.Add(("Purchases", additional.TransactionId)); Assert.IsFalse(additional.IsFirstPayment); Assert.IsFalse(additional.IsRenewal);
            await dispatcher.DispatchAsync(additional); Assert.AreEqual(0, fake.AdsCalls); Assert.AreEqual(3, fake.GaCalls);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.RecordAsync(payment with { ProviderTransactionId = "in_missing", CheckoutAttemptId = Guid.NewGuid().ToString() }));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.RecordAsync(payment with { AmountMinor = 0 }));
            var saved = await store.FindAsync(purchase.TransactionId); Assert.AreEqual(59m, saved!.Amount);
            var failedDelivery = await store.RecordAsync(payment with { ProviderTransactionId = "in_" + Guid.NewGuid() });
            cleanup.Add(("Purchases", failedDelivery.TransactionId));
            var failingDispatcher = new PurchaseConversionDispatcher(store, new FailingTransport(), Options, NullLogger<PurchaseConversionDispatcher>.Instance);
            await failingDispatcher.DispatchAsync(failedDelivery);
            var replay = await store.RecordAsync(payment with { ProviderTransactionId = failedDelivery.StripeTransactionId, StripeEventId = "evt_replay_after_delivery_failure" });
            Assert.AreEqual(failedDelivery.TransactionId, replay.TransactionId);
            var failedRow = await db.GetItemAsync("Purchases", Key(replay.TransactionId));
            Assert.AreEqual("Unknown", failedRow.Item["Ga4Status"].S);
        }
        finally { foreach (var (table, id) in cleanup) await db.DeleteItemAsync(table, Key(id)); }
    }
    private sealed class StubTransport : IGoogleConversionTransport
    {
        public int AdsCalls, GaCalls;
        public Task<string> SendAsync(string channel, object payload) { if (channel == "Ads") Interlocked.Increment(ref AdsCalls); else Interlocked.Increment(ref GaCalls); return Task.FromResult("Submitted"); }
    }
    private sealed class FailingTransport : IGoogleConversionTransport
    {
        public Task<string> SendAsync(string channel, object payload) => throw new HttpRequestException("Simulated offline transport");
    }
    private sealed class InvoiceHistoryHttp : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.AreEqual("/v1/invoices", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"object":"list","data":[],"has_more":false}""", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Messages) Messages.Add(formatter(state, exception)); }
    }
}
