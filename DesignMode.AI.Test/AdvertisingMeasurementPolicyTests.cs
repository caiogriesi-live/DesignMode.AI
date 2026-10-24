using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AdvertisingMeasurementPolicyTests
{
    [TestMethod]
    public void NewContext_IsAuthoritative_WithoutMutatingOldContextOrGoogleIds()
    {
        var old = JsonSerializer.Deserialize<MeasurementContext>("""{"AdvertisingConsent":false,"AnalyticsConsent":true,"GaClientId":"123.456","GaSessionId":"123"}""")!;
        var before = JsonSerializer.Serialize(old);
        foreach (var input in new MeasurementContext?[] { null, old, new() { AdvertisingConsent = true } })
        {
            var created = AttributionStore.CreateMeasurementContext(input);
            Assert.IsTrue(created.AdvertisingConsent);
            Assert.AreEqual(AdvertisingMeasurementPolicy.Version, created.AdvertisingMeasurementPolicyVersion);
            Assert.IsTrue(created.GoogleAdvertisingConsent);
            Assert.IsTrue(AdvertisingMeasurementPolicy.GoogleConsentGranted(created));
        }
        var result = AttributionStore.CreateMeasurementContext(old);
        Assert.AreEqual("123.456", result.GaClientId); Assert.AreEqual("123", result.GaSessionId);
        Assert.AreEqual(before, JsonSerializer.Serialize(old)); Assert.IsNull(old.AdvertisingMeasurementPolicyVersion);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static PurchaseRecord Purchase => new()
    {
        TransactionId = "3f26b544-bec8-42cd-a5d2-55296590a31c", UserId = "user", Amount = 59, AmountMinor = 5900,
        StripeSubscriptionId = "sub_fixture", IsFirstPayment = true, BillingReason = "subscription_create", PaidAt = Now.AddMinutes(-5),
        Snapshot = new() { Gclid = "fixture-click", Measurement = AttributionStore.CreateMeasurementContext(
            new() { AnalyticsConsent = true, GaClientId = "123.456", GaSessionId = "123", GoogleAdvertisingConsent = false }) }
    };
    private static AdsDeliveryOptions Options => new() { Valid = true, EligibilityVersion = "test", ActivationCutoff = Now.AddDays(-2),
        LocalDatabase = false, DeploymentEnvironment = "Production", MeasurementEnvironment = "Production", CustomerId = "1234567890", ConversionActionId = "123" };
    private static AdsDeliveryRecord Delivery => new() { PurchaseTransactionId = Purchase.TransactionId, StripeLivemode = true, StripeAccountVerified = true,
        StripeAccountVerificationMethod = "stripe-api-event-v1", DeploymentEnvironment = "Production", MeasurementEnvironment = "Production", LocalDatabase = false,
        ProvenanceVersion = "stripe-verified-v1", ProvenanceCapturedAt = Now.AddMinutes(-4), AdsEligibilityVersion = "test", DestinationCustomerId = "1234567890", DestinationConversionActionId = "123" };
    [TestMethod]
    public async Task NewPolicy_DoesNotRequireFabricatedUserConsentEvidence()
    {
        var service = new AdsEligibilityService(Options); var p = Purchase;
        Assert.AreEqual(AdsDeliveryStatus.Eligible, (await service.EvaluateCurrentAsync(p, Delivery, Now.AddHours(-1), Now)).Status);
        Assert.AreEqual(AdsDeliveryStatus.Eligible, (await service.EvaluateGoogleDeliveryAsync(p, Delivery, Now.AddHours(-1), Now)).Status);
        Assert.IsNull(p.Snapshot.AdvertisingConsentEvidence);
    }
    [TestMethod]
    public void NewPolicy_PreservesIndependentSafetyGatesAndGa4PurchaseFacts()
    {
        var p = Purchase; var service = new AdsEligibilityService(Options);
        Assert.AreEqual("STRIPE_TEST_MODE", service.Evaluate(p, Delivery with { StripeLivemode = false }, Now.AddHours(-1), Now).Reason);
        Assert.AreEqual("NON_PRODUCTION_ENVIRONMENT", service.Evaluate(p, Delivery with { LocalDatabase = true }, Now.AddHours(-1), Now).Reason);
        Assert.AreEqual("SYNTHETIC_CLICK_ID", service.Evaluate(p with { Snapshot = p.Snapshot with { Gclid = "TEST_ONLY_fixture" } }, Delivery, Now.AddHours(-1), Now).Reason);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(GoogleConversionPayloads.Ga4(p)));
        Assert.AreEqual("GRANTED", payload.RootElement.GetProperty("consent").GetProperty("ad_user_data").GetString());
        Assert.AreEqual("DENIED", payload.RootElement.GetProperty("consent").GetProperty("ad_personalization").GetString());
        var parameters = payload.RootElement.GetProperty("events")[0].GetProperty("params");
        Assert.AreEqual(p.TransactionId, parameters.GetProperty("transaction_id").GetString());
        Assert.AreEqual(59m, parameters.GetProperty("value").GetDecimal());
        Assert.AreEqual("USD", parameters.GetProperty("currency").GetString());
        Assert.IsFalse(new GoogleMeasurementOptions().AdsConfigured, "Legacy uploader remains disabled without its explicit flag and credentials.");
    }
    [TestMethod]
    public void OldPolicyGrant_IsNotConsentToNewGooglePurpose()
    {
        Assert.IsFalse(AdvertisingConsentPolicy.Supported(new() { AdvertisingConsent = true, PolicyVersion = AdvertisingConsentPolicy.LegacyPolicyVersion }));
    }
}
