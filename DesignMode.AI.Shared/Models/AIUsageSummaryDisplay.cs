namespace DesignMode.AI.Shared.Models;

public static class AIUsageSummaryDisplay
{
    public static string PrimaryLabel => "AI Credits";

    public static string PrimaryText(AIUsageSummary summary) =>
        summary.PurchasedCreditsRemaining > 0
            ? $"{Format(summary.GenerationsRemaining)} credits remaining"
            : summary.GenerationLimit is decimal limit
            ? $"{Format(summary.GenerationsUsed)} / {Format(limit)} used"
            : $"Credits: {Format(summary.GenerationsUsed)}";

    public static string RemainingText(AIUsageSummary summary) =>
        summary.CreditBalanceRestricted ? "Balance under review — contact support" :
        summary.PurchasedCreditsRemaining > 0 ? $"{Format(summary.BaseRemaining)} plan + {Format(summary.PurchasedCreditsRemaining)} purchased" :
        summary.GenerationLimit is decimal ? $"{Format(summary.GenerationsRemaining)} remaining" : "Unlimited";

    public static bool IsExhausted(AIUsageSummary summary) =>
        summary.GenerationLimit is decimal && summary.GenerationsRemaining <= 0m;

    private static string Format(decimal? value) => value.HasValue ? Format(value.Value) : "0";

    private static string Format(decimal value) =>
        value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
