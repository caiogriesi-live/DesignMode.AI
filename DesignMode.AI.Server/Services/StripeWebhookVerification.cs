using Stripe;

namespace DesignMode.AI.Server.Services;

public static class StripeWebhookVerification
{
    public static Event Verify(string rawBody, string signature, string secret) =>
        EventUtility.ConstructEvent(rawBody, signature, secret, throwOnApiVersionMismatch: false);
}
