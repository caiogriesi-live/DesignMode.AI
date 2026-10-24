using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models;
using System.Globalization;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

// One user wallet plus immutable purchase/reservation identities. No TTL: holds must
// be reconciled against provider outcomes, never released while work may still run.
public sealed class AICreditLedger
{
    public const string Table = "AICreditLedger";
    private readonly Func<IAmazonDynamoDB> _clients;
    public bool Enabled { get; init; } = true;
    public string TableName { get; init; } = Table;
    public AICreditLedger(AttributionStore clients) : this(() => clients.CreateClient()) { }
    public AICreditLedger(Func<IAmazonDynamoDB> clients) => _clients = clients;
    public sealed record Wallet(decimal Purchased = 0, string Period = "", decimal BaseUsed = 0, long Revision = 0);
    public sealed record Attempt(string Id, string UserId, string PackageId, bool IsTest, DateTimeOffset CreatedAt, string? PurchaseId = null);
    public sealed record Reservation(string Id, string UserId, string Period, decimal BaseUnits, decimal PurchasedUnits, string Status = "Reserved")
    {
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    }
    public static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(decimal value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    public static Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = S(id) };
    private static Dictionary<string, AttributeValue> Item<T>(string id, T value) => new() { ["Id"] = S(id), ["Data"] = S(JsonSerializer.Serialize(value)) };
    private async Task<T?> ReadAsync<T>(IAmazonDynamoDB db, string id, string? table = null) where T : class
    {
        var result = await db.GetItemAsync(new GetItemRequest { TableName = table ?? TableName, Key = Key(id), ConsistentRead = true });
        return result.Item?.TryGetValue("Data", out var data) == true ? JsonSerializer.Deserialize<T>(data.S) : null;
    }
    public async Task<Wallet> GetWalletAsync(string userId)
    {
        using var db = _clients();
        return await ReadAsync<Wallet>(db, "wallet:" + userId) ?? new();
    }
    public async Task<Attempt> CreateAttemptAsync(string userId, CreditPackage package, bool test)
    {
        var attempt = new Attempt("credit_" + Guid.NewGuid().ToString("N"), userId, package.Id, test, DateTimeOffset.UtcNow);
        using var db = _clients();
        await db.PutItemAsync(new PutItemRequest { TableName = TableName, Item = Item(attempt.Id, attempt), ConditionExpression = "attribute_not_exists(Id)" });
        return attempt;
    }
    public async Task<Attempt?> GetAttemptAsync(string id)
    {
        using var db = _clients();
        return await ReadAsync<Attempt>(db, id);
    }
    public async Task<PurchaseRecord?> GetPurchaseAsync(string id)
    {
        using var db = _clients();
        return await ReadAsync<PurchaseRecord>(db, id, "Purchases");
    }
    private TransactWriteItem WriteWallet(string userId, Wallet old, Wallet next)
    {
        var item = Item("wallet:" + userId, next with { Revision = old.Revision + 1 });
        item["Revision"] = N(old.Revision + 1);
        return new() { Put = new() { TableName = TableName, Item = item,
            ConditionExpression = old.Revision == 0 ? "attribute_not_exists(Id)" : "Revision = :revision",
            ExpressionAttributeValues = old.Revision == 0 ? null : new() { [":revision"] = N(old.Revision) } } };
    }
    private static bool Conflict(TransactionCanceledException ex) => ex.CancellationReasons is { Count: > 0 } &&
        ex.CancellationReasons.All(x => x.Code is null or "None" or "ConditionalCheckFailed" or "TransactionConflict");

