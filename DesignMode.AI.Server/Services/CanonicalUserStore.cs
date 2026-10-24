using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace DesignMode.AI.Server.Services;

public sealed class CanonicalUserStore
{
    private readonly AttributionStore _attributionStore;
    private readonly ILogger<CanonicalUserStore> _logger;

    public CanonicalUserStore(AttributionStore attributionStore, ILogger<CanonicalUserStore> logger)
    {
        _attributionStore = attributionStore;
        _logger = logger;
    }

    public sealed record CanonicalUser(string Id, string Username, string? PasswordHash = null, string? Salt = null, string? CurrentPage = null);

    public async Task<CanonicalUser?> FindByEmailAsync(string email, bool includePassword = false)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return null;

        using var client = _attributionStore.CreateClient();
        var fields = includePassword ? "Id, Username, PasswordHash, Salt, CurrentPage" : "Id, Username, CurrentPage";

        var exact = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT {fields} FROM Users WHERE Username = ?",
            ConsistentRead = true,
            Parameters = [new AttributeValue { S = normalized }]
        });
        var exactUser = ToUser(exact.Items?.FirstOrDefault());
        if (exactUser is not null) return exactUser;

        // Compatibility path for legacy mixed-case or whitespace usernames. This
        // scan runs only after the canonical indexed/PartiQL lookup misses.
        var legacy = await client.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT {fields} FROM Users",
            ConsistentRead = true
        });
        var matches = legacy.Items
            .Select(ToUser)
            .Where(user => user is not null && NormalizeEmail(user.Username) == normalized)
            .Cast<CanonicalUser>()
            .Take(2)
            .ToList();

        if (matches.Count > 1)
            throw new InvalidOperationException("Multiple canonical users match the normalized identity.");
        return matches.SingleOrDefault();
    }

    public async Task<CanonicalUser> FindOrCreateGoogleUserAsync(string email)
    {
        var normalized = NormalizeEmail(email) ?? throw new ArgumentException("A valid Google email is required.", nameof(email));
        _logger.LogInformation("[AUTH] Looking up canonical user");
        var existing = await FindByEmailAsync(normalized);
        if (existing is not null)
        {
            _logger.LogInformation("[AUTH] Existing user found");
            return existing;
        }

        _logger.LogInformation("[AUTH] Canonical user not found");
        _logger.LogInformation("[AUTH] Creating canonical user");
        var userId = UserIdForEmail(normalized);
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var client = _attributionStore.CreateClient();
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = "Users",
                Item = new()
                {
                    ["Id"] = new() { S = userId },
                    ["Username"] = new() { S = normalized },
                    ["NormalizedUsername"] = new() { S = normalized },
                    ["Onboarded"] = new() { S = "0" },
                    ["AuthenticationProvider"] = new() { S = "Google" },
                    ["CreatedDate"] = new() { S = now }
                },
                ConditionExpression = "attribute_not_exists(Id)"
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // Concurrent Google signups use the same opaque deterministic UUID.
            var concurrent = await client.GetItemAsync("Users", new() { ["Id"] = new() { S = userId } });
            var concurrentUser = ToUser(concurrent.Item);
            if (concurrentUser is null || NormalizeEmail(concurrentUser.Username) != normalized) throw;
            _logger.LogInformation("[AUTH] Existing user found");
            return concurrentUser;
        }

        var persisted = await client.GetItemAsync("Users", new() { ["Id"] = new() { S = userId } });
        var created = ToUser(persisted.Item);
        if (created is null || NormalizeEmail(created.Username) != normalized)
            throw new InvalidOperationException("Canonical Google user was not persisted.");
        _logger.LogInformation("[AUTH] Canonical user created and verified");
        return created;
    }

    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static CanonicalUser? ToUser(Dictionary<string, AttributeValue>? item)
    {
        if (item is null || !item.TryGetValue("Id", out var id) || string.IsNullOrWhiteSpace(id.S) ||
            !item.TryGetValue("Username", out var username) || string.IsNullOrWhiteSpace(username.S)) return null;
        return new CanonicalUser(id.S, username.S, item.GetValueOrDefault("PasswordHash")?.S, item.GetValueOrDefault("Salt")?.S, item.GetValueOrDefault("CurrentPage")?.S);
    }

    public static string UserIdForEmail(string normalizedEmail)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"DesignMode.GoogleIdentity.v1:{normalizedEmail}"))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes).ToString();
    }
}
