using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

public interface IStripeVerifiedPaymentResolver
{
    Task<VerifiedPayment?> ResolveAsync(Event stripeEvent, string verifiedJson);
}

// Invoked ONLY after EventUtility.ConstructEvent has verified the Stripe signature.
public sealed class StripeVerifiedPurchaseHandler(PurchaseMeasurementStore store, IStripeVerifiedPaymentResolver resolver, ILogger<StripeVerifiedPurchaseHandler>? logger = null,
    AdsVerifiedProvenance? adsProvenance = null)
{
    public async Task HandleVerifiedAsync(Event stripeEvent, string verifiedJson)
    {
        logger?.LogInformation("[STRIPE PURCHASE] handler invoked");
        if (!string.Equals(Environment.GetEnvironmentVariable("MEASUREMENT_LEDGER_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
        { logger?.LogWarning("[STRIPE PURCHASE] skipped: MEASUREMENT_LEDGER_ENABLED is not true"); return; }
        if (stripeEvent.Type is not ("invoice.paid" or "payment_intent.succeeded"))
        { logger?.LogInformation("[STRIPE PURCHASE] skipped: unsupported event type"); return; }
        logger?.LogInformation("[STRIPE PURCHASE] event accepted: {EventType}", stripeEvent.Type);
        using var document = JsonDocument.Parse(verifiedJson);
        var transaction = document.RootElement.GetProperty("data").GetProperty("object").GetProperty("id").GetString()!;
        if (await store.FindAsync(PurchaseMeasurementStore.TransactionId(transaction)) is not null)
        { logger?.LogInformation("[STRIPE PURCHASE] duplicate purchase ignored"); return; }
        var payment = await resolver.ResolveAsync(stripeEvent, verifiedJson);
        if (payment is not null)
        {
            logger?.LogInformation("[STRIPE PURCHASE] qualification passed");
            var purchase = await store.RecordAsync(payment);
            // Ads-only, best effort. Never modify canonical payment facts or block GA4 on sidecar failure.
            if (adsProvenance is not null) await adsProvenance.CaptureAsync(purchase, document.RootElement);
        }
    }
}

public sealed class StripeVerifiedPaymentResolver(ILogger<StripeVerifiedPaymentResolver> logger, IStripeClient? client = null) : IStripeVerifiedPaymentResolver
{
    public async Task<VerifiedPayment?> ResolveAsync(Event stripeEvent, string verifiedJson)
    {
        if (stripeEvent.Type == "invoice.paid")
            logger.LogInformation("[STRIPE PURCHASE] resolver invoked: invoice.paid");
        using var json = JsonDocument.Parse(verifiedJson);
        var data = json.RootElement.GetProperty("data").GetProperty("object");
        var transaction = data.GetProperty("id").GetString()!;
        var stripe = client ?? new StripeClient(Environment.GetEnvironmentVariable("StripeSecretKey"));
        string? attemptId = null, subscriptionId = null;
        long amount;
        var reason = "one_time";
        bool firstCandidate = true, renewal = false;
        var paidAt = new DateTimeOffset(stripeEvent.Created.ToUniversalTime());
        if (stripeEvent.Type == "invoice.paid")
        {
            if (Text(data, "status") != "paid") { logger.LogInformation("[STRIPE PURCHASE] skipped: invoice not paid"); return null; }
            if (data.TryGetProperty("paid_out_of_band", out var outOfBand) && outOfBand.ValueKind == JsonValueKind.True)
            { logger.LogInformation("[STRIPE PURCHASE] skipped: out-of-band payment"); return null; }
            amount = data.GetProperty("amount_paid").GetInt64();
            if (amount <= 0) { logger.LogInformation("[STRIPE PURCHASE] skipped: amount not positive"); return null; }
            reason = Text(data, "billing_reason") ?? "unknown";
            renewal = reason == "subscription_cycle";
            JsonElement details = default;
            // Current (including Dahlia) subscription snapshot takes precedence over legacy fields.
            // Never resolve checkout context from invoice-level or line-item metadata.
            if (data.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object) parent.TryGetProperty("subscription_details", out details);
            if (details.ValueKind != JsonValueKind.Object) data.TryGetProperty("subscription_details", out details);
            subscriptionId = Text(details, "subscription") ?? Text(data, "subscription");
            if (details.ValueKind == JsonValueKind.Object && details.TryGetProperty("metadata", out var metadata)) attemptId = Text(metadata, "checkoutAttemptId");
            if (string.IsNullOrWhiteSpace(subscriptionId)) { logger.LogInformation("[STRIPE PURCHASE] skipped: subscription context unavailable"); return null; }
            logger.LogInformation("[STRIPE PURCHASE] invoice qualifies as paid subscription invoice");
            logger.LogInformation("[STRIPE PURCHASE] subscription context resolved");
            if (string.IsNullOrWhiteSpace(attemptId))
            {
                logger.LogInformation("[STRIPE PURCHASE] subscription metadata lookup starting");
                Subscription subscription;
                try { subscription = await new SubscriptionService(stripe).GetAsync(subscriptionId); }
                catch (StripeException)
                { logger.LogError("[STRIPE PURCHASE] failed: subscription metadata lookup"); throw; }
                subscription.Metadata?.TryGetValue("checkoutAttemptId", out attemptId);
            }
            if (string.IsNullOrWhiteSpace(attemptId))
            { logger.LogWarning("[STRIPE PURCHASE] skipped: checkout attempt reference unavailable"); return null; }
            logger.LogInformation("[STRIPE PURCHASE] checkout attempt reference resolved");
            if (data.TryGetProperty("status_transitions", out var transitions) && transitions.TryGetProperty("paid_at", out var timestamp) && timestamp.TryGetInt64(out var seconds)) paidAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            // Includes trial conversions: first positive paid invoice, not merely subscription_create.
            var created = data.GetProperty("created").GetInt64();
            logger.LogInformation("[STRIPE PURCHASE] paid invoice history lookup starting");
            try
            {
                await foreach (var prior in new InvoiceService(stripe).ListAutoPagingAsync(new InvoiceListOptions { Subscription = subscriptionId, Status = "paid", Limit = 100 }))
                    if (prior.Id != transaction && prior.AmountPaid > 0 && new DateTimeOffset(prior.Created.ToUniversalTime()).ToUnixTimeSeconds() < created) { firstCandidate = false; break; }
            }
            catch (StripeException)
            { logger.LogError("[STRIPE PURCHASE] failed: paid invoice history lookup"); throw; }
            logger.LogInformation("[STRIPE PURCHASE] paid invoice history lookup completed");
        }
        else if (stripeEvent.Type == "payment_intent.succeeded")
        {
            if (Text(data, "status") != "succeeded") { logger.LogInformation("[STRIPE PURCHASE] skipped: payment not succeeded"); return null; }
            amount = data.GetProperty("amount_received").GetInt64();
            if (amount <= 0) { logger.LogInformation("[STRIPE PURCHASE] skipped: amount not positive"); return null; }
            var sessions = await new SessionService(stripe).ListAsync(new SessionListOptions { PaymentIntent = transaction, Limit = 1 });
            var session = sessions.FirstOrDefault();
            if (session is null || session.Mode != "payment" || session.SubscriptionId is not null)
            { logger.LogInformation("[STRIPE PURCHASE] skipped: one-time Checkout Session not resolved"); return null; }
            session.Metadata?.TryGetValue("checkoutAttemptId", out attemptId);
        }
        else { logger.LogInformation("[STRIPE PURCHASE] skipped: unsupported event type"); return null; }
        if (string.IsNullOrWhiteSpace(attemptId))
        {
            logger.LogWarning("[STRIPE PURCHASE] skipped: checkout attempt reference unavailable");
            return null;
        }
        logger.LogInformation("[STRIPE PURCHASE] payment qualifies");
        var plan = ConfiguredInvoicePlan(data, stripeEvent.Livemode);
        return new(transaction, stripeEvent.Id, attemptId, subscriptionId, amount,
            Text(data, "currency") ?? "usd", paidAt, firstCandidate, renewal, reason)
        { Plan = plan, BillingInterval = plan is null ? null : "monthly" };
    }
    public static string? ConfiguredInvoicePlan(JsonElement invoice, bool live)
    {
        if (!invoice.TryGetProperty("lines", out var lines) || !lines.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var plans = new HashSet<string>();
        foreach (var line in data.EnumerateArray())
        {
            if (!line.TryGetProperty("amount", out var amount) || !amount.TryGetInt64(out var value) || value <= 0) continue;
            var priceId = line.TryGetProperty("price", out var price) ? Text(price, "id") : null;
            if (line.TryGetProperty("pricing", out var pricing) && pricing.TryGetProperty("price_details", out var details)) priceId ??= Text(details, "price");
            if (ConfiguredPricePlan(priceId, live) is { } plan) plans.Add(plan);
        }
        return plans.Count == 1 ? plans.Single() : null;
    }
    public static string? ConfiguredPricePlan(string? priceId, bool live) => new[] { "plus", "extreme" }
        .FirstOrDefault(plan => !string.IsNullOrEmpty(priceId) && priceId == Environment.GetEnvironmentVariable($"AI_UPGRADES_{(live ? "LIVE" : "TEST")}_{plan.ToUpperInvariant()}_PRICE_ID"));
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
