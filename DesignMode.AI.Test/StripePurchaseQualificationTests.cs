using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class StripePurchaseQualificationTests
{
    private static string Invoice(string status = "paid", long amount = 5900, bool outOfBand = false) =>
        JsonSerializer.Serialize(new { api_version = "2026-04-22.dahlia", type = "invoice.paid", data = new { @object = new { id = "in_test", status, amount_paid = amount,
            paid_out_of_band = outOfBand, created = 1700000000, currency = "usd", billing_reason = "subscription_create",
            metadata = new { }, lines = new { data = new[] { new { metadata = new { checkoutAttemptId = "untrusted_line_attempt" } } } },
            parent = new { type = "subscription_details", subscription_details = new { subscription = "sub_test", metadata = new { checkoutAttemptId = "attempt_test" } } } } } });

    private static StripeVerifiedPaymentResolver Resolver(string response) => new(
        NullLogger<StripeVerifiedPaymentResolver>.Instance,
        new StripeClient("sk_test_offline", httpClient: new SystemNetHttpClient(new HttpClient(new FakeHttp(response)))));

    [TestMethod]
    public async Task DahliaInvoice_EmptyInvoiceMetadataUsesParentNotConflictingLineMetadata()
    {
        var result = await Resolver("""{"object":"list","data":[],"has_more":false}""")
            .ResolveAsync(new Event { Type = "invoice.paid", Id = "evt_test", Created = DateTime.UtcNow }, Invoice());
        Assert.IsNotNull(result);
        Assert.AreEqual("attempt_test", result.CheckoutAttemptId);
        Assert.AreEqual(5900L, result.AmountMinor);
        Assert.IsTrue(result.FirstPaymentCandidate);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LegacyInvoice_ResolvesSnapshotOrFetchesSubscription(bool hasSnapshot)
    {
        var fixture = JsonNode.Parse(Invoice())!;
        var invoice = fixture["data"]!["object"]!.AsObject();
        invoice.Remove("parent");
        invoice["subscription"] = "sub_test";
        if (hasSnapshot) invoice["subscription_details"] = JsonNode.Parse("""{"metadata":{"checkoutAttemptId":"attempt_test"}}""");
        fixture["api_version"] = "2024-06-20";
        var http = new SubscriptionHttp();
        var resolver = new StripeVerifiedPaymentResolver(NullLogger<StripeVerifiedPaymentResolver>.Instance,
            new StripeClient("sk_test_offline", httpClient: new SystemNetHttpClient(new HttpClient(http))));
        var payment = await resolver.ResolveAsync(new Event { Type = "invoice.paid", Created = DateTime.UtcNow }, fixture.ToJsonString());
        Assert.IsNotNull(payment);
        Assert.AreEqual("attempt_test", payment.CheckoutAttemptId);
        Assert.AreEqual(hasSnapshot ? 0 : 1, http.SubscriptionCalls);
    }

    [TestMethod]
    public async Task DahliaInvoice_PrefersParentOverConflictingLegacyFields()
    {
        var fixture = JsonNode.Parse(Invoice())!;
        var invoice = fixture["data"]!["object"]!;
        invoice["subscription"] = "legacy_subscription";
        invoice["subscription_details"] = JsonNode.Parse("""{"metadata":{"checkoutAttemptId":"legacy_attempt"}}""");
        var result = await Resolver("""{"object":"list","data":[],"has_more":false}""")
            .ResolveAsync(new Event { Type = "invoice.paid", Created = DateTime.UtcNow }, fixture.ToJsonString());
        Assert.AreEqual("attempt_test", result!.CheckoutAttemptId);
        Assert.AreEqual("sub_test", result.SubscriptionId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingContext_LogsSpecificSkipWithoutTrustingLineMetadata(bool hasSubscription)
    {
        var fixture = JsonNode.Parse(Invoice())!;
        var details = fixture["data"]!["object"]!["parent"]!["subscription_details"]!.AsObject();
        details["metadata"] = new JsonObject();
        if (!hasSubscription) details.Remove("subscription");
        var log = new SafeLogger();
        // Only a subscription lookup is allowed; missing context must never query history.
        var http = new ContextHttp(false);
        var resolver = new StripeVerifiedPaymentResolver(log,
            new StripeClient("sk_test_offline", httpClient: new SystemNetHttpClient(new HttpClient(http))));
        Assert.IsNull(await resolver.ResolveAsync(new Event { Type = "invoice.paid" }, fixture.ToJsonString()));
        Assert.AreEqual(hasSubscription ? 1 : 0, http.Calls);
        CollectionAssert.Contains(log.Messages, hasSubscription
            ? "[STRIPE PURCHASE] skipped: checkout attempt reference unavailable"
            : "[STRIPE PURCHASE] skipped: subscription context unavailable");
        Assert.IsFalse(string.Join(" ", log.Messages).Contains("sub_test"));
        Assert.IsFalse(string.Join(" ", log.Messages).Contains("untrusted_line_attempt"));
    }

    [TestMethod]
    public async Task HistoryFailure_IsLoggedAndRethrownNotConvertedToNull()
    {
        var log = new SafeLogger();
        var resolver = new StripeVerifiedPaymentResolver(log,
            new StripeClient("sk_test_offline", httpClient: new SystemNetHttpClient(new HttpClient(new ContextHttp(true)))));
        await Assert.ThrowsExceptionAsync<StripeException>(() => resolver.ResolveAsync(new Event { Type = "invoice.paid" }, Invoice()));
        CollectionAssert.Contains(log.Messages, "[STRIPE PURCHASE] checkout attempt reference resolved");
        CollectionAssert.Contains(log.Messages, "[STRIPE PURCHASE] failed: paid invoice history lookup");
        Assert.IsFalse(string.Join(" ", log.Messages).Contains("sensitive_test_error"));
    }

    private sealed class ContextHttp(bool failHistory) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.AreEqual(failHistory ? "/v1/invoices" : "/v1/subscriptions/sub_test", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(failHistory ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(failHistory ? """{"error":{"type":"invalid_request_error","message":"sensitive_test_error"}}"""
                : """{"id":"sub_test","object":"subscription","metadata":{}}""", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class SafeLogger : ILogger<StripeVerifiedPaymentResolver>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class SubscriptionHttp : HttpMessageHandler
    {
        public int SubscriptionCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var subscription = request.RequestUri!.AbsolutePath == "/v1/subscriptions/sub_test";
            Assert.IsTrue(subscription || request.RequestUri.AbsolutePath == "/v1/invoices");
            if (subscription) SubscriptionCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(subscription
                ? """{"id":"sub_test","object":"subscription","metadata":{"checkoutAttemptId":"attempt_test"}}"""
                : """{"object":"list","data":[],"has_more":false}""", Encoding.UTF8, "application/json") });
        }
    }

    [TestMethod]
    [DataRow("open", 5900L, false)]
    [DataRow("paid", 0L, false)]
    [DataRow("paid", 5900L, true)]
    public async Task NonQualifyingInvoice_IsSkippedWithoutNetwork(string status, long amount, bool outOfBand)
    {
        var resolver = new StripeVerifiedPaymentResolver(NullLogger<StripeVerifiedPaymentResolver>.Instance,
            new StripeClient("sk_test_offline", httpClient: new SystemNetHttpClient(new HttpClient(new FakeHttp(null)))));
        Assert.IsNull(await resolver.ResolveAsync(new Event { Type = "invoice.paid" }, Invoice(status, amount, outOfBand)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OneTimePayment_RequiresCheckoutSession(bool found)
    {
        var response = found
            ? """{"object":"list","data":[{"id":"cs_test","object":"checkout.session","mode":"payment","metadata":{"checkoutAttemptId":"attempt_test"}}],"has_more":false}"""
            : """{"object":"list","data":[],"has_more":false}""";
        var result = await Resolver(response).ResolveAsync(new Event { Type = "payment_intent.succeeded", Created = DateTime.UtcNow },
            """{"data":{"object":{"id":"pi_test","status":"succeeded","amount_received":5900,"currency":"usd"}}}""");
        Assert.AreEqual(found, result is not null);
        if (found) Assert.AreEqual("attempt_test", result!.CheckoutAttemptId);
    }

    [TestMethod]
    public async Task UnsupportedEvent_IsSkippedWithoutNetwork()
    {
        Assert.IsNull(await Resolver(null!).ResolveAsync(new Event { Type = "customer.created" }, Invoice()));
    }

    [TestMethod]
    public void RawSignature_ValidAcceptedAndTamperedRejectedBeforePersistence()
    {
        const string secret = "test-signing-secret-not-a-production-credential";
        const string body = """{"id":"evt_test","object":"event","type":"invoice.paid","data":{"object":{"id":"in_test","object":"invoice"}}}""";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        var header = $"t={timestamp},v1={hash}";
        Assert.AreEqual("invoice.paid", StripeWebhookVerification.Verify(body, header, secret).Type);
        Assert.ThrowsException<StripeException>(() => StripeWebhookVerification.Verify(body + " ", header, secret));
    }

    private sealed class FakeHttp(string? response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.IsNotNull(response, "This skip path must not contact Stripe.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response!, Encoding.UTF8, "application/json") });
        }
    }
}
