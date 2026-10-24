using Amazon.DynamoDBv2.Model;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

public sealed class PurchaseMeasurementStore(AttributionStore clients, ILogger<PurchaseMeasurementStore>? logger = null)
{
    public static string TransactionId(string providerTransaction) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("stripe-purchase:v1:" + providerTransaction))[..16]).ToString();
    private static AttributeValue S(string value) => new() { S = value };
    private static Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = S(id) };
    public async Task<PurchaseRecord?> FindAsync(string id)
    {
        using var db = clients.CreateClient();
        var row = await db.GetItemAsync(new GetItemRequest { TableName = "Purchases", Key = Key(id), ConsistentRead = true });
        if (row.Item is not { Count: > 0 }) return null;
        return row.Item.TryGetValue("Data", out var data) ? JsonSerializer.Deserialize<PurchaseRecord>(data.S) : null;
    }
    public async Task<PurchaseRecord> RecordAsync(VerifiedPayment payment)
    {
        logger?.LogInformation("[STRIPE PURCHASE] persistence invoked");
        if (payment.AmountMinor <= 0) throw new ArgumentException("A collected payment must be positive.");
        var id = TransactionId(payment.ProviderTransactionId);
        var existing = await FindAsync(id);
        if (existing is not null) { logger?.LogInformation("[STRIPE PURCHASE] duplicate purchase ignored"); return existing; }
        using var db = clients.CreateClient();
        var attempt = (await db.GetItemAsync(new GetItemRequest { TableName = "CheckoutAttempts", Key = Key(payment.CheckoutAttemptId), ConsistentRead = true })).Item ?? new();
        string Read(string field) => attempt.GetValueOrDefault(field)?.S ?? "";
        if (attempt.Count == 0 || string.IsNullOrWhiteSpace(Read("UserId")))
        { logger?.LogError("[STRIPE PURCHASE] failed: CheckoutAttempt not found or missing canonical user"); throw new InvalidOperationException("Checkout attempt unresolved."); }
        var snapshotRow = (await db.GetItemAsync(new GetItemRequest { TableName = "AttributionSnapshots", Key = Key(Read("AttributionSnapshotId")), ConsistentRead = true })).Item ?? new();
        if (!snapshotRow.TryGetValue("Data", out var snapshotData))
        { logger?.LogError("[STRIPE PURCHASE] failed: AttributionSnapshot not found"); throw new InvalidOperationException("Attribution snapshot unresolved."); }
        var snapshot = JsonSerializer.Deserialize<AttributionSnapshot>(snapshotData.S)!;
        DateTimeOffset? billingPeriodStart = null;
        if (payment.BillingReason == "subscription_update" && payment.Plan is not null)
        {
            Dictionary<string, AttributeValue>? cursor = null;
            var prior = new List<PurchaseRecord>();
            do
            {
                var page = await db.ScanAsync(new ScanRequest
                {
                    TableName = "Purchases", ConsistentRead = true, ExclusiveStartKey = cursor,
                    FilterExpression = "UserId = :user", ExpressionAttributeValues = new() { [":user"] = S(Read("UserId")) }
                });
                prior.AddRange(page.Items.Where(x => x.ContainsKey("Data")).Select(x => JsonSerializer.Deserialize<PurchaseRecord>(x["Data"].S)!)
                    .Where(x => x.StripeSubscriptionId == payment.SubscriptionId && x.PaidAt <= payment.PaidAt));
                cursor = page.LastEvaluatedKey;
            } while (cursor is { Count: > 0 });
            billingPeriodStart = UpgradePeriodStart(prior, payment.PaidAt);
        }
        logger?.LogInformation("[STRIPE PURCHASE] checkout context resolved");
        for (var retry = 0; retry < 4; retry++)
        {
            var customer = await db.GetItemAsync(new GetItemRequest { TableName = "PurchaseCustomers", Key = Key(Read("UserId")), ConsistentRead = true });
            var customerItem = customer.Item ?? new();
            var earlierThanUnclaimed = customerItem.GetValueOrDefault("AcquisitionClaimed")?.BOOL == false
                && DateTimeOffset.TryParse(customerItem.GetValueOrDefault("FirstPaidAt")?.S, out var knownPaidAt) && payment.PaidAt < knownPaidAt;
            var first = payment.BillingReason != "subscription_update" && payment.FirstPaymentCandidate && (customerItem.Count == 0 || earlierThanUnclaimed);
            var record = new PurchaseRecord
            {
                TransactionId = id, CheckoutAttemptId = payment.CheckoutAttemptId, UserId = Read("UserId"), ProjectId = Read("ProjectId"),
                FunnelId = Read("FunnelId"), AcquisitionId = Read("AcquisitionId"), AttributionSnapshotId = Read("AttributionSnapshotId"),
                StripeTransactionId = payment.ProviderTransactionId, StripeEventId = payment.StripeEventId, StripeSubscriptionId = payment.SubscriptionId,
                AmountMinor = payment.AmountMinor, Amount = MeasurementMoney.FromMinor(payment.AmountMinor, payment.Currency), Currency = payment.Currency.ToUpperInvariant(),
                Plan = payment.Plan ?? Read("Plan"), BillingInterval = payment.BillingInterval ?? Read("BillingInterval"), IsFirstPayment = first, IsRenewal = !first && payment.Renewal,
                BillingPeriodStart = billingPeriodStart,
                BillingReason = payment.BillingReason, PaidAt = payment.PaidAt, Snapshot = snapshot
            };
            var item = Key(id);
            item["Data"] = S(JsonSerializer.Serialize(record)); item["TransactionId"] = S(id);
            item["UserId"] = S(record.UserId); item["ProjectId"] = S(record.ProjectId); item["FunnelId"] = S(record.FunnelId);
            item["Amount"] = new() { N = record.Amount.ToString(CultureInfo.InvariantCulture) }; item["Currency"] = S(record.Currency);
            item["PaymentStatus"] = S("Paid"); item["IsFirstPayment"] = new() { BOOL = first }; item["IsRenewal"] = new() { BOOL = record.IsRenewal };
            item["Ga4Status"] = S(snapshot.Measurement.AnalyticsConsent ? "Pending" : "NoConsent");
            item["AdsStatus"] = S(first && snapshot.Measurement.AdvertisingConsent ? "Pending" : "NotEligible");
            var writes = new List<TransactWriteItem> { new() { Put = new() { TableName = "Purchases", Item = item, ConditionExpression = "attribute_not_exists(Id)" } } };
            if (customerItem.Count == 0) writes.Add(new() { Put = new() { TableName = "PurchaseCustomers", Item = new()
                { ["Id"] = S(record.UserId), ["TransactionId"] = S(id), ["FirstPaidAt"] = S(payment.PaidAt.ToString("O")), ["AcquisitionClaimed"] = new() { BOOL = first } }, ConditionExpression = "attribute_not_exists(Id)" } });
            else if (first) writes.Add(new() { Update = new() { TableName = "PurchaseCustomers", Key = Key(record.UserId),
                UpdateExpression = "SET TransactionId = :id, FirstPaidAt = :paid, AcquisitionClaimed = :yes",
                ConditionExpression = "AcquisitionClaimed = :no AND FirstPaidAt > :paid", ExpressionAttributeValues = new()
                { [":id"] = S(id), [":paid"] = S(payment.PaidAt.ToString("O")), [":yes"] = new() { BOOL = true }, [":no"] = new() { BOOL = false } } } });
            try
            {
                logger?.LogInformation("[STRIPE PURCHASE] canonical purchase write started");
                await db.TransactWriteItemsAsync(new() { TransactItems = writes });
                logger?.LogInformation("[STRIPE PURCHASE] canonical purchase created");
                logger?.LogInformation(snapshot.Measurement.AnalyticsConsent
                    ? "[STRIPE PURCHASE] GA4 purchase delivery queued"
                    : "[STRIPE PURCHASE] GA4 purchase delivery not queued: analytics disabled at checkout");
                return record;
            }
            catch (TransactionCanceledException ex)
            {
                existing = await FindAsync(id);
                if (existing is not null) { logger?.LogInformation("[STRIPE PURCHASE] duplicate purchase ignored"); return existing; }
                if (ex.CancellationReasons?.Any(x => x.Code is not (null or "None" or "ConditionalCheckFailed" or "TransactionConflict")) == true)
                { logger?.LogError("[STRIPE PURCHASE] failed: persistence error"); throw; }
            }
        }
        logger?.LogError("[STRIPE PURCHASE] failed: persistence conflict retries exhausted");
        throw new InvalidOperationException("Purchase transaction could not be recorded.");
    }
    public async Task<List<PurchaseRecord>> PendingAsync()
    {
        using var db = clients.CreateClient(); var result = new List<PurchaseRecord>(); Dictionary<string, AttributeValue>? key = null;
        do
        {
            var page = await db.ScanAsync(new ScanRequest { TableName = "Purchases", ExclusiveStartKey = key, FilterExpression = "Ga4Status = :pending OR AdsStatus = :pending", ExpressionAttributeValues = new() { [":pending"] = S("Pending") } });
            result.AddRange((page.Items ?? []).Select(x => JsonSerializer.Deserialize<PurchaseRecord>(x["Data"].S)!)); key = page.LastEvaluatedKey;
        } while (key is { Count: > 0 });
        return result;
    }
    public static DateTimeOffset UpgradePeriodStart(IEnumerable<PurchaseRecord> prior, DateTimeOffset paidAt)
    {
        var previous = prior.Where(x => x.PaymentStatus == "Paid" && x.PaidAt <= paidAt).OrderByDescending(x => x.PaidAt).FirstOrDefault();
        var start = previous?.BillingPeriodStart ?? previous?.PaidAt;
        if (start is null || AIUsageSummaryService.BillingPeriodEnd(start.Value, previous!.BillingInterval) is not { } end || paidAt >= end)
            throw new InvalidOperationException("Upgrade billing period is unresolved.");
        return start.Value;
    }

    public async Task<bool> AcquireAsync(string id, string channel)
    {
        using var db = clients.CreateClient();
        try
        {
            await db.UpdateItemAsync(new() { TableName = "Purchases", Key = Key(id), UpdateExpression = "SET #lease = :lease",
                ConditionExpression = "#status = :pending AND (attribute_not_exists(#lease) OR #lease < :now)",
                ExpressionAttributeNames = new() { ["#status"] = channel + "Status", ["#lease"] = channel + "Lease" },
                ExpressionAttributeValues = new() { [":pending"] = S("Pending"), [":now"] = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }, [":lease"] = new() { N = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString() } } });
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }
    public async Task FinishAsync(string id, string channel, string status)
    {
        using var db = clients.CreateClient();
        await db.UpdateItemAsync(new() { TableName = "Purchases", Key = Key(id), UpdateExpression = "SET #status = :status",
            ExpressionAttributeNames = new() { ["#status"] = channel + "Status" }, ExpressionAttributeValues = new() { [":status"] = S(status) } });
    }
}
