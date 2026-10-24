using Amazon.DynamoDBv2.Model;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesignMode.AI.Server.Services;

public static class AdvertisingConsentPolicy
{
    public const string SchemaVersion = "1";
    public const string LegacyPolicyVersion = "advertising-measurement-2026-08-31";
    // Explicit Google storage/user-data choice, NOT the internal always-on product policy.
    // Prior policy evidence is retained but cannot silently grant this newly described purpose.
    public const string PolicyVersion = "google-advertising-consent-2026-08-31";
    public static bool Supported(AdvertisingConsentRecord? value) => value is not null
        && value.ConsentVersion == SchemaVersion && value.PolicyVersion == PolicyVersion;
}

public sealed record AdvertisingConsentRecord
{
    public string Id { get; init; } = "";
    public string SubjectType { get; init; } = "Visitor";
    // Server-issued consent visitor ID, deliberately NOT the client-declared attribution UUID.
    public string VisitorId { get; init; } = "";
    public string? UserId { get; init; }
    public bool AdvertisingConsent { get; init; }
    public string ConsentVersion { get; init; } = AdvertisingConsentPolicy.SchemaVersion;
    public string PolicyVersion { get; init; } = AdvertisingConsentPolicy.PolicyVersion;
    public string Source { get; init; } = "default";
    public long Revision { get; init; }
    public long LastWithdrawalRevision { get; init; }
    public DateTimeOffset? GrantedAt { get; init; }
    public DateTimeOffset? WithdrawnAt { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AdvertisingConsentEvidence(string SubjectId, long Revision, string ConsentVersion, string PolicyVersion);
public sealed class ConsentAccessException : Exception;
public sealed class ConsentConflictException : Exception;
public interface IAdvertisingConsentReader
{
    Task<AdvertisingConsentRecord?> CurrentUserAsync(string userId);
}

public sealed class AdvertisingConsentStore : IAdvertisingConsentReader
{
    public const string TableName = "AdvertisingConsents";
    private static AttributeValue S(string value) => new() { S = value };
    private static Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = S(id) };
    private static Dictionary<string, AttributeValue> Item(string id, AdvertisingConsentRecord row) => new()
    { ["Id"] = S(id), ["Data"] = S(JsonSerializer.Serialize(row)), ["Revision"] = new() { N = row.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
    private static string VisitorKey(string token)
    {
        if (!Regex.IsMatch(token, @"\A[0-9a-f]{64}\z")) throw new ConsentAccessException();
        return "v:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
    public async Task<AdvertisingConsentRecord?> FindAsync(string key)
    {
        using var db = DynamoDbClientFactory.CreateClient();
        var response = await db.GetItemAsync(new GetItemRequest { TableName = TableName, Key = Key(key), ConsistentRead = true });
        return response.Item?.TryGetValue("Data", out var data) == true ? JsonSerializer.Deserialize<AdvertisingConsentRecord>(data.S) : null;
    }
    public Task<AdvertisingConsentRecord?> CurrentUserAsync(string userId) => FindAsync("u:" + userId);
    public async Task<(string Token, AdvertisingConsentRecord State)> CreateVisitorAsync()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var row = new AdvertisingConsentRecord { Id = VisitorKey(token), VisitorId = Guid.NewGuid().ToString(), RecordedAt = now, UpdatedAt = now };
        using var db = DynamoDbClientFactory.CreateClient();
        await db.PutItemAsync(new PutItemRequest { TableName = TableName, Item = Item(row.Id, row), ConditionExpression = "attribute_not_exists(Id)" });
        return (token, row);
    }
    private static Put Conditional(AdvertisingConsentRecord before, AdvertisingConsentRecord after) => new()
    {
        TableName = TableName, Item = Item(after.Id, after), ConditionExpression = "Revision = :revision",
        ExpressionAttributeValues = new() { [":revision"] = new() { N = before.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
    };
    private static TransactWriteItem Audit(AdvertisingConsentRecord row, string command) => new()
    { Put = new() { TableName = TableName, Item = Item(row.Id + ":e:" + command, row), ConditionExpression = "attribute_not_exists(Id)" } };

    // userId is supplied ONLY after JWT validation and canonical Users resolution at the API boundary.
    public async Task<AdvertisingConsentRecord> ResolveAsync(string token, string? userId)
    {
        var visitor = await FindAsync(VisitorKey(token)) ?? throw new ConsentAccessException();
        if (visitor.UserId is not null && visitor.UserId != userId) throw new ConsentAccessException();
        if (userId is null) return visitor;
        var current = await CurrentUserAsync(userId);
        if (visitor.UserId == userId) return current ?? throw new ConsentConflictException();
        var now = DateTimeOffset.UtcNow;
        var bound = visitor with { UserId = userId, Revision = visitor.Revision + 1, UpdatedAt = now, Source = "authenticated-binding" };
        // Existing account preferences always win over a new browser. Never overwrite them with an anonymous grant.
        var account = current ?? new AdvertisingConsentRecord
        {
            Id = "u:" + userId, SubjectType = "User", UserId = userId, VisitorId = visitor.VisitorId,
            AdvertisingConsent = AdvertisingConsentPolicy.Supported(visitor) && visitor.AdvertisingConsent,
            Source = "authenticated-binding", Revision = 1, RecordedAt = now, UpdatedAt = now,
            GrantedAt = visitor.GrantedAt, WithdrawnAt = visitor.WithdrawnAt,
            LastWithdrawalRevision = visitor.WithdrawnAt is null ? 0 : 1
        };
        var writes = new List<TransactWriteItem> { new() { Put = Conditional(visitor, bound) }, Audit(bound, "bind") };
        if (current is null)
        {
            writes.Add(new() { Put = new() { TableName = TableName, Item = Item(account.Id, account), ConditionExpression = "attribute_not_exists(Id)" } });
            writes.Add(Audit(account, "initial-binding"));
        }
        using var db = DynamoDbClientFactory.CreateClient();
        try { await db.TransactWriteItemsAsync(new() { TransactItems = writes }); }
        catch (TransactionCanceledException) { throw new ConsentConflictException(); }
        return account;
    }
    public async Task<AdvertisingConsentRecord> RecordAsync(string token, string? userId, bool granted, string command, long expectedRevision,
        string schema, string policy)
    {
        if (!Guid.TryParseExact(command, "D", out _) || schema != AdvertisingConsentPolicy.SchemaVersion || policy != AdvertisingConsentPolicy.PolicyVersion)
            throw new ConsentConflictException();
        var current = await ResolveAsync(token, userId);
        var replay = await FindAsync(current.Id + ":e:" + command);
        if (replay is not null)
        {
            if (replay.AdvertisingConsent != granted) throw new ConsentConflictException();
            return current; // A replay must never restore a superseded grant.
        }
        if (current.Revision != expectedRevision) throw new ConsentConflictException();
        var now = DateTimeOffset.UtcNow; var nextRevision = checked(current.Revision + 1);
        var next = current with
        {
            AdvertisingConsent = granted, ConsentVersion = schema, PolicyVersion = policy, Source = "preferences",
            Revision = nextRevision, RecordedAt = now, UpdatedAt = now,
            GrantedAt = granted ? now : current.GrantedAt, WithdrawnAt = granted ? current.WithdrawnAt : now,
            LastWithdrawalRevision = granted ? current.LastWithdrawalRevision : nextRevision
        };
        using var db = DynamoDbClientFactory.CreateClient();
        try { await db.TransactWriteItemsAsync(new() { TransactItems = [new() { Put = Conditional(current, next) }, Audit(next, command)] }); }
        catch (TransactionCanceledException) { throw new ConsentConflictException(); }
        return next;
    }
}
