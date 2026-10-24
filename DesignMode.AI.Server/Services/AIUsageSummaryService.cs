using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

public sealed class AIUsageSummaryService(AttributionStore clients, AIUsageQuotaOptions options, ILogger<AIUsageSummaryService> logger, AICreditLedger? credits = null)
{
    public async Task<AIUsageSummary?> GetAsync(string userId, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var purchase = await LatestPurchaseAsync(userId, current);
        if (purchase is null || string.IsNullOrWhiteSpace(purchase.BillingInterval))
            return credits?.Enabled == true ? await WithWalletAsync(userId, Calculate("none", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0)) : null;

        var periodStart = purchase.BillingPeriodStart ?? purchase.PaidAt;
        var periodEnd = BillingPeriodEnd(periodStart, purchase.BillingInterval);
        if (!periodEnd.HasValue)
            return credits?.Enabled == true ? await WithWalletAsync(userId, Calculate("none", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0)) : null;
        var plan = NormalizePlan(purchase.Plan);
        var limit = credits?.Enabled == true && periodEnd <= current ? 0 : options.LimitFor(plan);
        var used = await UsedAsync(userId, periodStart, periodEnd.Value);

        return await WithWalletAsync(userId, new AIUsageSummary
        {
            Plan = plan,
            BillingPeriodStart = periodStart,
            BillingPeriodEnd = periodEnd.Value,
            GenerationLimit = limit,
            GenerationsUsed = used,
            GenerationsRemaining = limit.HasValue ? Math.Max(0m, limit.Value - used) : null,
            PercentUsed = limit is > 0 ? Math.Min(100m, decimal.Round(used * 100m / limit.Value, 1)) : null,
            EnforcementEnabled = limit.HasValue
        });
    }

    private async Task<AIUsageSummary> WithWalletAsync(string userId, AIUsageSummary summary)
    {
        summary.BaseRemaining = Math.Max(0, (summary.GenerationLimit ?? 0) - summary.GenerationsUsed);
        if (credits?.Enabled != true) return summary;
        summary.GenerationLimit ??= 0;
        var wallet = await credits.GetWalletAsync(userId);
        var used = wallet.Period == summary.BillingPeriodStart.ToString("O") ? wallet.BaseUsed : summary.GenerationsUsed;
        summary.BaseRemaining = Math.Max(0, (summary.GenerationLimit ?? 0) - used);
        summary.PurchasedCreditsRemaining = Math.Max(0, wallet.Purchased);
        summary.CreditBalanceRestricted = wallet.Purchased < 0;
        summary.GenerationsRemaining = wallet.Purchased < 0 ? 0 : summary.BaseRemaining + summary.PurchasedCreditsRemaining;
        summary.EnforcementEnabled = true;
        return summary;
    }

    public async Task<AIQuotaDecision> EvaluateAsync(string userId, string feature)
    {
        var summary = await GetAsync(userId);
        var requested = options.UnitsFor(feature);
        var allowed = IsAllowed(summary, requested);
        var reason = summary is null ? "billing_period_unavailable" :
            !summary.EnforcementEnabled ? "limit_not_configured" :
            allowed ? "sufficient_remaining_units" : "insufficient_remaining_units";
        logger.LogInformation("[AI QUOTA] feature={Feature}", SafeFeature(feature));
        logger.LogInformation("[AI QUOTA] plan={Plan}", summary?.Plan ?? "unavailable");
        logger.LogInformation("[AI QUOTA] limit={Limit}", summary?.GenerationLimit);
        logger.LogInformation("[AI QUOTA] used={Used}", summary?.GenerationsUsed);
        logger.LogInformation("[AI QUOTA] remaining={Remaining}", summary?.GenerationsRemaining);
        logger.LogInformation("[AI QUOTA] requestedUnits={RequestedUnits}", requested);
        logger.LogInformation("[AI QUOTA] allowed={Allowed}", allowed.ToString().ToLowerInvariant());
        logger.LogInformation("[AI QUOTA] reason={Reason}", reason);
        return new(summary, requested, allowed, reason);
    }

    public decimal UnitsFor(string feature) => options.UnitsFor(feature);

    public static bool IsAllowed(AIUsageSummary? summary, decimal requestedUnits) =>
        summary is null || !summary.EnforcementEnabled || summary.GenerationsRemaining >= requestedUnits;

