using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace DesignMode.AI.Server.Services;

public sealed class FunnelRecoveryUnsubscribe : IFunnelRecoveryMarketingPermission
{
    private readonly FunnelRecoveryConfiguration _configuration;
    private readonly IFunnelRecoveryStore _recovery;
    private readonly Func<IAmazonDynamoDB> _createClient;

    public FunnelRecoveryUnsubscribe(FunnelRecoveryConfiguration configuration, IFunnelRecoveryStore recovery)
        : this(configuration, recovery, () => DynamoDbClientFactory.CreateClient()) { }

    public FunnelRecoveryUnsubscribe(FunnelRecoveryConfiguration configuration, IFunnelRecoveryStore recovery,
        Func<IAmazonDynamoDB> createClient)
    {
        _configuration = configuration;
        _recovery = recovery;
        _createClient = createClient;
    }

    private byte[] Sign(string id)
    {
        var secret = _configuration.Get("JwtSecretKey");
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Unsubscribe signing is not configured.");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("recovery-unsubscribe:v1:" + id));
    }

    public string CreateUrl(string? hostUrl, string recoveryId, bool allowLocalLinks = false)
    {
        // Reuse the recovery sender's origin validation. The token contains only an
        // opaque recovery hash and a purpose-specific signature, never email or user ID.
        var resume = FunnelRecoveryEmail.Create(hostUrl, null, allowLocalLinks).ResumeUrl;
        if (recoveryId.Length != 64 || !recoveryId.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid recovery identity.");
        var token = recoveryId + "." + Convert.ToHexString(Sign(recoveryId));
        return new Uri(resume).GetLeftPart(UriPartial.Authority) + "/unsubscribe?token=" + token;
    }

    public bool TryValidate(string? token, out string id)
    {
        id = "";
        if (token is null || token.Length != 129 || token[64] != '.') return false;
        var candidate = token[..64];
        if (!candidate.All(Uri.IsHexDigit) || !token[65..].All(Uri.IsHexDigit)) return false;
        if (!CryptographicOperations.FixedTimeEquals(Sign(candidate), Convert.FromHexString(token[65..]))) return false;
        id = candidate;
        return true;
    }

    public async Task<bool> UnsubscribeAsync(string? token)
    {
        if (!TryValidate(token, out var id)) return false;
        var state = await _recovery.ReadAsync(id);
        if (state is null || string.IsNullOrWhiteSpace(state.UserId)) return false;
        using var client = _createClient();
        try
        {
            await client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = "Users", Key = new() { ["Id"] = new() { S = state.UserId } },
                ConditionExpression = "attribute_exists(Id)",
                UpdateExpression = "SET RecoveryEmailsUnsubscribedAt = if_not_exists(RecoveryEmailsUnsubscribedAt, :now)",
                ExpressionAttributeValues = new() { [":now"] = new() { S = DateTimeOffset.UtcNow.ToString("O") } }
            });
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }

    public async Task<RecoveryMarketingPermission> CheckAsync(string canonicalUserId)
    {
        if (string.IsNullOrWhiteSpace(canonicalUserId)) return RecoveryMarketingPermission.Unavailable;
        using var client = _createClient();
        var user = await client.GetItemAsync(new GetItemRequest
        {
            TableName = "Users", Key = new() { ["Id"] = new() { S = canonicalUserId } },
            ConsistentRead = true, ProjectionExpression = "Id, RecoveryEmailsUnsubscribedAt"
        });
        // Recovery policy: allow existing canonical users unless they unsubscribed.
        // Missing/mismatched records never establish permission. Read failures propagate
        // to the scheduler, which skips delivery and retries evaluation on a later run.
        if (user.Item is null || !user.Item.TryGetValue("Id", out var id) ||
            !string.Equals(id.S, canonicalUserId, StringComparison.Ordinal))
            return RecoveryMarketingPermission.Unavailable;
        return user.Item.ContainsKey("RecoveryEmailsUnsubscribedAt")
            ? RecoveryMarketingPermission.Denied : RecoveryMarketingPermission.Allowed;
    }
}
