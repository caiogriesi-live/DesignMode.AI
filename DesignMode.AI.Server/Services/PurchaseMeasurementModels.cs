using DesignMode.AI.Shared.Models.Attribution;

namespace DesignMode.AI.Server.Services;

public sealed record AttributionSnapshot
{
    public string Id { get; init; } = "";
    public string AcquisitionId { get; init; } = "";
    public string? TouchId { get; init; }
    public string? Gclid { get; init; }
    public string? Gbraid { get; init; }
    public string? Wbraid { get; init; }
    public Dictionary<string, string> Acquisition { get; init; } = new();
    public MeasurementContext Measurement { get; init; } = new();
    // Separate Google consent evidence; internal always-on measurement is never evidence of a grant.
    public AdvertisingConsentEvidence? AdvertisingConsentEvidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record VerifiedPayment(string ProviderTransactionId, string StripeEventId, string CheckoutAttemptId,
    string? SubscriptionId, long AmountMinor, string Currency, DateTimeOffset PaidAt, bool FirstPaymentCandidate,
    bool Renewal, string BillingReason)
{
    public string? Plan { get; init; }
    public string? BillingInterval { get; init; }
}

public sealed record PurchaseRecord
{
    public string PurchaseType { get; init; } = "Subscription";
    public string? CreditPackageId { get; init; }
    public decimal CreditsGranted { get; init; }
    public decimal CreditsRevoked { get; init; }
    public bool IsTestCreditPurchase { get; init; }
    public string? StripeCheckoutSessionId { get; init; }
    public string TransactionId { get; init; } = "";
    public string CheckoutAttemptId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string FunnelId { get; init; } = "";
    public string AttributionSnapshotId { get; init; } = "";
    public string AcquisitionId { get; init; } = "";
    public string StripeTransactionId { get; init; } = "";
    public string StripeEventId { get; init; } = "";
    public string? StripeSubscriptionId { get; init; }
    public decimal Amount { get; init; }
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "USD";
    public string Plan { get; init; } = "";
    public string BillingInterval { get; init; } = "";
    public string PaymentStatus { get; init; } = "Paid";
    public string BillingReason { get; init; } = "";
    public bool IsFirstPayment { get; init; }

    public bool IsRenewal { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset PaidAt { get; init; }
    public DateTimeOffset? BillingPeriodStart { get; init; }
    public AttributionSnapshot Snapshot { get; init; } = new();
}

public static class MeasurementMoney
{
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
        { "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };
    public static decimal FromMinor(long amount, string currency) => ZeroDecimal.Contains(currency) ? amount : amount / 100m;
}
