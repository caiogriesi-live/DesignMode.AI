using Stripe;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

public interface IStripeAccountVerifier
{
    Task<bool?> VerifyAsync(JsonElement verifiedEvent);
}

// DesignMode uses a single platform credential; it does not create Checkout sessions with Stripe-Account overrides.
public sealed class StripeAccountVerifier(AdsDeliveryOptions options, IStripeClient? client = null) : IStripeAccountVerifier
{
    public async Task<bool?> VerifyAsync(JsonElement verifiedEvent)
    {
        static string? Text(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!verifiedEvent.TryGetProperty("livemode", out var live) || live.ValueKind != JsonValueKind.True) return false;
        if (string.IsNullOrWhiteSpace(options.ExpectedStripeAccountId)) return null;
        // A Connect event is outside this integration's platform-account model. Do not guess or follow browser metadata.
        if (!string.IsNullOrEmpty(Text(verifiedEvent, "account"))) return false;
        var id = Text(verifiedEvent, "id"); var type = Text(verifiedEvent, "type");
        if (string.IsNullOrWhiteSpace(id) || type is not ("invoice.paid" or "payment_intent.succeeded")) return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var stripe = client ?? new StripeClient(Environment.GetEnvironmentVariable("StripeSecretKey"));
            var account = await new AccountService(stripe).GetSelfAsync(cancellationToken: timeout.Token);
            if (account.Id != options.ExpectedStripeAccountId) return false;
            // Retrieving the signed event under the SAME credential establishes event ownership, not merely key ownership.
            var retrieved = await new EventService(stripe).GetAsync(id, cancellationToken: timeout.Token);
            var paymentId = Text(verifiedEvent.GetProperty("data").GetProperty("object"), "id");
            var retrievedPaymentId = retrieved.Data?.Object switch { Invoice invoice => invoice.Id, PaymentIntent intent => intent.Id, _ => null };
            return retrieved.Id == id && retrieved.Type == type && retrieved.Livemode && string.IsNullOrEmpty(retrieved.Account)
                && !string.IsNullOrEmpty(paymentId) && retrievedPaymentId == paymentId;
        }
        catch { return null; } // No raw Stripe exceptions, payloads or account identifiers in logs.
    }
}
