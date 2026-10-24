using Amazon.DynamoDBv2.Model;
using System.Text.Json;
using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Server.Services;

public enum PurchaseRevenueKind { SubscriptionRevenue, AICreditRevenue }

/// <summary>A reporting projection of a qualifying canonical payment, retaining its source currency.</summary>
public sealed record PurchaseRevenueFact
{
    public string TransactionId { get; init; } = "";
    /// <summary>Internal aggregation key. Never expose this field in dashboard responses.</summary>
    public string CustomerKey { get; init; } = "";
    public DateTimeOffset PaidAt { get; init; }
    public PurchaseRevenueKind RevenueKind { get; init; }
    public decimal Amount { get; init; }
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "";
    public string? BillingReason { get; init; }
    public bool IsFirstPayment { get; init; }
    public bool IsRenewal { get; init; }
    public bool HasGoogleTransactionAttribution { get; init; }
    // These checkout-snapshot identifiers stay inside the server and are used only for exact reporting joins.
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? GoogleClickId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? GoogleClickIdType { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal DateTimeOffset? GoogleClickCapturedAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? CapturedCampaignId { get; init; }
}

public sealed record PurchaseReportingRead(IReadOnlyList<PurchaseRecord> Purchases, ReportingSourceCoverage Coverage);

/// <summary>The sole source operation is a read of canonical purchase records.</summary>
public interface IPurchaseReportingReader
{
    Task<PurchaseReportingRead> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only full scan of canonical Purchases. A successful scan is still partial until historical coverage is validated.</summary>
public sealed class DynamoPurchaseReportingReader(AttributionStore clients) : IPurchaseReportingReader
{
    public async Task<PurchaseReportingRead> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var db = clients.CreateClient();
            var purchases = new List<PurchaseRecord>();
            Dictionary<string, AttributeValue>? key = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await db.ScanAsync(new ScanRequest { TableName = "Purchases", ConsistentRead = true, ExclusiveStartKey = key }, cancellationToken);
                foreach (var item in page.Items ?? [])
                    if (item.TryGetValue("Data", out var data) && !string.IsNullOrWhiteSpace(data.S))
                    {
                        var purchase = JsonSerializer.Deserialize<PurchaseRecord>(data.S);
                        if (purchase is not null) purchases.Add(purchase);
                    }
                key = page.LastEvaluatedKey;
            } while (key is { Count: > 0 });

            return new(purchases, new ReportingSourceCoverage
            {
                Source = MarketingReportingSource.Purchases,
                State = ReportingDataState.Partial,
                RecordsObserved = purchases.Count,
                Explanation = "Canonical purchase records were read, but historical source coverage has not been validated; completeness is not established."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new([], new ReportingSourceCoverage
            {
                Source = MarketingReportingSource.Purchases,
                State = ReportingDataState.Error,
                Explanation = "Canonical purchase reporting read failed; no purchase coverage or revenue amount is asserted.",
                ErrorCode = "purchase_source_read_failed"
            });
        }
    }
}

public sealed record PurchaseReportingResult(
    IReadOnlyList<PurchaseRevenueFact> Rows,
    IReadOnlyList<ReportingSourceCoverage> SourceCoverage,
    ReportingDataState State);

/// <summary>Classifies canonical paid facts without writes, aggregation, currency conversion, or customer-level attribution inheritance.</summary>
public interface IPurchaseClickProvenanceReader
{
    Task<DateTimeOffset?> GetOriginalClickTimeAsync(PurchaseRecord purchase, CancellationToken cancellationToken = default);
}

public sealed class VerifiedPurchaseClickProvenanceReader(IAdsDeliveryStore store) : IPurchaseClickProvenanceReader
{
    public Task<DateTimeOffset?> GetOriginalClickTimeAsync(PurchaseRecord purchase, CancellationToken cancellationToken = default)
        => store.ClickCapturedAtAsync(purchase);
}

public sealed class PurchaseReportingSource(IPurchaseReportingReader reader, IPurchaseClickProvenanceReader? clickProvenance = null)
{
    public async Task<PurchaseReportingResult> GetVerifiedClickAttributionCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var read = await reader.ReadAsync(cancellationToken);
        var coverage = new List<ReportingSourceCoverage> { read.Coverage };
        coverage.AddRange(UnavailableFinancialSources());
        if (read.Coverage.State is ReportingDataState.Error or ReportingDataState.Unavailable)
            return new([], coverage, read.Coverage.State);
        var all = new List<PurchaseRevenueFact>();
        foreach (var purchase in read.Purchases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fact = Classify(purchase, IsSupportedUpgrade(purchase, read.Purchases));
            if (fact is null || fact.GoogleClickIdType != "gclid" || clickProvenance is null) continue;
            try
            {
                var clickTime = await clickProvenance.GetOriginalClickTimeAsync(purchase, cancellationToken);
                if (clickTime is not null) all.Add(fact with { GoogleClickCapturedAt = clickTime });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* Missing/inaccessible click provenance remains unattributed. */ }
        }
        var facts = all.Where(x => x.GoogleClickCapturedAt is not null && x.GoogleClickCapturedAt <= x.PaidAt)
            .OrderBy(x => x.PaidAt).ToArray();
        return new(facts, coverage, ReportingDataState.Partial);
    }

