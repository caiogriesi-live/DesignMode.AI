
using Amazon.DynamoDBv2.Model;

namespace DesignMode.AI.Server.Services;

public sealed class FunnelEmailTrackingService
{
    public async Task TrackAsync(
        string recoveryId,
        string campaignId,
        string variant,
        string eventType,
        string? projectId = null,
        string? funnelId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recoveryId) ||
            string.IsNullOrWhiteSpace(campaignId) ||
            string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Tracking identifiers are required.");

        using var db = DynamoDbClientFactory.CreateClient();

        string now = DateTimeOffset.UtcNow.ToString("O");

        string eventId =
            $"{now}#{Guid.NewGuid():N}";

        await db.PutItemAsync(new PutItemRequest
        {
            TableName = "FunnelEmailEvents",
            Item = new()
            {
                ["RecoveryId"] = new() { S = recoveryId },
                ["EventId"] = new() { S = eventId },
                ["CampaignId"] = new() { S = campaignId },
                ["Variant"] = new() { S = variant },
                ["EventType"] = new() { S = eventType },
                ["ProjectId"] = new() { S = projectId ?? "" },
                ["FunnelId"] = new() { S = funnelId ?? "" },
                ["OccurredAtUtc"] = new() { S = now }
            }
        }, ct);
    }
}
