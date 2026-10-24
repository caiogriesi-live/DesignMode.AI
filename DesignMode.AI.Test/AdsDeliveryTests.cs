using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AdsDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static AdsDeliveryOptions Options => new()
    {
        Valid = true, Mode = AdsDeliveryMode.DryRun, EligibilityVersion = "stage1-v1", ActivationCutoff = Now.AddDays(-3),
        CustomerId = "1234567890", ConversionActionId = "123", DeploymentEnvironment = "Production",
        MeasurementEnvironment = "Production", LocalDatabase = false, ExpectedStripeAccountId = "acct_fixture"
    };
    private static PurchaseRecord Purchase => new()
    {
        UserId = "fixture-user",
        TransactionId = "cb997f47-f88e-45b4-9ef3-c79e64a101d6", Amount = 59m, AmountMinor = 5900, Currency = "USD",
        StripeSubscriptionId = "sub_fixture", IsFirstPayment = true, PaidAt = Now.AddHours(-1), BillingReason = "subscription_create",
        Snapshot = new() { Gclid = "synthetic-unit-fixture", Measurement = new() { AdvertisingConsent = true, AnalyticsConsent = true, GaClientId = "123.456" },
            AdvertisingConsentEvidence = new("u:fixture-user", 1, AdvertisingConsentPolicy.SchemaVersion, AdvertisingConsentPolicy.PolicyVersion) }
    };
    private static AdvertisingConsentRecord Consent => new() { Id = "u:fixture-user", UserId = "fixture-user", AdvertisingConsent = true, Revision = 1 };
    private static AdsDeliveryRecord Row => new()
    {
        PurchaseTransactionId = Purchase.TransactionId, StripeLivemode = true, StripeAccountVerified = true,
        StripeAccountVerificationMethod = "stripe-api-event-v1",
        ProvenanceVersion = "stripe-verified-v1", ProvenanceCapturedAt = Now.AddMinutes(-30),
        DeploymentEnvironment = "Production", MeasurementEnvironment = "Production", LocalDatabase = false,
        AdsEligibilityVersion = Options.EligibilityVersion, DestinationCustomerId = Options.CustomerId, DestinationConversionActionId = Options.ConversionActionId
    };
    [TestMethod]
    [DataRow("gclid")]
    [DataRow("gbraid")]
    [DataRow("wbraid")]
    public void FirstLiveSubscription_ResolvesOnePrioritizedClick(string type)
    {
        var p = Purchase with { Snapshot = Purchase.Snapshot with { Gclid = type == "gclid" ? "fixture" : null,
            Gbraid = type is "gclid" or "gbraid" ? "fixture" : null, Wbraid = "fixture" } };
        var result = new AdsEligibilityService(Options).Evaluate(p, Row, Now.AddDays(-1), Now, Consent);
        Assert.AreEqual(AdsDeliveryStatus.Eligible, result.Status); Assert.AreEqual(type, result.ClickType);
    }
    [TestMethod]
    [DataRow("test", "NotEligible", "STRIPE_TEST_MODE")]
    [DataRow("missing", "NeedsReview", "UNKNOWN_STRIPE_PROVENANCE")]
    [DataRow("historical", "NeedsReview", "MISSING_SIDECAR")]
    [DataRow("consent", "NotEligible", "NO_AD_CONSENT")]
    [DataRow("renewal", "NotEligible", "RENEWAL")]
    [DataRow("notfirst", "NotEligible", "NOT_FIRST_PAYMENT")]
    [DataRow("conflict", "NeedsReview", "INTEGRITY_CONFLICT")]
    [DataRow("noclick", "NotEligible", "NO_CLICK_ID")]
    [DataRow("fake", "NotEligible", "SYNTHETIC_CLICK_ID")]
    [DataRow("placeholder", "NotEligible", "SYNTHETIC_CLICK_ID")]
    [DataRow("zero", "NeedsReview", "INVALID_AMOUNT")]
    [DataRow("negative", "NeedsReview", "INVALID_AMOUNT")]
    [DataRow("currency", "NotEligible", "UNSUPPORTED_CURRENCY")]
    [DataRow("future", "NeedsReview", "INVALID_TIMESTAMP")]
    [DataRow("cutoff", "NotEligible", "BEFORE_ACTIVATION_CUTOFF")]
    [DataRow("oldclick", "Expired", "ATTRIBUTION_EXPIRED")]
    [DataRow("oldpayment", "Expired", "DELIVERY_BUDGET_EXPIRED")]
    [DataRow("local", "NotEligible", "NON_PRODUCTION_ENVIRONMENT")]
    [DataRow("measurement", "NotEligible", "NON_PRODUCTION_ENVIRONMENT")]
    [DataRow("database", "NotEligible", "NON_PRODUCTION_ENVIRONMENT")]
    [DataRow("host", "NotEligible", "NON_PRODUCTION_ENVIRONMENT")]
    [DataRow("version", "NeedsReview", "ELIGIBILITY_VERSION_MISMATCH")]
    [DataRow("destination", "NeedsReview", "DESTINATION_MISMATCH")]
    [DataRow("account", "NeedsReview", "STRIPE_ACCOUNT_UNVERIFIED")]
    [DataRow("onetime", "NotEligible", "NOT_SUBSCRIPTION")]
    [DataRow("unpaid", "NotEligible", "NOT_PAID")]
    [DataRow("transaction", "NeedsReview", "INTEGRITY_CONFLICT")]
    [DataRow("notime", "NeedsReview", "ATTRIBUTION_TIMESTAMP_UNAVAILABLE")]
    public void IndependentSafetyGates_FailClosed(string scenario, string status, string reason)
    {
        var p = Purchase; AdsDeliveryRecord? r = Row; var o = Options; DateTimeOffset? click = Now.AddDays(-1);
        switch (scenario)
        {
            case "test": r = r with { StripeLivemode = false }; break;
            case "missing": r = r with { StripeLivemode = null }; break;
            case "historical": r = null; break;
            case "consent": p = p with { Snapshot = p.Snapshot with { Measurement = new() } }; break;
            case "renewal": p = p with { IsFirstPayment = false, IsRenewal = true }; break;
            case "notfirst": p = p with { IsFirstPayment = false }; break;
            case "conflict": p = p with { IsRenewal = true }; break;
            case "noclick": p = p with { Snapshot = p.Snapshot with { Gclid = null } }; break;
            case "fake": p = p with { Snapshot = p.Snapshot with { Gclid = "TEST_ONLY_NOT_A_REAL_GOOGLE_CLICK" } }; break;
            case "placeholder": p = p with { Snapshot = p.Snapshot with { Gclid = "REAL_GCLID_PLACEHOLDER" } }; break;
            case "zero": p = p with { Amount = 0 }; break;
            case "negative": p = p with { Amount = -1 }; break;
            case "currency": p = p with { Currency = "EUR" }; break;
            case "future": p = p with { PaidAt = Now.AddSeconds(1) }; break;
            case "cutoff": p = p with { PaidAt = Now.AddDays(-4) }; break;
            case "oldclick": click = Now.AddDays(-31); break;
            case "oldpayment": p = p with { PaidAt = Now.AddDays(-8) }; o = o with { ActivationCutoff = Now.AddDays(-20) }; break;
            case "local": r = r with { DeploymentEnvironment = "Development" }; break;
            case "measurement": r = r with { MeasurementEnvironment = "Test" }; break;
            case "database": r = r with { LocalDatabase = true }; break;
            case "host": o = o with { DeploymentEnvironment = "Development" }; break;
            case "version": r = r with { AdsEligibilityVersion = "old" }; break;
            case "destination": r = r with { DestinationCustomerId = "9999999999" }; break;
            case "account": r = r with { StripeAccountVerified = null }; break;
            case "onetime": p = p with { StripeSubscriptionId = null }; break;
            case "unpaid": p = p with { PaymentStatus = "Pending" }; break;
            case "transaction": p = p with { TransactionId = "PURCHASE_TRANSACTION_PLACEHOLDER" }; break;
            case "notime": click = null; break;
        }
        var result = new AdsEligibilityService(o).Evaluate(p, r, click, Now, Consent);
        Assert.AreEqual(status, result.Status.ToString()); Assert.AreEqual(reason, result.Reason);
        Assert.IsNull(result.ClickType);
        // Run the same complete independent-gate matrix under the prospective internal policy.
        p.Snapshot.Measurement.AdvertisingMeasurementPolicyVersion = DesignMode.AI.Shared.Models.Attribution.AdvertisingMeasurementPolicy.Version;
        var prospective = new AdsEligibilityService(o).Evaluate(p, r, click, Now);
        Assert.AreEqual(status, prospective.Status.ToString()); Assert.AreEqual(reason, prospective.Reason);
        Assert.IsNull(prospective.ClickType);
    }
    [TestMethod]
    public void Configuration_DefaultOff_LegacyFlagIgnored_InvalidCutoffFailsClosed()
    {
        var env = new Dictionary<string, string> { ["GOOGLE_ADS_UPLOAD_ENABLED"] = "true" };
        var o = AdsDeliveryOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.AreEqual(AdsDeliveryMode.Off, o.Mode); Assert.IsFalse(o.Valid);
        env["ADS_DELIVERY_MODE"] = "dry-run"; env["ADS_ELIGIBILITY_VERSION"] = "v1";
        env["ADS_DESTINATION_CUSTOMER_ID"] = "1234567890"; env["ADS_DESTINATION_CONVERSION_ACTION_ID"] = "123";
        env["ADS_ACTIVATION_CUTOFF"] = "2026-08-31T00:00:00";
        Assert.IsFalse(AdsDeliveryOptions.FromEnvironment(k => env.GetValueOrDefault(k)).Valid);
        env["ADS_ACTIVATION_CUTOFF"] += "Z";
        Assert.IsTrue(AdsDeliveryOptions.FromEnvironment(k => env.GetValueOrDefault(k)).Valid);
        env["ADS_DELIVERY_MODE"] = "typo";
        Assert.IsFalse(AdsDeliveryOptions.FromEnvironment(k => env.GetValueOrDefault(k)).Valid);
    }

    [TestMethod]
    [DataRow("2026-09-01T00:00:00Z")]
    [DataRow("2026-09-01T00:00:00+00:00")]
    [DataRow("2026-09-01T04:00:00+04:00")]
    public void ActivationCutoff_TimezoneQualifiedIso8601_IsAcceptedAndNormalizedToUtc(string value)
    {
        Assert.IsTrue(AdsDeliveryOptions.TryParseActivationCutoff(value, out var parsed));
        Assert.AreEqual(TimeSpan.Zero, parsed.Offset);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), parsed);
    }

    [TestMethod]
    [DataRow("2026-09-01T00:00:00")]
    [DataRow("garbage")]
    [DataRow("")]
    [DataRow(null)]
    public void ActivationCutoff_MissingTimezoneOrMalformed_IsRejected(string? value) =>
        Assert.IsFalse(AdsDeliveryOptions.TryParseActivationCutoff(value, out _));

    [TestMethod]
    public void LocalSettingsValuesPath_ResolvesRuntimeSingletonThroughProgramRegistration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Values:ADS_DELIVERY_MODE"] = "dry-run",
            ["Values:ADS_ELIGIBILITY_VERSION"] = "local-sidecar-v1",
            ["Values:ADS_ACTIVATION_CUTOFF"] = "2026-09-01T00:00:00Z",
            ["Values:ADS_MEASUREMENT_ENVIRONMENT"] = "Local",
            ["Values:AZURE_FUNCTIONS_ENVIRONMENT"] = "Development",
            ["Values:USE_LOCAL_DYNAMODB"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddAdsDeliveryOptions(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AdsDeliveryOptions>();
        Assert.AreSame(options, provider.GetRequiredService<AdsDeliveryOptions>());
        Assert.IsTrue(options.CanCaptureSidecar);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), options.ActivationCutoff);
        Assert.AreEqual("configuration", options.ConfigurationSource);
        Assert.IsFalse(options.ValidationFailures.Contains("INVALID_ACTIVATION_CUTOFF"));
    }

    [TestMethod]
    public void FunctionsLocalSettingsLoader_UsesActualValuesShapeAndRuntimeRegistration()
    {
        const string json = """
        { "Values": {
          "ADS_DELIVERY_MODE": "dry-run",
          "ADS_ELIGIBILITY_VERSION": "local-sidecar-v1",
          "ADS_ACTIVATION_CUTOFF": "2026-09-01T00:00:00Z",
          "ADS_MEASUREMENT_ENVIRONMENT": "Local",
          "USE_LOCAL_DYNAMODB": "true",
          "MEASUREMENT_LEDGER_ENABLED": "true"
        } }
        """;
        var configuration = new ConfigurationBuilder()
            .AddFunctionsLocalSettingsValues(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var services = new ServiceCollection();
        services.AddAdsDeliveryOptions(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AdsDeliveryOptions>();
        Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), options.ActivationCutoff);
        Assert.IsTrue(options.CanCaptureSidecar);
        Assert.IsTrue(options.CanEvaluateDryRun);
        Assert.IsFalse(options.CanDeliverToGoogle);
        Assert.IsTrue(options.MeasurementLedgerEnabled);
        Assert.IsFalse(options.ValidationFailures.Contains("INVALID_ACTIVATION_CUTOFF"));
    }

    [TestMethod]
    public void LocalFunctionsValuesCutoff_WinsOverWorkerEnvironmentThatLostTimezoneSuffix()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Values:ADS_DELIVERY_MODE"] = "dry-run",
            ["Values:ADS_ELIGIBILITY_VERSION"] = "local-sidecar-v1",
            ["Values:ADS_ACTIVATION_CUTOFF"] = "2026-09-01T00:00:00Z",
            ["Values:ADS_MEASUREMENT_ENVIRONMENT"] = "Local",
            ["Values:USE_LOCAL_DYNAMODB"] = "true"
        }).Build();
        string? WorkerEnvironment(string key) => key == "ADS_ACTIVATION_CUTOFF" ? "2026-09-01T00:00:00" : null;
        var options = AdsDeliveryOptions.FromConfiguration(configuration, WorkerEnvironment);
        Assert.AreEqual("configuration", options.ConfigurationSource);
        Assert.AreEqual(20, options.CutoffLength);
        Assert.IsTrue(options.CutoffParseable);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), options.ActivationCutoff);
        Assert.IsTrue(options.CanCaptureSidecar);
    }
    [TestMethod]
    public async Task LocalDryRun_CapturesSidecarWithoutGoogleDestination_AndCannotBecomeSendable()
    {
        var env = new Dictionary<string, string>
        {
            ["ADS_DELIVERY_MODE"] = "dry-run", ["ADS_ELIGIBILITY_VERSION"] = "local-sidecar-v1",
            ["ADS_ACTIVATION_CUTOFF"] = "2026-09-01T00:00:00Z", ["USE_LOCAL_DYNAMODB"] = "true",
            ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development", ["ADS_MEASUREMENT_ENVIRONMENT"] = "Local"
        };
        var options = AdsDeliveryOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.AreEqual(AdsDeliveryMode.DryRun, options.Mode);
        Assert.AreEqual("local-sidecar-v1", options.EligibilityVersion);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-01T00:00:00Z"), options.ActivationCutoff);
        Assert.AreEqual("Local", options.MeasurementEnvironment);
        Assert.IsTrue(options.LocalDatabase);
        Assert.IsTrue(options.Valid); Assert.IsTrue(options.CanCaptureSidecar); Assert.IsTrue(options.CanEvaluateDryRun);
        Assert.IsFalse(options.CanDeliverToGoogle); Assert.AreEqual(0, options.ValidationFailures.Count);
        Assert.AreEqual("", options.CustomerId); Assert.AreEqual("", options.ConversionActionId);
        var store = new MemoryStore();
        var capture = new AdsVerifiedProvenance(store, options, NullLogger<AdsVerifiedProvenance>.Instance);
        using var json = JsonDocument.Parse("""{"livemode":false,"data":{"object":{"livemode":false}}}""");
        await capture.CaptureAsync(Purchase, json.RootElement);
        Assert.AreEqual(1, store.Created.Count);
        var sidecar = store.Created[0];
        Assert.AreEqual(false, sidecar.StripeLivemode); Assert.IsTrue(sidecar.LocalDatabase);
        var diagnosticNow = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.AreEqual("STRIPE_TEST_MODE", new AdsEligibilityService(options)
            .Evaluate(Purchase with { PaidAt = diagnosticNow.AddMinutes(-2) }, sidecar, diagnosticNow.AddHours(-2), diagnosticNow).Reason);
        Assert.AreEqual("NON_PRODUCTION_ENVIRONMENT", new AdsEligibilityService(options)
            .Evaluate(Purchase with { PaidAt = diagnosticNow.AddMinutes(-2) }, sidecar with { StripeLivemode = true, StripeAccountVerified = true }, diagnosticNow.AddHours(-2), diagnosticNow).Reason);
        Assert.AreEqual("SYNTHETIC_CLICK_ID", new AdsEligibilityService(Options)
            .Evaluate(Purchase with { Snapshot = Purchase.Snapshot with { Gclid = "TEST_ONLY_fake" } }, Row, Now.AddHours(-2), Now).Reason);
        env["ADS_DELIVERY_MODE"] = "canary";
        Assert.IsFalse(AdsDeliveryOptions.FromEnvironment(k => env.GetValueOrDefault(k)).Valid);
    }
    [TestMethod]
    public void SignedProvenance_DoesNotTrustMetadata_OrInventAccountVerification()
    {
        using var json = JsonDocument.Parse("""{"livemode":false,"data":{"object":{"metadata":{"livemode":true}}}}""");
        var r = AdsVerifiedProvenance.Build(Purchase.TransactionId, json.RootElement, Options, Now);
        Assert.AreEqual(false, r.StripeLivemode); Assert.IsNull(r.StripeAccountVerified);
        using var live = JsonDocument.Parse("""{"livemode":true,"account":"acct_fixture","data":{"object":{"livemode":true}}}""");
        r = AdsVerifiedProvenance.Build(Purchase.TransactionId, live.RootElement, Options, Now);
        Assert.AreEqual(true, r.StripeLivemode); Assert.IsNull(r.StripeAccountVerified, "Event account fields alone no longer substitute for API verification.");
        using var conflict = JsonDocument.Parse("""{"livemode":true,"data":{"object":{"livemode":false}}}""");
        Assert.IsNull(AdsVerifiedProvenance.Build(Purchase.TransactionId, conflict.RootElement, Options, Now).StripeLivemode);
        var ga4Before = JsonSerializer.Serialize(GoogleConversionPayloads.Ga4(Purchase));
        _ = new AdsEligibilityService(Options).Evaluate(Purchase, r with { StripeLivemode = false }, Now.AddDays(-1), Now);
        Assert.AreEqual(ga4Before, JsonSerializer.Serialize(GoogleConversionPayloads.Ga4(Purchase)));
        foreach (var forbidden in new[] { "acct_fixture", "sub_fixture", "synthetic-unit-fixture", "123.456" })
            Assert.IsFalse(JsonSerializer.Serialize(r).Contains(forbidden));
    }
    [TestMethod]
    [DataRow(AdsDeliveryMode.Off)]
    [DataRow(AdsDeliveryMode.DryRun)]
    [DataRow(AdsDeliveryMode.Canary)]
    [DataRow(AdsDeliveryMode.Enabled)]
    public async Task Stage1Modes_NeverSend_OnlyDryRunEvaluates(AdsDeliveryMode mode)
    {
        var o = Options with { Mode = mode, ActivationCutoff = DateTimeOffset.UtcNow.AddDays(-3) };
        var store = new MemoryStore();
        var service = new AdsDryRunService(store, new AdsEligibilityService(o, new ConsentReader()), o, NullLogger<AdsDryRunService>.Instance);
        var results = await service.RunAsync();
        Assert.AreEqual(mode == AdsDeliveryMode.DryRun ? 1 : 0, store.Scans);
        Assert.AreEqual(mode == AdsDeliveryMode.DryRun ? 1 : 0, results.Count);
        if (mode == AdsDeliveryMode.DryRun) Assert.AreEqual(1, results["Eligible:ELIGIBLE"]);
        Assert.IsFalse(store.Saved.Any(x => x.Status is AdsDeliveryStatus.Accepted or AdsDeliveryStatus.Submitted));
        // No transport or token provider can be supplied to the Stage 1 service.
        Assert.IsFalse(typeof(AdsDryRunService).GetConstructors().SelectMany(x => x.GetParameters()).Any(x => x.ParameterType == typeof(IGoogleConversionTransport)));
    }
    [TestMethod]
    public void ConditionalWrites_PinRevisionAndStatus_AndTerminalStatesCannotRetry()
    {
        var request = AdsDeliveryStore.ConditionalReplacement(Row with { Revision = 7 }, Row);
        Assert.AreEqual("Revision = :revision AND AdsStatus = :status", request.ConditionExpression);
        Assert.AreEqual("7", request.ExpressionAttributeValues[":revision"].N);
        Assert.AreEqual("8", request.Item["Revision"].N);
        foreach (var terminal in new[] { AdsDeliveryStatus.Submitted, AdsDeliveryStatus.NotEligible, AdsDeliveryStatus.Expired, AdsDeliveryStatus.Rejected, AdsDeliveryStatus.NeedsReview })
            Assert.IsFalse(AdsDeliveryStore.CanTransition(terminal, AdsDeliveryStatus.Eligible));
        Assert.IsFalse(AdsDeliveryStore.CanTransition(AdsDeliveryStatus.Eligible, AdsDeliveryStatus.Submitted));
        Assert.IsTrue(AdsDeliveryStore.CanTransition(AdsDeliveryStatus.Accepted, AdsDeliveryStatus.Submitted));
    }
    private sealed class MemoryStore : IAdsDeliveryStore
    {
        public int Scans;
        public List<AdsEligibilityResult> Saved = [];
        public List<AdsDeliveryRecord> Created = [];
        public Task<bool> CreateAsync(AdsDeliveryRecord record) { Created.Add(record); return Task.FromResult(true); }
        public Task<List<AdsDeliveryRecord>> CandidatesAsync() { Scans++; return Task.FromResult(new List<AdsDeliveryRecord> { Row with { ProvenanceCapturedAt = DateTimeOffset.UtcNow.AddMinutes(-30) } }); }
        public Task<PurchaseRecord?> PurchaseAsync(string id) => Task.FromResult<PurchaseRecord?>(Purchase with { PaidAt = DateTimeOffset.UtcNow.AddHours(-1) });
        public Task<DateTimeOffset?> ClickCapturedAtAsync(PurchaseRecord purchase) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddDays(-1));
        public Task<bool> SaveEvaluationAsync(AdsDeliveryRecord previous, AdsEligibilityResult result, DateTimeOffset now) { Saved.Add(result); return Task.FromResult(true); }
    }
    private sealed class ConsentReader : IAdvertisingConsentReader
    {
        public Task<AdvertisingConsentRecord?> CurrentUserAsync(string userId) => Task.FromResult<AdvertisingConsentRecord?>(Consent);
    }
    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task Sidecar_ConditionalCreateLeaseAndFencing_RejectStaleWorkers()
    {
        if (Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS") != "true") Assert.Inconclusive("Opt into DynamoDB Local tests.");
        LocalDynamoDbTestSafety.RequireLoopback();
        var clients = new AttributionStore(NullLogger<AttributionStore>.Instance);
        using var db = clients.CreateClient();
        try { await db.DescribeTableAsync(AdsDeliveryStore.TableName); }
        catch (ResourceNotFoundException) { await db.CreateTableAsync(new CreateTableRequest { TableName = AdsDeliveryStore.TableName, BillingMode = Amazon.DynamoDBv2.BillingMode.PAY_PER_REQUEST,
            KeySchema = [new("Id", Amazon.DynamoDBv2.KeyType.HASH)], AttributeDefinitions = [new("Id", Amazon.DynamoDBv2.ScalarAttributeType.S)] }); }
        var store = new AdsDeliveryStore(clients, new PurchaseMeasurementStore(clients));
        var row = Row with { PurchaseTransactionId = Guid.NewGuid().ToString() };
        try
        {
            Assert.IsTrue(await store.CreateAsync(row)); Assert.IsFalse(await store.CreateAsync(row with { StripeLivemode = false }));
            Assert.IsTrue(await store.SaveEvaluationAsync(row, new(AdsDeliveryStatus.Eligible, "ELIGIBLE"), Now));
            Assert.IsFalse(await store.SaveEvaluationAsync(row, new(AdsDeliveryStatus.NotEligible, "STALE"), Now));
            var owner = Guid.NewGuid().ToString();
            var lease = await store.AcquireAsync(row.PurchaseTransactionId, owner, Now); Assert.IsNotNull(lease);
            Assert.IsNull(await store.AcquireAsync(row.PurchaseTransactionId, Guid.NewGuid().ToString(), Now));
            var newerOwner = Guid.NewGuid().ToString();
            var newer = await store.AcquireAsync(row.PurchaseTransactionId, newerOwner, Now.AddMinutes(6)); Assert.IsNotNull(newer);
            // Stale worker even with a stale clock cannot overwrite the newer revision.
            Assert.IsFalse(await store.CompleteLeaseAsync(lease, owner, AdsDeliveryStatus.Rejected, Now.AddMinutes(1)));
            Assert.IsFalse(await store.CompleteLeaseAsync(newer, newerOwner, AdsDeliveryStatus.Accepted, Now.AddMinutes(7)));
            Assert.IsTrue(await store.CompleteLeaseAsync(newer, newerOwner, AdsDeliveryStatus.NeedsReview, Now.AddMinutes(7)));
        }
        finally { await db.DeleteItemAsync(AdsDeliveryStore.TableName, new Dictionary<string, AttributeValue> { ["Id"] = new() { S = row.PurchaseTransactionId } }); }
    }
}
