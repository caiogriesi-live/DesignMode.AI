using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AdvertisingConsentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static AdsDeliveryOptions Options => new() { Mode = AdsDeliveryMode.DryRun, Valid = true, EligibilityVersion = "v1", ActivationCutoff = Now.AddDays(-2),
        LocalDatabase = false, DeploymentEnvironment = "Production", MeasurementEnvironment = "Production", CustomerId = "1234567890", ConversionActionId = "123", ExpectedStripeAccountId = "acct_expected" };
    private static AdvertisingConsentRecord Granted => new() { Id = "u:user", UserId = "user", SubjectType = "User", AdvertisingConsent = true, Revision = 1 };
    private static PurchaseRecord Purchase => new() { TransactionId = "3f26b544-bec8-42cd-a5d2-55296590a31c", UserId = "user", Amount = 59, AmountMinor = 5900,
        StripeSubscriptionId = "sub_fixture", IsFirstPayment = true, BillingReason = "subscription_create", PaidAt = Now.AddMinutes(-5),
        Snapshot = new() { Gclid = "fixture-click", Measurement = new() { AdvertisingConsent = true, AnalyticsConsent = true, GaClientId = "123.456" },
            AdvertisingConsentEvidence = new("u:user", 1, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion) } };
    private static AdsDeliveryRecord Delivery => new() { PurchaseTransactionId = Purchase.TransactionId, StripeLivemode = true, StripeAccountVerified = true,
        StripeAccountVerificationMethod = "stripe-api-event-v1", DeploymentEnvironment = "Production", MeasurementEnvironment = "Production", LocalDatabase = false,
        ProvenanceVersion = "stripe-verified-v1", ProvenanceCapturedAt = Now.AddMinutes(-4), AdsEligibilityVersion = "v1", DestinationCustomerId = "1234567890", DestinationConversionActionId = "123" };
    private sealed class Reader : IAdvertisingConsentReader
    {
        public AdvertisingConsentRecord? Value = Granted;
        public Task<AdvertisingConsentRecord?> CurrentUserAsync(string userId) => Task.FromResult(Value);
    }
    [TestMethod]
    public async Task FinalConsentCheck_RejectsWithdrawalAndRegrant_WithoutChangingGa4()
    {
        var reader = new Reader(); var service = new AdsEligibilityService(Options, reader);
        var ga4 = JsonSerializer.Serialize(GoogleConversionPayloads.Ga4(Purchase));
        Assert.AreEqual(AdsDeliveryStatus.Eligible, (await service.EvaluateCurrentAsync(Purchase, Delivery, Now.AddHours(-1), Now)).Status);
        reader.Value = Granted with { AdvertisingConsent = false, Revision = 2, LastWithdrawalRevision = 2 };
        Assert.AreEqual("AD_CONSENT_WITHDRAWN", (await service.EvaluateCurrentAsync(Purchase, Delivery, Now.AddHours(-1), Now)).Reason);
        reader.Value = reader.Value with { AdvertisingConsent = true, Revision = 3 };
        Assert.AreEqual("AD_CONSENT_WITHDRAWN", (await service.EvaluateCurrentAsync(Purchase, Delivery, Now.AddHours(-1), Now)).Reason);
        var denied = Purchase with { Snapshot = Purchase.Snapshot with { Measurement = new() } };
        Assert.AreEqual("NO_AD_CONSENT", (await service.EvaluateCurrentAsync(denied, Delivery, Now.AddHours(-1), Now)).Reason);
        Assert.AreEqual(ga4, JsonSerializer.Serialize(GoogleConversionPayloads.Ga4(Purchase)));
        var future = Purchase with { Snapshot = Purchase.Snapshot with { AdvertisingConsentEvidence = new("u:user", 3, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion) } };
        Assert.AreEqual(AdsDeliveryStatus.Eligible, (await service.EvaluateCurrentAsync(future, Delivery, Now.AddHours(-1), Now)).Status);
    }
    [TestMethod]
    public async Task UnknownVersionsAndLegacyProof_AreNotEligible()
    {
        var reader = new Reader { Value = Granted with { PolicyVersion = "unknown" } };
        var service = new AdsEligibilityService(Options, reader);
        Assert.AreEqual(AdsDeliveryStatus.NeedsReview, (await service.EvaluateCurrentAsync(Purchase, Delivery, Now.AddHours(-1), Now)).Status);
        reader.Value = Granted;
        var old = Purchase with { Snapshot = Purchase.Snapshot with { AdvertisingConsentEvidence = null } };
        Assert.AreEqual("CHECKOUT_CONSENT_EVIDENCE_UNAVAILABLE", (await service.EvaluateCurrentAsync(old, Delivery, Now.AddHours(-1), Now)).Reason);
        Assert.AreEqual("STRIPE_ACCOUNT_UNVERIFIED", (await service.EvaluateCurrentAsync(Purchase, Delivery with { StripeAccountVerificationMethod = "" }, Now.AddHours(-1), Now)).Reason);
        Assert.AreEqual("STRIPE_TEST_MODE", (await service.EvaluateCurrentAsync(Purchase, Delivery with { StripeLivemode = false }, Now.AddHours(-1), Now)).Reason);
    }
    [TestMethod]
    [DataRow("correct", true)]
    [DataRow("wrong-account", false)]
    [DataRow("wrong-event", false)]
    [DataRow("wrong-payment", false)]
    [DataRow("test", false)]
    [DataRow("connect", false)]
    public async Task StripeAccountVerification_UsesAuthenticatedAccountAndEvent(string scenario, bool expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "evt_fixture", type = "invoice.paid", livemode = scenario != "test",
            account = scenario == "connect" ? "acct_expected" : null, data = new { @object = new { id = "in_fixture" } } }));
        var http = new StripeHttp(scenario);
        var stripe = new Stripe.StripeClient("sk_test_mock_only", httpClient: new Stripe.SystemNetHttpClient(new HttpClient(http)));
        Assert.AreEqual(expected, await new StripeAccountVerifier(Options, stripe).VerifyAsync(json.RootElement));
        Assert.AreEqual(scenario is "test" or "connect" ? 0 : scenario == "wrong-account" ? 1 : 2, http.Calls);
    }
    [TestMethod]
    public async Task StripeAccountVerification_UnavailableIsUnknown()
    {
        using var json = JsonDocument.Parse("""{"id":"evt_fixture","type":"invoice.paid","livemode":true,"data":{"object":{"id":"in_fixture"}}}""");
        var stripe = new Stripe.StripeClient("sk_test_mock_only", httpClient: new Stripe.SystemNetHttpClient(new HttpClient(new StripeHttp("unavailable"))));
        Assert.IsNull(await new StripeAccountVerifier(Options, stripe).VerifyAsync(json.RootElement));
        Assert.IsNull(await new StripeAccountVerifier(Options with { ExpectedStripeAccountId = "" }, stripe).VerifyAsync(json.RootElement));
    }
    private sealed class StripeHttp(string scenario) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.AreEqual("api.stripe.com", request.RequestUri!.Host);
            Assert.IsFalse(request.Headers.Contains("Stripe-Account"));
            if (scenario == "unavailable") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
            var body = request.RequestUri.AbsolutePath == "/v1/account"
                ? JsonSerializer.Serialize(new { id = scenario == "wrong-account" ? "acct_other" : "acct_expected", @object = "account" })
                : JsonSerializer.Serialize(new { id = scenario == "wrong-event" ? "evt_other" : "evt_fixture", @object = "event", type = "invoice.paid", livemode = true,
                    data = new { @object = new { id = scenario == "wrong-payment" ? "in_other" : "in_fixture", @object = "invoice" } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    [TestMethod]
    public void RetiredConsentEndpoint_IsGoneWithoutStorageAccess()
    {
        var prior = Environment.GetEnvironmentVariable("HostUrl");
        try
        {
            Environment.SetEnvironmentVariable("HostUrl", "https://example.invalid");
            var function = new AdvertisingConsentFunction();
            var request = new DefaultHttpContext().Request; request.Headers.Origin = "https://attacker.invalid";
            Assert.AreEqual(StatusCodes.Status410Gone, ((StatusCodeResult)function.Run(request)).StatusCode);
            request.Headers.Origin = "https://example.invalid"; request.Headers.Authorization = "Bearer invalid";
            Assert.AreEqual(StatusCodes.Status410Gone, ((StatusCodeResult)function.Run(request)).StatusCode);
            request.Headers.Remove("Authorization"); request.ContentType = "application/json";
            request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"action":"open","visitorId":"another-visitor"}"""));
            Assert.AreEqual(StatusCodes.Status410Gone, ((StatusCodeResult)function.Run(request)).StatusCode);
        }
        finally { Environment.SetEnvironmentVariable("HostUrl", prior); }
    }
    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task ConsentEvidence_BindsSafelyAndControlsImmutableCheckoutSnapshots()
    {
        if (Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS") != "true") Assert.Inconclusive("Opt into DynamoDB Local tests.");
        LocalDynamoDbTestSafety.RequireLoopback();
        using var db = DynamoDbClientFactory.CreateClient();
        foreach (var table in new[] { AdvertisingConsentStore.TableName, "Projects", "AttributionTouches", "AttributionSnapshots", "CheckoutAttempts" })
        {
            try { await db.DescribeTableAsync(table); }
            catch (ResourceNotFoundException) { await db.CreateTableAsync(new CreateTableRequest { TableName = table, BillingMode = BillingMode.PAY_PER_REQUEST,
                KeySchema = [new("Id", KeyType.HASH)], AttributeDefinitions = [new("Id", ScalarAttributeType.S)] }); }
        }
        var store = new AdvertisingConsentStore(); var user = Guid.NewGuid().ToString(); var project = Guid.NewGuid().ToString(); var funnel = Guid.NewGuid().ToString();
        var visitor = await store.CreateVisitorAsync(); var other = await store.CreateVisitorAsync();
        var prefixes = new List<string> { visitor.State.Id, other.State.Id, "u:" + user };
        var checkoutRows = new List<AttributionStore.CheckoutCreated>();
        try
        {
            Assert.IsFalse(visitor.State.AdvertisingConsent);
            var command = Guid.NewGuid().ToString();
            var grant = await store.RecordAsync(visitor.Token, null, true, command, 0, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion);
            Assert.IsTrue(grant.AdvertisingConsent); Assert.IsNotNull(grant.GrantedAt);
            Assert.IsTrue((await store.FindAsync(grant.Id + ":e:" + command))!.AdvertisingConsent);
            Assert.AreEqual(grant.Revision, (await store.RecordAsync(visitor.Token, null, true, command, 0, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion)).Revision);
            await Assert.ThrowsExceptionAsync<ConsentConflictException>(() => store.RecordAsync(visitor.Token, null, false, Guid.NewGuid().ToString(), 0, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion));
            var account = await store.ResolveAsync(visitor.Token, user);
            Assert.IsTrue(account.AdvertisingConsent);
            await Assert.ThrowsExceptionAsync<ConsentAccessException>(() => store.ResolveAsync(visitor.Token, "another-user"));
            await Assert.ThrowsExceptionAsync<ConsentAccessException>(() => store.ResolveAsync(visitor.Token, null));
            await Assert.ThrowsExceptionAsync<ConsentAccessException>(() => store.ResolveAsync(new string('0', 64), user));
            Assert.IsFalse((await store.ResolveAsync(other.Token, null)).AdvertisingConsent, "Other visitor is untouched.");
            // Binding a second browser must not copy its default denial over the account's explicit grant.
            Assert.IsTrue((await store.ResolveAsync(other.Token, user)).AdvertisingConsent);
            await db.PutItemAsync("Projects", new() { ["Id"] = new() { S = project }, ["UserId"] = new() { S = user }, ["OriginatingFunnelId"] = new() { S = funnel } });
            var attribution = new AttributionStore(NullLogger<AttributionStore>.Instance, store);
            async Task<AttributionSnapshot> Checkout(bool raw)
            {
                var checkout = (await attribution.CreateCheckoutAttemptAsync(user, project, funnel, "plus", 59, "usd",
                    new MeasurementContext { AdvertisingConsent = false, GoogleAdvertisingConsent = raw, AnalyticsConsent = true, GaClientId = "123.456" }))!;
                checkoutRows.Add(checkout);
                var row = await db.GetItemAsync("AttributionSnapshots", new() { ["Id"] = new() { S = checkout.AttributionSnapshotId } });
                return JsonSerializer.Deserialize<AttributionSnapshot>(row.Item["Data"].S)!;
            }
            var allowed = await Checkout(true); Assert.IsTrue(allowed.Measurement.AdvertisingConsent);
            Assert.IsTrue(allowed.Measurement.GoogleAdvertisingConsent); Assert.IsNull(allowed.AdvertisingConsentEvidence);
            var withdrawalCommand = Guid.NewGuid().ToString();
            var withdrawal = await store.RecordAsync(visitor.Token, user, false, withdrawalCommand, account.Revision, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion);
            Assert.IsFalse(withdrawal.AdvertisingConsent); Assert.IsNotNull(withdrawal.WithdrawnAt);
            var denied = await Checkout(true); Assert.IsTrue(denied.Measurement.AdvertisingConsent, "Internal measurement remains enabled.");
            Assert.IsTrue(denied.Measurement.GoogleAdvertisingConsent, "Prospective policy overrides historical preferences and raw client input.");
            Assert.IsNull(denied.AdvertisingConsentEvidence);
            Assert.IsTrue(denied.Measurement.AnalyticsConsent); Assert.AreEqual("123.456", denied.Measurement.GaClientId);
            var regrant = await store.RecordAsync(visitor.Token, user, true, Guid.NewGuid().ToString(), withdrawal.Revision, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion);
            Assert.AreEqual(withdrawal.LastWithdrawalRevision, regrant.LastWithdrawalRevision);
            Assert.IsTrue((await Checkout(true)).Measurement.AdvertisingConsent);
            var localWithdrawal = await Checkout(false);
            Assert.IsTrue(localWithdrawal.Measurement.AdvertisingConsent);
            Assert.IsTrue(localWithdrawal.Measurement.GoogleAdvertisingConsent);
            var reread = await db.GetItemAsync("AttributionSnapshots", new() { ["Id"] = new() { S = denied.Id } });
            var unchanged = JsonSerializer.Deserialize<AttributionSnapshot>(reread.Item["Data"].S)!;
            Assert.IsTrue(unchanged.Measurement.AdvertisingConsent);
            Assert.IsTrue(unchanged.Measurement.GoogleAdvertisingConsent);
            Assert.IsTrue((await store.RecordAsync(visitor.Token, user, false, withdrawalCommand, account.Revision, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion)).AdvertisingConsent,
                "Idempotent old withdrawal must not replace a subsequent deliberate grant.");
        }
        finally
        {
            // Only this test's randomly identified subjects and their audit evidence, on a guarded loopback database.
            foreach (var prefix in prefixes)
            {
                Dictionary<string, AttributeValue>? cursor = null;
                do
                {
                    var page = await db.ScanAsync(new ScanRequest { TableName = AdvertisingConsentStore.TableName, ExclusiveStartKey = cursor,
                        FilterExpression = "Id = :id OR begins_with(Id, :prefix)", ExpressionAttributeValues = new() { [":id"] = new() { S = prefix }, [":prefix"] = new() { S = prefix + ":e:" } } });
                    foreach (var row in page.Items) await db.DeleteItemAsync(AdvertisingConsentStore.TableName, new() { ["Id"] = row["Id"] });
                    cursor = page.LastEvaluatedKey;
                } while (cursor is { Count: > 0 });
            }
            await db.DeleteItemAsync("Projects", new() { ["Id"] = new() { S = project } });
            foreach (var checkout in checkoutRows)
            {
                await db.DeleteItemAsync("CheckoutAttempts", new() { ["Id"] = new() { S = checkout.CheckoutAttemptId } });
                await db.DeleteItemAsync("AttributionSnapshots", new() { ["Id"] = new() { S = checkout.AttributionSnapshotId } });
            }
        }
    }
}
