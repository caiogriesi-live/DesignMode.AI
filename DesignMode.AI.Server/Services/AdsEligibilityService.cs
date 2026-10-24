using DesignMode.AI.Shared.Models.Attribution;

namespace DesignMode.AI.Server.Services;

public sealed class AdsEligibilityService(AdsDeliveryOptions options, IAdvertisingConsentReader? consentReader = null)
{
    // Internal measurement evaluation only. Eligible is NOT Google transmission permission.
    public async Task<AdsEligibilityResult> EvaluateCurrentAsync(PurchaseRecord purchase, AdsDeliveryRecord? delivery, DateTimeOffset? clickCapturedAt, DateTimeOffset now)
    {
        if (AdvertisingMeasurementPolicy.IsCurrent(purchase.Snapshot.Measurement))
            return Evaluate(purchase, delivery, clickCapturedAt, now);
        AdvertisingConsentRecord? current = null;
        try { if (consentReader is not null) current = await consentReader.CurrentUserAsync(purchase.UserId); }
        catch { return new(AdsDeliveryStatus.NeedsReview, "CURRENT_CONSENT_UNAVAILABLE"); }
        return Evaluate(purchase, delivery, clickCapturedAt, now, current);
    }
    // Mandatory additional gate for any future Google transport. Re-read withdrawals at send time.
    public async Task<AdsEligibilityResult> EvaluateGoogleDeliveryAsync(PurchaseRecord purchase, AdsDeliveryRecord? delivery, DateTimeOffset? clickCapturedAt, DateTimeOffset now)
    {
        var result = await EvaluateCurrentAsync(purchase, delivery, clickCapturedAt, now);
        if (result.Status != AdsDeliveryStatus.Eligible) return result;
        // New policy-versioned records require no fabricated user grant/evidence. Historical records retain old gates.
        if (AdvertisingMeasurementPolicy.IsCurrent(purchase.Snapshot.Measurement)) return result;
        if (!AdvertisingMeasurementPolicy.GoogleConsentGranted(purchase.Snapshot.Measurement))
            return new(AdsDeliveryStatus.NotEligible, "NO_GOOGLE_AD_CONSENT");
        AdvertisingConsentRecord? current = null;
        try { if (consentReader is not null) current = await consentReader.CurrentUserAsync(purchase.UserId); }
        catch { return new(AdsDeliveryStatus.NeedsReview, "CURRENT_CONSENT_UNAVAILABLE"); }
        return CheckConsent(purchase, current, googleDelivery: true) ?? result;
    }
    public AdsEligibilityResult Evaluate(PurchaseRecord purchase, AdsDeliveryRecord? delivery, DateTimeOffset? clickCapturedAt, DateTimeOffset now,
        AdvertisingConsentRecord? currentConsent = null)
    {
        static AdsEligibilityResult Review(string reason) => new(AdsDeliveryStatus.NeedsReview, reason);
        static AdsEligibilityResult No(string reason) => new(AdsDeliveryStatus.NotEligible, reason);
        if (delivery is null) return Review("MISSING_SIDECAR");
        if (!options.Valid) return Review("INVALID_CONFIGURATION");
        if (purchase.Snapshot.Measurement.AdvertisingMeasurementPolicyVersion is not null
            && !AdvertisingMeasurementPolicy.IsCurrent(purchase.Snapshot.Measurement))
            return Review("UNSUPPORTED_MEASUREMENT_POLICY");
        if (!Guid.TryParseExact(purchase.TransactionId, "D", out var id) || id == Guid.Empty
            || delivery.PurchaseTransactionId != purchase.TransactionId) return Review("INTEGRITY_CONFLICT");
        if (delivery.ProvenanceVersion != "stripe-verified-v1" || delivery.StripeLivemode is null
            || delivery.ProvenanceCapturedAt == default || delivery.ProvenanceCapturedAt > now) return Review("UNKNOWN_STRIPE_PROVENANCE");
        if (delivery.StripeLivemode != true) return No("STRIPE_TEST_MODE");
        if (delivery.LocalDatabase || options.LocalDatabase || delivery.DeploymentEnvironment != "Production"
            || options.DeploymentEnvironment != "Production" || delivery.MeasurementEnvironment != "Production"
            || options.MeasurementEnvironment != "Production") return No("NON_PRODUCTION_ENVIRONMENT");
        if (delivery.StripeAccountVerified != true || delivery.StripeAccountVerificationMethod != "stripe-api-event-v1") return Review("STRIPE_ACCOUNT_UNVERIFIED");
        if (delivery.AdsEligibilityVersion != options.EligibilityVersion) return Review("ELIGIBILITY_VERSION_MISMATCH");
        if (delivery.DestinationCustomerId != options.CustomerId || delivery.DestinationConversionActionId != options.ConversionActionId)
            return Review("DESTINATION_MISMATCH");
        if (purchase.IsFirstPayment && purchase.IsRenewal) return Review("INTEGRITY_CONFLICT");
        if (purchase.IsRenewal) return No("RENEWAL");
        if (!purchase.IsFirstPayment) return No("NOT_FIRST_PAYMENT");
        if (purchase.PaymentStatus != "Paid") return No("NOT_PAID");
        if (string.IsNullOrWhiteSpace(purchase.StripeSubscriptionId) || !purchase.BillingReason.StartsWith("subscription_", StringComparison.Ordinal))
            return No("NOT_SUBSCRIPTION");
        if (!purchase.Snapshot.Measurement.AdvertisingConsent) return No("NO_AD_CONSENT");
        // Decimal cannot represent NaN/infinity. Also detect disagreement with canonical minor units.
        if (purchase.Amount <= 0 || purchase.AmountMinor <= 0) return Review("INVALID_AMOUNT");
        if (purchase.Currency != "USD") return No("UNSUPPORTED_CURRENCY");
        if (purchase.Amount != purchase.AmountMinor / 100m) return Review("INTEGRITY_CONFLICT");
        if (purchase.PaidAt == default || purchase.PaidAt > now) return Review("INVALID_TIMESTAMP");
        if (purchase.PaidAt < options.ActivationCutoff || delivery.ProvenanceCapturedAt < options.ActivationCutoff)
            return No("BEFORE_ACTIVATION_CUTOFF");
        if (now - purchase.PaidAt > TimeSpan.FromDays(7)) return new(AdsDeliveryStatus.Expired, "DELIVERY_BUDGET_EXPIRED");
        var candidates = new[] { ("gclid", purchase.Snapshot.Gclid), ("gbraid", purchase.Snapshot.Gbraid), ("wbraid", purchase.Snapshot.Wbraid) };
        // Reject synthetic data anywhere, rather than falling back from a known fake gclid to a braid.
        if (candidates.Any(x => x.Item2 is not null && new[] { "TEST_ONLY_", "REAL_GCLID_PLACEHOLDER", "PURCHASE_TRANSACTION_PLACEHOLDER" }
            .Any(marker => x.Item2.Contains(marker, StringComparison.OrdinalIgnoreCase)))) return No("SYNTHETIC_CLICK_ID");
        var click = candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Item2));
        if (click.Item2 is null) return No("NO_CLICK_ID");
        if (click.Item2.Length > 512 || click.Item2.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            return No("UNUSABLE_CLICK_ID");
        if (clickCapturedAt is null || clickCapturedAt == default(DateTimeOffset) || clickCapturedAt > purchase.PaidAt)
            return Review("ATTRIBUTION_TIMESTAMP_UNAVAILABLE");
        if (purchase.PaidAt - clickCapturedAt.Value > TimeSpan.FromDays(30) || now - clickCapturedAt.Value > TimeSpan.FromDays(30))
            return new(AdsDeliveryStatus.Expired, "ATTRIBUTION_EXPIRED");
        // Preserve the original consent gates for unversioned historical snapshots.
        if (!AdvertisingMeasurementPolicy.IsCurrent(purchase.Snapshot.Measurement))
        {
            var consentFailure = CheckConsent(purchase, currentConsent);
            if (consentFailure is not null) return consentFailure;
        }
        return new(AdsDeliveryStatus.Eligible, "ELIGIBLE", click.Item1);
    }

    private static AdsEligibilityResult? CheckConsent(PurchaseRecord purchase, AdvertisingConsentRecord? currentConsent, bool googleDelivery = false)
    {
        static AdsEligibilityResult Review(string reason) => new(AdsDeliveryStatus.NeedsReview, reason);
        var evidence = purchase.Snapshot.AdvertisingConsentEvidence;
        if (evidence is null || evidence.SubjectId != "u:" + purchase.UserId || evidence.Revision < 1)
            return Review("CHECKOUT_CONSENT_EVIDENCE_UNAVAILABLE");
        var legacy = !googleDelivery && purchase.Snapshot.Measurement.AdvertisingMeasurementPolicyVersion is null
            && evidence.PolicyVersion == AdvertisingConsentPolicy.LegacyPolicyVersion
            && currentConsent?.PolicyVersion == AdvertisingConsentPolicy.LegacyPolicyVersion
            && currentConsent.ConsentVersion == AdvertisingConsentPolicy.SchemaVersion;
        if (evidence.ConsentVersion != AdvertisingConsentPolicy.SchemaVersion || (!legacy &&
            (evidence.PolicyVersion != AdvertisingConsentPolicy.PolicyVersion || !AdvertisingConsentPolicy.Supported(currentConsent))))
            return Review("UNSUPPORTED_CONSENT_VERSION");
        if (currentConsent!.Id != evidence.SubjectId || currentConsent.UserId != purchase.UserId || currentConsent.Revision < evidence.Revision)
            return Review("CURRENT_CONSENT_UNAVAILABLE");
        // Monotonic revision is stronger than timestamp comparison for same-clock-tick withdrawals/re-grants.
        if (!currentConsent.AdvertisingConsent || currentConsent.LastWithdrawalRevision > evidence.Revision)
            return new(AdsDeliveryStatus.NotEligible, "AD_CONSENT_WITHDRAWN");
        return null;
    }
}
