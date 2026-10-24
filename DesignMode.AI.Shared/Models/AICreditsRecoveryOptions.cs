namespace DesignMode.AI.Shared.Models;

public sealed class AICreditsRecoveryOptions
{
    public bool CreditPackConfigured { get; set; }
    public decimal? CreditPackUnits { get; set; }
    public string? CreditPackDisplayPrice { get; set; }
    // Configuration alone must never enable a purchase without a verified entitlement flow.
    public bool CanPurchaseCredits { get; set; }
    public bool IsSandbox { get; set; }
    public List<AICreditPackOption> CreditPacks { get; set; } = [];
    public List<AICreditsUpgradeOption> Upgrades { get; set; } = [];
}

public sealed record AICreditsUpgradeOption(string Plan, string Name, decimal MonthlyPrice,
    decimal? IncludedCredits, bool CanUpgrade = false);

public sealed record AICreditPackOption(string Id, string Name, decimal Price, decimal Credits, bool CanPurchase = false);
