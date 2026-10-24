using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

public sealed class AdsDryRunService(IAdsDeliveryStore store, AdsEligibilityService eligibility, AdsDeliveryOptions options,
    ILogger<AdsDryRunService> logger)
{
    public async Task<Dictionary<string, int>> RunAsync()
    {
        var counts = new Dictionary<string, int>();
        // Canary/Enabled remain inert until a separately approved transport stage. No Google HTTP/OAuth dependency exists.
        if (!options.CanEvaluateDryRun && !(options.Valid && options.Mode == AdsDeliveryMode.DryRun))
        { logger.LogWarning("[ADS DRY RUN] skipped: OPTIONS_NOT_EVALUATABLE"); return counts; }
        logger.LogInformation("[ADS DRY RUN] database={Database}", options.LocalDatabase ? "local" : "aws");
        // Internal eligibility only; future Google transmission must call EvaluateGoogleDeliveryAsync.
        var candidates = await store.CandidatesAsync();
        logger.LogInformation("[ADS DRY RUN] pending sidecars={Count}", candidates.Count);
        foreach (var row in candidates)
        {
            var purchase = await store.PurchaseAsync(row.PurchaseTransactionId);
            var now = DateTimeOffset.UtcNow;
            var result = purchase is null ? new AdsEligibilityResult(AdsDeliveryStatus.NeedsReview, "PURCHASE_UNAVAILABLE")
                : await eligibility.EvaluateCurrentAsync(purchase, row, await store.ClickCapturedAtAsync(purchase), now);
            if (!await store.SaveEvaluationAsync(row, result, now))
            {
                logger.LogWarning("[ADS DRY RUN] update skipped: CONDITIONAL_UPDATE_FAILED");
                result = new(AdsDeliveryStatus.NeedsReview, "CONCURRENT_UPDATE");
            }
            var key = result.Status + ":" + result.Reason;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        foreach (var pair in counts) logger.LogInformation("[ADS DRY RUN] {Result}: {Count}", pair.Key, pair.Value);
        return counts;
    }
}

public sealed class AdsVerifiedProvenance(IAdsDeliveryStore store, AdsDeliveryOptions options, ILogger<AdsVerifiedProvenance> logger,
    IStripeAccountVerifier? accountVerifier = null)
{
    // Called only by the signature-verified handler, only for a newly recorded purchase.
    public async Task CaptureAsync(PurchaseRecord purchase, JsonElement verifiedEvent)
    {
        if (options.Mode == AdsDeliveryMode.Off)
        { logger.LogInformation("[ADS PROVENANCE] skipped: delivery mode off"); return; }
        if (!options.CanCaptureSidecar && !options.Valid)
        {
            var reasons = options.ValidationFailures.Count == 0 ? "INVALID_CONFIGURATION" : string.Join(",", options.ValidationFailures);
            logger.LogWarning("[ADS PROVENANCE] configuration invalid: {Reasons}", reasons);
            return;
        }
        try
        {
            var accountVerified = accountVerifier is null ? null : await accountVerifier.VerifyAsync(verifiedEvent);
            var created = await store.CreateAsync(Build(purchase.TransactionId, verifiedEvent, options, DateTimeOffset.UtcNow) with
            { StripeAccountVerified = accountVerified, StripeAccountVerificationMethod = "stripe-api-event-v1" });
            logger.LogInformation(created ? "[ADS PROVENANCE] sidecar created" : "[ADS PROVENANCE] sidecar already exists");
        }
        catch { logger.LogWarning("[ADS PROVENANCE] capture failed; purchase requires review, GA4 unaffected"); }
    }
    public static AdsDeliveryRecord Build(string transactionId, JsonElement verifiedEvent, AdsDeliveryOptions options, DateTimeOffset now)
    {
        bool? live = verifiedEvent.TryGetProperty("livemode", out var mode) && mode.ValueKind is JsonValueKind.True or JsonValueKind.False ? mode.GetBoolean() : null;
        // Require agreement with the signed payment object when its livemode is present.
        if (verifiedEvent.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var payment)
            && payment.TryGetProperty("livemode", out var paymentMode) && (paymentMode.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || paymentMode.GetBoolean() != live)) live = null;
        return new()
        {
            PurchaseTransactionId = transactionId, StripeLivemode = live, StripeAccountVerified = null,
            AdsEligibilityVersion = options.EligibilityVersion, DeploymentEnvironment = options.DeploymentEnvironment,
            MeasurementEnvironment = options.MeasurementEnvironment, LocalDatabase = options.LocalDatabase,
            ProvenanceVersion = "stripe-verified-v1", ProvenanceCapturedAt = now, CreatedAt = now, UpdatedAt = now,
            DestinationCustomerId = options.CustomerId, DestinationConversionActionId = options.ConversionActionId
        };
    }
}
