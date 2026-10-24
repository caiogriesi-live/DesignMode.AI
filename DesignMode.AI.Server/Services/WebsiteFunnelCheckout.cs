using Stripe.Checkout;

namespace DesignMode.AI.Server.Services;

public static class WebsiteFunnelCheckout
{
    public static bool IsPaidOwner(Session? session, string userId) =>
        !string.IsNullOrWhiteSpace(userId) && session is { Status: "complete", PaymentStatus: "paid" } &&
        session.ClientReferenceId == userId && session.Metadata?.GetValueOrDefault("userId") == userId &&
        !string.IsNullOrWhiteSpace(session.Metadata.GetValueOrDefault("projectId")) &&
        !string.IsNullOrWhiteSpace(session.Metadata.GetValueOrDefault("funnelId"));
}
