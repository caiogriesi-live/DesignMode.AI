namespace DesignMode.AI.Shared.Models;

public sealed record AICreditCheckoutRequest(string PackageId);
public sealed record AICreditCheckoutResponse(string AttemptId, string Url);
public sealed record AICreditPurchaseStatus(bool Paid, decimal CreditsAdded, decimal? Remaining);