    public async Task<PurchaseReportingResult> GetAsync(MarketingReportingPeriod period, CancellationToken cancellationToken = default)
    {
        var read = await reader.ReadAsync(cancellationToken);
        var coverage = new List<ReportingSourceCoverage> { read.Coverage };
        coverage.AddRange(UnavailableFinancialSources());
        if (read.Coverage.State is ReportingDataState.Error or ReportingDataState.Unavailable)
            return new([], coverage, read.Coverage.State);
        if (period.EndExclusive <= period.StartInclusive)
            return new([], coverage, ReportingDataState.Error);

        var facts = read.Purchases.Where(x => x.PaidAt >= period.StartInclusive && x.PaidAt < period.EndExclusive)
            .Select(x => Classify(x, IsSupportedUpgrade(x, read.Purchases))).Where(x => x is not null).Select(x => x!).OrderBy(x => x.PaidAt).ToArray();
        var state = read.Coverage.State == ReportingDataState.Complete
            ? ReportingDataState.Partial // financial source coverage remains unvalidated/unavailable
            : read.Coverage.State;
        return new(facts, coverage, state);
    }

    public static PurchaseRevenueFact? Classify(PurchaseRecord purchase, bool isSupportedUpgrade = false)
    {
        if (purchase.PaymentStatus != "Paid" || purchase.Amount <= 0 || purchase.AmountMinor <= 0 ||
            string.IsNullOrWhiteSpace(purchase.TransactionId) || string.IsNullOrWhiteSpace(purchase.Currency) || purchase.PaidAt == default)
            return null;

        PurchaseRevenueKind kind;
        if (string.Equals(purchase.PurchaseType, "AICredits", StringComparison.OrdinalIgnoreCase))
        {
            if (purchase.IsTestCreditPurchase || !string.Equals(purchase.BillingReason, "ai_credit_topup", StringComparison.Ordinal)) return null;
            kind = PurchaseRevenueKind.AICreditRevenue;
        }
        else if (string.Equals(purchase.PurchaseType, "Subscription", StringComparison.OrdinalIgnoreCase))
        {
            var supported = purchase.BillingReason switch
            {
                "subscription_create" => purchase.IsFirstPayment,
                "subscription_cycle" => purchase.IsRenewal,
                "subscription_update" => isSupportedUpgrade && !purchase.IsFirstPayment && !purchase.IsRenewal,
                _ => false
            };
            if (!supported) return null;
            kind = PurchaseRevenueKind.SubscriptionRevenue;
        }
        else return null;

        // Only transaction's own checkout snapshot is considered. AcquisitionId alone is intentionally insufficient.
        var snapshot = purchase.Snapshot;
        var googleEvidence = !string.IsNullOrWhiteSpace(snapshot.Gclid) || !string.IsNullOrWhiteSpace(snapshot.Gbraid) ||
            !string.IsNullOrWhiteSpace(snapshot.Wbraid) || snapshot.Acquisition.Any(x =>
                x.Key.Equals("UtmSource", StringComparison.OrdinalIgnoreCase) && x.Value.Equals("google", StringComparison.OrdinalIgnoreCase));
        return new PurchaseRevenueFact
        {
            TransactionId = purchase.TransactionId, CustomerKey = purchase.UserId, PaidAt = purchase.PaidAt, RevenueKind = kind,
            Amount = purchase.Amount, AmountMinor = purchase.AmountMinor, Currency = purchase.Currency,
            BillingReason = purchase.BillingReason, IsFirstPayment = purchase.IsFirstPayment,
            IsRenewal = purchase.IsRenewal, HasGoogleTransactionAttribution = googleEvidence,
            GoogleClickId = !string.IsNullOrWhiteSpace(snapshot.Gclid) ? snapshot.Gclid : null,
            GoogleClickIdType = !string.IsNullOrWhiteSpace(snapshot.Gclid) ? "gclid"
                : !string.IsNullOrWhiteSpace(snapshot.Gbraid) ? "gbraid"
                : !string.IsNullOrWhiteSpace(snapshot.Wbraid) ? "wbraid" : null,
            // Checkout snapshots can be created long after the original click; only the verified
            // AttributionTouches record supplies the click timestamp for campaign joins.
            GoogleClickCapturedAt = null,
            CapturedCampaignId = snapshot.Acquisition.GetValueOrDefault("CampaignId")
        };
    }

    private static bool IsSupportedUpgrade(PurchaseRecord purchase, IReadOnlyList<PurchaseRecord> purchases)
    {
        if (purchase.BillingReason != "subscription_update" || purchase.BillingPeriodStart is null ||
            string.IsNullOrWhiteSpace(purchase.StripeSubscriptionId) || string.IsNullOrWhiteSpace(purchase.Plan))
            return false;

        var previous = purchases.Where(x => x.PaymentStatus == "Paid" && x.PaidAt < purchase.PaidAt &&
                x.StripeSubscriptionId == purchase.StripeSubscriptionId)
            .OrderByDescending(x => x.PaidAt).FirstOrDefault();
        return previous is not null && IsSupportedPlanUpgrade(previous.Plan, purchase.Plan);
    }

    // Keep reporting aligned with the upgrade options exposed by AICreditsRecoveryConfiguration.
    // Unknown plans and plan changes without a configured upward path are not proven upgrades.
    private static bool IsSupportedPlanUpgrade(string? previousPlan, string? currentPlan)
    {
        var previous = previousPlan?.Trim().ToLowerInvariant();
        var current = currentPlan?.Trim().ToLowerInvariant();
        return (previous, current) switch
        {
            ("starter", "plus" or "extreme") => true,
            ("pro" or "plus", "extreme") => true,
            _ => false
        };
    }

    private static IEnumerable<ReportingSourceCoverage> UnavailableFinancialSources()
    {
        foreach (var source in new[] { MarketingReportingSource.Refunds, MarketingReportingSource.Chargebacks,
                     MarketingReportingSource.Taxes, MarketingReportingSource.StripeFees })
            yield return new ReportingSourceCoverage
            {
                Source = source,
                State = ReportingDataState.Unavailable,
                Explanation = "No validated read-only source with reliable transaction association is available; amount is unknown, not zero."
            };
    }
}