    // Purchase and entitlement commit together. A retry cannot leave one without the other.
    public async Task GrantAsync(Attempt attempt, CreditPackage pack, string paymentIntent, string sessionId, string eventId, DateTimeOffset paidAt, decimal revoked = 0)
    {
        using var db = _clients();
        var id = PurchaseMeasurementStore.TransactionId(paymentIntent);
        for (var retry = 0; retry < 12; retry++)
        {
            if (await ReadAsync<PurchaseRecord>(db, id, "Purchases") is not null) return;
            var currentAttempt = await ReadAsync<Attempt>(db, attempt.Id) ?? throw new InvalidOperationException("Unknown credit checkout.");
            // A reopened Payment Link may create a second paid session. Each distinct
            // PaymentIntent is fulfilled once for the same server-bound owner.
            var wallet = await ReadAsync<Wallet>(db, "wallet:" + attempt.UserId) ?? new();
            var purchase = new PurchaseRecord { TransactionId = id, UserId = attempt.UserId, CheckoutAttemptId = attempt.Id,
                StripeTransactionId = paymentIntent, StripeEventId = eventId, StripeCheckoutSessionId = sessionId,
                PurchaseType = "AICredits", CreditPackageId = pack.Id, CreditsGranted = pack.Credits, CreditsRevoked = revoked, IsTestCreditPurchase = attempt.IsTest,
                AmountMinor = pack.AmountMinor, Amount = pack.AmountMinor / 100m, Currency = "USD", PaidAt = paidAt,
                BillingReason = "ai_credit_topup", IsFirstPayment = false, IsRenewal = false };
            var item = Item(id, purchase);
            item["UserId"] = S(attempt.UserId); item["PurchaseType"] = S("AICredits");
            item["TransactionId"] = S(id); item["Amount"] = N(purchase.Amount); item["Currency"] = S(purchase.Currency);
            item["PaymentStatus"] = S("Paid"); item["IsFirstPayment"] = new() { BOOL = false }; item["IsRenewal"] = new() { BOOL = false };
            item["Ga4Status"] = S("NotEligible"); item["AdsStatus"] = S("NotEligible"); item["Revision"] = N(0);
            try
            {
                await db.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [
                    WriteWallet(attempt.UserId, wallet, wallet with { Purchased = wallet.Purchased + pack.Credits - revoked }),
                    new() { Put = new() { TableName = "Purchases", Item = item, ConditionExpression = "attribute_not_exists(Id)" } },
                    new() { Put = new() { TableName = TableName, Item = Item(attempt.Id, attempt with { PurchaseId = id }),
                        ConditionExpression = "#data = :previous", ExpressionAttributeNames = new() { ["#data"] = "Data" },
                        ExpressionAttributeValues = new() { [":previous"] = S(JsonSerializer.Serialize(currentAttempt)) } } }
                ] });
                return;
            }
            catch (TransactionCanceledException ex) when (Conflict(ex)) { }
        }
        throw new InvalidOperationException("Credit purchase contention; retry webhook.");
    }

    public static (decimal Base, decimal Purchased) Allocate(decimal availableBase, decimal purchased, decimal units)
    {
        if (units <= 0 || purchased < 0 || availableBase + purchased < units) throw new AICreditQuotaException();
        var fromBase = Math.Min(Math.Max(0, availableBase), units);
        return (fromBase, units - fromBase);
    }

    public async Task ReserveAsync(AIGenerationUsage usage, AIUsageSummary summary)
    {
        using var db = _clients();
        var period = summary.BillingPeriodStart.ToString("O");
        var reservationId = "reservation:" + usage.Id;
        // User + job + feature prevents duplicate positive-unit operations from spending twice.
        var jobId = "job:" + PurchaseMeasurementStore.TransactionId($"{usage.UserId}:{usage.GenerationJobId}:{usage.Feature}");
        for (var retry = 0; retry < 12; retry++)
        {
            if (await ReadAsync<Reservation>(db, reservationId) is not null || await ReadAsync<Reservation>(db, jobId) is not null)
                throw new AICreditDuplicateGenerationException();
            var old = await ReadAsync<Wallet>(db, "wallet:" + usage.UserId) ?? new();
            // An operation that raced a renewal must never roll the wallet back and
            // reset the new allowance a second time. Credit-only access keeps the
            // latest known period without manufacturing an allowance.
            if (DateTimeOffset.TryParse(old.Period, out var walletPeriod) && walletPeriod > summary.BillingPeriodStart)
            {
                if (summary.GenerationLimit > 0) throw new InvalidOperationException("Billing period changed; retry generation.");
                period = old.Period;
            }
            var used = old.Period == period ? old.BaseUsed : Math.Min(summary.GenerationLimit ?? 0, summary.GenerationsUsed);
            var allocation = Allocate(Math.Max(0, (summary.GenerationLimit ?? 0) - used), old.Purchased, usage.GenerationUnits!.Value);
            var reservation = new Reservation(reservationId, usage.UserId!, period, allocation.Base, allocation.Purchased);
            try
            {
                await db.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [
                    WriteWallet(usage.UserId!, old, old with { Period = period, BaseUsed = used + allocation.Base, Purchased = old.Purchased - allocation.Purchased }),
                    new() { Put = new() { TableName = TableName, Item = Item(reservationId, reservation), ConditionExpression = "attribute_not_exists(Id)" } },
                    new() { Put = new() { TableName = TableName, Item = Item(jobId, reservation), ConditionExpression = "attribute_not_exists(Id)" } }
                ] });
                return;
            }
            catch (TransactionCanceledException ex) when (Conflict(ex)) { }
        }
        throw new InvalidOperationException("Credit reservation contention.");
    }

    public async Task RequireFundedJobAsync(string userId, string job, string feature)
    {
        using var db = _clients();
        var jobId = "job:" + PurchaseMeasurementStore.TransactionId($"{userId}:{job}:{feature}");
        var pointer = await ReadAsync<Reservation>(db, jobId);
        var reservation = pointer is null ? null : await ReadAsync<Reservation>(db, pointer.Id);
        if (reservation is null || reservation.UserId != userId || reservation.Status == "Released")
            throw new AICreditQuotaException();
    }

    public async Task CompleteAsync(string usageId, bool success)
    {
        using var db = _clients();
        var id = "reservation:" + usageId;
        for (var retry = 0; retry < 12; retry++)
        {
            var reservation = await ReadAsync<Reservation>(db, id);
            if (reservation is null || reservation.Status != "Reserved") return;
            var old = await ReadAsync<Wallet>(db, "wallet:" + reservation.UserId) ?? throw new InvalidOperationException("Missing credit wallet.");
            var next = success ? old : old with { Purchased = old.Purchased + reservation.PurchasedUnits,
                BaseUsed = old.BaseUsed - (old.Period == reservation.Period ? reservation.BaseUnits : 0) };
            try
            {
                await db.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [
                    WriteWallet(reservation.UserId, old, next),
                    new() { Put = new() { TableName = TableName, Item = Item(id, reservation with { Status = success ? "Consumed" : "Released" }),
                        ConditionExpression = "#data = :previous", ExpressionAttributeNames = new() { ["#data"] = "Data" },
                        ExpressionAttributeValues = new() { [":previous"] = S(JsonSerializer.Serialize(reservation)) } } }
                ] });
                return;
            }
            catch (TransactionCanceledException ex) when (Conflict(ex)) { }
        }
        throw new InvalidOperationException("Credit completion contention.");
    }

    // Cumulative revocation is monotonic. Negative balances retain debt after spent
    // credits are refunded. Dispute wins need audited manual reinstatement.
    public async Task RevokeAsync(string paymentIntent, long refundedMinor, bool disputed)
    {
        using var db = _clients();
        var id = PurchaseMeasurementStore.TransactionId(paymentIntent);
        for (var retry = 0; retry < 12; retry++)
        {
            var purchase = await ReadAsync<PurchaseRecord>(db, id, "Purchases");
            if (purchase?.PurchaseType != "AICredits") return;
            var revoked = disputed ? purchase.CreditsGranted : Math.Min(purchase.CreditsGranted,
                decimal.Ceiling(purchase.CreditsGranted * refundedMinor / purchase.AmountMinor * 100m) / 100m);
            if (revoked <= purchase.CreditsRevoked) return;
            var old = await ReadAsync<Wallet>(db, "wallet:" + purchase.UserId) ?? new();
            var next = purchase with { CreditsRevoked = revoked };
            try
            {
                await db.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [
                    WriteWallet(purchase.UserId, old, old with { Purchased = old.Purchased - (revoked - purchase.CreditsRevoked) }),
                    new() { Update = new() { TableName = "Purchases", Key = Key(id), UpdateExpression = "SET #data = :next",
                        ConditionExpression = "#data = :previous", ExpressionAttributeNames = new() { ["#data"] = "Data" },
                        ExpressionAttributeValues = new() { [":next"] = S(JsonSerializer.Serialize(next)), [":previous"] = S(JsonSerializer.Serialize(purchase)) } } }
                ] });
                return;
            }
            catch (TransactionCanceledException ex) when (Conflict(ex)) { }
        }
        throw new InvalidOperationException("Credit reversal contention; retry webhook.");
    }
}

public sealed class AICreditQuotaException : Exception { }
public sealed class AICreditDuplicateGenerationException : Exception { }