    public static decimal AggregateGenerationUnits(IEnumerable<(string Id, string? JobId, string? Feature, decimal Units)> rows)
    {
        var unique = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = !string.IsNullOrWhiteSpace(row.JobId) ? $"{row.JobId}|{row.Feature}" : row.Id;
            unique[key] = Math.Max(unique.GetValueOrDefault(key), Math.Max(0m, row.Units));
        }
        return unique.Values.Sum();
    }

    public static bool CountsTowardPaidAllowance(string? scope, string? status, DateTimeOffset startedAt, DateTimeOffset periodStart, DateTimeOffset periodEnd) =>
        string.Equals(scope, "Paid", StringComparison.Ordinal) &&
        string.Equals(status, "Succeeded", StringComparison.Ordinal) &&
        startedAt >= periodStart && startedAt < periodEnd;

    public static DateTimeOffset? BillingPeriodEnd(DateTimeOffset start, string? interval) =>
        (interval ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "month" or "monthly" => start.AddMonths(1),
            "year" or "yearly" => start.AddYears(1),
            _ => null
        };

    public static AIUsageSummary Calculate(string plan, DateTimeOffset start, DateTimeOffset end, decimal? limit, decimal used) => new()
    {
        Plan = plan,
        BillingPeriodStart = start,
        BillingPeriodEnd = end,
        GenerationLimit = limit,
        GenerationsUsed = used,
        GenerationsRemaining = limit.HasValue ? Math.Max(0m, limit.Value - used) : null,
        PercentUsed = limit is > 0 ? Math.Min(100m, decimal.Round(used * 100m / limit.Value, 1)) : null,
        EnforcementEnabled = limit.HasValue
    };

    private async Task<PurchaseRecord?> LatestPurchaseAsync(string userId, DateTimeOffset now)
    {
        using var db = clients.CreateClient();
        Dictionary<string, AttributeValue>? key = null;
        var rows = new List<PurchaseRecord>();
        do
        {
            var page = await db.ScanAsync(new ScanRequest
            {
                TableName = "Purchases", ExclusiveStartKey = key,
                ConsistentRead = true,
                FilterExpression = "UserId = :user",
                ExpressionAttributeValues = new() { [":user"] = new() { S = userId } }
            });
            rows.AddRange(page.Items.Where(x => x.ContainsKey("Data")).Select(x => JsonSerializer.Deserialize<PurchaseRecord>(x["Data"].S)!).Where(x => x.PaidAt <= now));
            key = page.LastEvaluatedKey;
        } while (key is { Count: > 0 });
        return rows.Where(x => x.PurchaseType != "AICredits" && x.PaymentStatus == "Paid" && !string.IsNullOrWhiteSpace(x.BillingInterval))
            .OrderByDescending(x => x.PaidAt).FirstOrDefault();
    }

    private async Task<decimal> UsedAsync(string userId, DateTimeOffset start, DateTimeOffset end)
    {
        using var db = clients.CreateClient();
        Dictionary<string, AttributeValue>? key = null;
        var rows = new List<(string Id, string? JobId, string? Feature, decimal Units)>();
        do
        {
            var page = await db.ScanAsync(new ScanRequest
            {
                TableName = "AIGenerationUsage", ExclusiveStartKey = key,
                ConsistentRead = true,
                FilterExpression = "UserId = :user AND UsageScope = :scope AND #status = :succeeded AND StartedAt >= :start AND StartedAt < :end",
                ExpressionAttributeNames = new() { ["#status"] = "Status" },
                ExpressionAttributeValues = new()
                {
                    [":user"] = new() { S = userId }, [":scope"] = new() { S = "Paid" }, [":succeeded"] = new() { S = "Succeeded" },
                    [":start"] = new() { S = start.ToString("O") }, [":end"] = new() { S = end.ToString("O") }
                }
            });
            foreach (var item in page.Items)
            {
                var units = decimal.TryParse(item.GetValueOrDefault("GenerationUnits")?.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
                var job = item.GetValueOrDefault("GenerationJobId")?.S;
                var feature = item.GetValueOrDefault("Feature")?.S;
                var id = item.GetValueOrDefault("Id")?.S ?? Guid.NewGuid().ToString();
                rows.Add((id, job, feature, units));
            }
            key = page.LastEvaluatedKey;
        } while (key is { Count: > 0 });
        return AggregateGenerationUnits(rows);
    }

    private static string SafeFeature(string feature) => feature switch
    {
        "website_generation" => "website",
        "gen_main_image" => "main_image",
        "gen_secondary_image" => "secondary_image",
        "social_text" or "social_image" or "social_post" => "social",
        "ai_recommendations" => "recommendations",
        "commercial_video" => "commercial_video",
        "package_label" => "package_label",
        "print_material" => "print_material",
        "branding_logo" => "branding_logo",
        "digital_marketing" => "digital_marketing",
        "event_activation" => "event_activation",
        _ => "unknown"
    };
    private static string NormalizePlan(string? plan) => (plan ?? "").Trim().ToLowerInvariant() switch
    {
        var x when x.Contains("extreme") => "extreme",
        var x when x.Contains("pro") => "pro",
        var x when x.Contains("plus") => "plus",
        var x when x.Contains("starter") => "starter",
        _ => "unknown"
    };
}
