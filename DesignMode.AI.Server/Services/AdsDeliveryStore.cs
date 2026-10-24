using Amazon.DynamoDBv2.Model;
using System.Globalization;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

public interface IAdsDeliveryStore
{
    Task<bool> CreateAsync(AdsDeliveryRecord record);
    Task<List<AdsDeliveryRecord>> CandidatesAsync();
    Task<PurchaseRecord?> PurchaseAsync(string id);
    Task<DateTimeOffset?> ClickCapturedAtAsync(PurchaseRecord purchase);
    Task<bool> SaveEvaluationAsync(AdsDeliveryRecord previous, AdsEligibilityResult result, DateTimeOffset now);
}

public sealed class AdsDeliveryStore(AttributionStore clients, PurchaseMeasurementStore purchases) : IAdsDeliveryStore
{
    public const string TableName = "AdsDeliveries";
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = S(id) };
    private static Dictionary<string, AttributeValue> Item(AdsDeliveryRecord row)
    {
        var item = Key(row.PurchaseTransactionId);
        item["Data"] = S(JsonSerializer.Serialize(row));
        item["AdsStatus"] = S(row.AdsStatus.ToString());
        item["Revision"] = N(row.Revision);
        return item;
    }
    public async Task<bool> CreateAsync(AdsDeliveryRecord record)
    {
        using var db = clients.CreateClient();
        try
        {
            await db.PutItemAsync(new PutItemRequest { TableName = TableName, Item = Item(record), ConditionExpression = "attribute_not_exists(Id)" });
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }
    public async Task<AdsDeliveryRecord?> FindAsync(string id)
    {
        using var db = clients.CreateClient();
        var response = await db.GetItemAsync(new GetItemRequest { TableName = TableName, Key = Key(id), ConsistentRead = true });
        return response.Item?.TryGetValue("Data", out var data) == true ? JsonSerializer.Deserialize<AdsDeliveryRecord>(data.S) : null;
    }
    public Task<PurchaseRecord?> PurchaseAsync(string id) => purchases.FindAsync(id);
    public async Task<DateTimeOffset?> ClickCapturedAtAsync(PurchaseRecord purchase)
    {
        // Use the original server timestamp, NOT checkout/snapshot creation time (which would refresh old clicks).
        if (string.IsNullOrWhiteSpace(purchase.Snapshot.TouchId)) return null;
        using var db = clients.CreateClient();
        var response = await db.GetItemAsync(new GetItemRequest { TableName = "AttributionTouches", Key = Key(purchase.Snapshot.TouchId), ConsistentRead = true });
        var row = response.Item;
        if (row is null || row.GetValueOrDefault("FunnelId")?.S != purchase.FunnelId) return null;
        foreach (var pair in new[] { ("Gclid", purchase.Snapshot.Gclid), ("Gbraid", purchase.Snapshot.Gbraid), ("Wbraid", purchase.Snapshot.Wbraid) })
            if (!string.Equals(row.GetValueOrDefault(pair.Item1)?.S ?? "", pair.Item2 ?? "", StringComparison.Ordinal)) return null;
        return DateTimeOffset.TryParse(row.GetValueOrDefault("CreatedAt")?.S, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
    }
    public async Task<List<AdsDeliveryRecord>> CandidatesAsync()
    {
        // Never scan Purchases to manufacture provenance for historical Pending rows.
        using var db = clients.CreateClient();
        var result = new List<AdsDeliveryRecord>(); Dictionary<string, AttributeValue>? key = null;
        do
        {
            var page = await db.ScanAsync(new ScanRequest { TableName = TableName, ExclusiveStartKey = key,
                FilterExpression = "AdsStatus IN (:pending, :eligible, :retry)", ExpressionAttributeValues = new()
                { [":pending"] = S("Pending"), [":eligible"] = S("Eligible"), [":retry"] = S("RetryScheduled") } });
            result.AddRange((page.Items ?? []).Select(x => JsonSerializer.Deserialize<AdsDeliveryRecord>(x["Data"].S)!));
            key = page.LastEvaluatedKey;
        } while (key is { Count: > 0 });
        return result;
    }
    // Full-row CAS pins provenance/destination and fences every operation with an incrementing revision.
    public static PutItemRequest ConditionalReplacement(AdsDeliveryRecord before, AdsDeliveryRecord after) => new()
    {
        TableName = TableName, Item = Item(after with { Revision = checked(before.Revision + 1) }),
        ConditionExpression = "Revision = :revision AND AdsStatus = :status",
        ExpressionAttributeValues = new() { [":revision"] = N(before.Revision), [":status"] = S(before.AdsStatus.ToString()) }
    };
    private async Task<bool> ReplaceAsync(AdsDeliveryRecord before, AdsDeliveryRecord after)
    {
        using var db = clients.CreateClient();
        try { await db.PutItemAsync(ConditionalReplacement(before, after)); return true; }
        catch (ConditionalCheckFailedException) { return false; }
    }
    public Task<bool> SaveEvaluationAsync(AdsDeliveryRecord previous, AdsEligibilityResult result, DateTimeOffset now)
    {
        if (previous.AdsStatus is not (AdsDeliveryStatus.Pending or AdsDeliveryStatus.Eligible or AdsDeliveryStatus.RetryScheduled)
            || previous.AdsLeaseUntil > now || result.Status is not (AdsDeliveryStatus.Eligible or AdsDeliveryStatus.NotEligible or AdsDeliveryStatus.Expired or AdsDeliveryStatus.NeedsReview))
            return Task.FromResult(false);
        // A routine consent refresh must not advance an existing retry schedule.
        var status = previous.AdsStatus == AdsDeliveryStatus.RetryScheduled && result.Status == AdsDeliveryStatus.Eligible ? previous.AdsStatus : result.Status;
        return ReplaceAsync(previous, previous with { AdsStatus = status, AdsEligibilityReason = result.Reason, UpdatedAt = now });
    }
    public async Task<AdsDeliveryRecord?> AcquireAsync(string id, string owner, DateTimeOffset now)
    {
        if (!Guid.TryParse(owner, out _)) throw new ArgumentException("Lease owner must be an opaque UUID.");
        var row = await FindAsync(id);
        if (row is null || row.AdsStatus is not (AdsDeliveryStatus.Eligible or AdsDeliveryStatus.RetryScheduled or AdsDeliveryStatus.Accepted)
            || row.AdsLeaseUntil > now || row.AdsNextAttemptAt > now) return null;
        var next = row with { AdsLeaseOwner = owner, AdsLeaseUntil = now.AddMinutes(5), UpdatedAt = now, Revision = row.Revision + 1 };
        return await ReplaceAsync(row, next) ? next : null;
    }
    public static bool CanTransition(AdsDeliveryStatus from, AdsDeliveryStatus to) => from switch
    {
        AdsDeliveryStatus.Eligible or AdsDeliveryStatus.RetryScheduled => to is AdsDeliveryStatus.Accepted or AdsDeliveryStatus.RetryScheduled
            or AdsDeliveryStatus.NotEligible or AdsDeliveryStatus.Expired or AdsDeliveryStatus.Rejected or AdsDeliveryStatus.NeedsReview,
        AdsDeliveryStatus.Accepted => to is AdsDeliveryStatus.Submitted or AdsDeliveryStatus.Rejected or AdsDeliveryStatus.NeedsReview,
        _ => false
    };
    public Task<bool> CompleteLeaseAsync(AdsDeliveryRecord lease, string owner, AdsDeliveryStatus status, DateTimeOffset now)
    {
        if (lease.AdsLeaseOwner != owner || lease.AdsLeaseUntil is null || lease.AdsLeaseUntil <= now || !CanTransition(lease.AdsStatus, status))
            return Task.FromResult(false);
        // Future transport must supply acknowledgement/diagnostics before these states are reachable.
        if (status is AdsDeliveryStatus.Accepted or AdsDeliveryStatus.Submitted) return Task.FromResult(false);
        return ReplaceAsync(lease, lease with { AdsStatus = status, AdsLeaseOwner = null, AdsLeaseUntil = null, UpdatedAt = now });
    }
}
