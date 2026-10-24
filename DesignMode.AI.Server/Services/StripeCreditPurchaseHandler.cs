using Stripe;
using Stripe.Checkout;

namespace DesignMode.AI.Server.Services;

// Called exclusively inside the existing signature-verified webhook. The canonical
// grant event is payment_intent.succeeded, keyed by the PaymentIntent, never a redirect.
public sealed class StripeCreditPurchaseHandler(AICreditPurchaseOptions options, AICreditLedger ledger, IStripeClient? client = null)
{
    public async Task<string> CreateCheckoutAsync(CreditPackage pack, string attemptId, string dashboard)
    {
        var stripe = client ?? new StripeClient(Environment.GetEnvironmentVariable("StripeSecretKey"));
        var session = await new SessionService(stripe).CreateAsync(new SessionCreateOptions
        {
            Mode = "payment", ClientReferenceId = attemptId,
            SuccessUrl = dashboard + "?credits=return", CancelUrl = dashboard,
            LineItems = [new() { Price = pack.PriceId, Quantity = 1 }]
        }, new RequestOptions { IdempotencyKey = attemptId });
        return session.Url;
    }
    public async Task ValidateCheckoutAsync(CreditPackage pack)
    {
        var stripe = client ?? new StripeClient(Environment.GetEnvironmentVariable("StripeSecretKey"));
        var link = await new PaymentLinkService(stripe).GetAsync(pack.PaymentLinkId);
        var lines = await new PaymentLinkLineItemService(stripe).ListAsync(pack.PaymentLinkId, new PaymentLinkLineItemListOptions { Limit = 2 });
        if (!link.Active || link.Livemode == options.IsTest || link.Url != pack.PaymentLink ||
            lines.HasMore || !PackageMatches(lines.Data, pack))
            throw new InvalidOperationException("Stripe credit link does not match its one-time package configuration.");
    }

    public async Task<bool> HandleVerifiedAsync(Event stripeEvent)
    {
        var stripe = client ?? new StripeClient(Environment.GetEnvironmentVariable("StripeSecretKey"));
        if (stripeEvent.Type is "charge.refunded" or "charge.dispute.created")
        {
            var chargeId = stripeEvent.Data.Object switch { Charge c => c.Id, Dispute d => d.ChargeId, _ => null };
            if (chargeId is null) return false;
            var reversedCharge = await new ChargeService(stripe).GetAsync(chargeId);
            if (string.IsNullOrEmpty(reversedCharge.PaymentIntentId)) return false;
            var purchase = await ledger.GetPurchaseAsync(PurchaseMeasurementStore.TransactionId(reversedCharge.PaymentIntentId));
            if (purchase?.PurchaseType != "AICredits") return false;
            if (!ledger.Enabled || purchase.IsTestCreditPurchase != options.IsTest || stripeEvent.Livemode == options.IsTest || !string.IsNullOrEmpty(stripeEvent.Account))
                throw new InvalidOperationException("Credit reversal environment mismatch.");
            await ledger.RevokeAsync(reversedCharge.PaymentIntentId, reversedCharge.AmountRefunded, reversedCharge.Disputed);
            return true;
        }
        if (stripeEvent.Data.Object is Session checkout && stripeEvent.Type.StartsWith("checkout.session.", StringComparison.Ordinal))
            return IsCreditCheckout(checkout); // Never pass a top-up into subscription mapping.
        if (stripeEvent.Type != "payment_intent.succeeded" || stripeEvent.Data.Object is not PaymentIntent intent) return false;
        var sessions = await new SessionService(stripe).ListAsync(new SessionListOptions { PaymentIntent = intent.Id, Limit = 2 });
        var session = sessions.SingleOrDefault();
        if (session is null || !IsCreditCheckout(session)) return false;
        var attempt = await ledger.GetAttemptAsync(session.ClientReferenceId ?? "");
        var pack = options.Packages.SingleOrDefault(x => x.Id == attempt?.PackageId);
        if (!ledger.Enabled || pack is null || !options.IsFulfillmentConfigured(pack) || stripeEvent.Livemode == options.IsTest || !string.IsNullOrEmpty(stripeEvent.Account))
            throw new InvalidOperationException("Credit payment configuration or mode mismatch.");
        var lines = await new SessionLineItemService(stripe).ListAsync(session.Id, new SessionLineItemListOptions { Limit = 2 });
        if (!Qualifies(intent, session, lines.Data, pack)) throw new InvalidOperationException("Credit payment package mismatch.");
        if (attempt is null || attempt.PackageId != pack.Id || attempt.IsTest != options.IsTest)
            throw new InvalidOperationException("Credit payment has no matching authenticated checkout correlation.");
        // Check current charge before AND after the transaction to close out-of-order
        // refund/webhook races. A failed post-check makes Stripe retry the webhook.
        var current = await new PaymentIntentService(stripe).GetAsync(intent.Id);
        var charge = await new ChargeService(stripe).GetAsync(current.LatestChargeId);
        if (charge.AmountRefunded > 0 || charge.Disputed)
        {
            // Record the original paid purchase and its reversal for auditability.
            var revoked = charge.Disputed ? pack.Credits : Math.Min(pack.Credits, decimal.Ceiling((decimal)pack.Credits * charge.AmountRefunded / pack.AmountMinor * 100m) / 100m);
            await ledger.GrantAsync(attempt, pack, intent.Id, session.Id, stripeEvent.Id, new DateTimeOffset(stripeEvent.Created.ToUniversalTime()), revoked);
            await ledger.RevokeAsync(intent.Id, charge.AmountRefunded, charge.Disputed);
        }
        else await ledger.GrantAsync(attempt, pack, intent.Id, session.Id, stripeEvent.Id, new DateTimeOffset(stripeEvent.Created.ToUniversalTime()));
        charge = await new ChargeService(stripe).GetAsync(current.LatestChargeId);
        await ledger.RevokeAsync(intent.Id, charge.AmountRefunded, charge.Disputed);
        return true;
    }

    private bool IsCreditCheckout(Session session) => session.ClientReferenceId?.StartsWith("credit_", StringComparison.Ordinal) == true ||
        options.Packages.Any(x => !string.IsNullOrEmpty(x.PaymentLinkId) && x.PaymentLinkId == session.PaymentLinkId);

    public static bool Qualifies(PaymentIntent intent, Session session, IReadOnlyList<LineItem> lines, CreditPackage pack) =>
        intent.Status == "succeeded" && intent.AmountReceived == pack.AmountMinor && intent.Currency == "usd" &&
        session.Mode == "payment" && session.SubscriptionId is null && session.PaymentStatus == "paid" &&
        session.PaymentIntentId == intent.Id && (session.PaymentLinkId == pack.PaymentLinkId ||
            session.PaymentLinkId is null && session.ClientReferenceId?.StartsWith("credit_", StringComparison.Ordinal) == true) &&
        session.AmountTotal == pack.AmountMinor && session.Currency == "usd" && PackageMatches(lines, pack);

    private static bool PackageMatches(IReadOnlyList<LineItem> lines, CreditPackage pack) => lines.Count == 1 &&
        lines[0].Quantity == 1 && lines[0].Price?.Id == pack.PriceId && lines[0].Price?.Type == "one_time" &&
        lines[0].Price?.UnitAmount == pack.AmountMinor && lines[0].Price?.Currency == "usd";
}
