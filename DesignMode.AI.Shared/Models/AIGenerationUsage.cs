namespace DesignMode.AI.Shared.Models;

public sealed class AIGenerationUsage
{
    public string Id { get; set; } = string.Empty;
    public string UsageId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ProjectId { get; set; }
    public string? FunnelId { get; set; }
    public string GenerationJobId { get; set; } = string.Empty;
    public string? ParentUsageId { get; set; }
    public string? GenerationCategory { get; set; }
    public string? DeliverableType { get; set; }
    public string? Feature { get; set; }
    public string? UsageScope { get; set; }
    public decimal? GenerationUnits { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ProviderRequestId { get; set; }
    public string? ProviderTaskId { get; set; }
    public long? InputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public int? ImageCount { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public decimal? VideoSeconds { get; set; }
    public decimal? AudioSeconds { get; set; }
    public long? CharacterCount { get; set; }
    public decimal? OtherBillableUnits { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "Started";
    public string? FailureCategory { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public string Currency { get; set; } = "USD";
    public string? PricingVersion { get; set; }
    public string? RawUsageReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public decimal? EffectiveCost => ActualCost ?? EstimatedCost;
}

public sealed record AIGenerationCostAggregate(decimal TotalActualCost, decimal TotalEstimatedCost, string Currency, int SucceededCount, int FailedCount);

public sealed class AIUsageSummary
{
    public AICreditsRecoveryOptions? RecoveryOptions { get; set; }
    public string Plan { get; set; } = string.Empty;
    public DateTimeOffset BillingPeriodStart { get; set; }
    public DateTimeOffset BillingPeriodEnd { get; set; }
    public decimal? GenerationLimit { get; set; }
    public decimal GenerationsUsed { get; set; }
    public decimal? GenerationsRemaining { get; set; }
    public decimal BaseRemaining { get; set; }
    public decimal PurchasedCreditsRemaining { get; set; }
    public bool CreditBalanceRestricted { get; set; }
    public decimal? PercentUsed { get; set; }
    public bool EnforcementEnabled { get; set; }
}
