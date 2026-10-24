using Amazon.DynamoDBv2.Model;

namespace DesignMode.AI.Server.Services;

public sealed partial class FunnelRecoveryStore
{
    public async Task<FunnelRecoveryContext?> ResolveRecoveryAsync(FunnelRecoveryState state)
    {
        using var client = _createClient();
        // Existing recovery enrollment proves readiness; no job scan or historical enrollment.
        return await ResolveOwnershipAsync(client, state.UserId, state.FunnelId, state.ProjectId);
    }

    public async IAsyncEnumerable<string> FindCandidatesAsync(DateTimeOffset readyBefore)
    {
        using var client = _createClient();
        // The existing table has only an Id partition key, no due-date GSI.
        // Page through this table only; DynamoDB filters do not reduce read capacity.
        var request = new ScanRequest
        {
            TableName = TableName, Limit = 100, ProjectionExpression = "Id",
            FilterExpression = "attribute_exists(VideoReadyEmailSentAt) AND VideoReadyAt <= :cutoff AND attribute_not_exists(SuppressedAt) AND attribute_not_exists(FinalReminderSentAt)",
            ExpressionAttributeValues = new() { [":cutoff"] = S(readyBefore.ToUniversalTime().ToString("O")) }
        };
        do
        {
            var page = await client.ScanAsync(request);
            foreach (var row in page.Items) yield return Value(row, "Id");
            request.ExclusiveStartKey = page.LastEvaluatedKey;
        } while (request.ExclusiveStartKey is { Count: > 0 });
    }

    public Task SuppressAsync(string id, string reason) => UpdateAsync(id,
        "SET SuppressedAt = if_not_exists(SuppressedAt, :now), SuppressionReason = if_not_exists(SuppressionReason, :reason), UpdatedAt = :now ADD Revision :one",
        "attribute_exists(Id)", new() { [":reason"] = S(reason) });

    private static string SentField(FunnelRecoveryStep step) => step switch
    {
        FunnelRecoveryStep.Reminder1 => "Reminder1SentAt",
        FunnelRecoveryStep.Reminder2 => "Reminder2SentAt",
        FunnelRecoveryStep.FinalReminder => "FinalReminderSentAt",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    public static UpdateItemRequest ReminderClaimRequest(FunnelRecoveryState state, FunnelRecoveryStep step,
        DateTimeOffset now, DateTimeOffset readyBefore)
    {
        var priorCondition = step switch
        {
            FunnelRecoveryStep.Reminder1 => "attribute_not_exists(Reminder2SentAt)",
            FunnelRecoveryStep.Reminder2 => "attribute_exists(Reminder1SentAt) AND Reminder1SentAt <= :spacing",
            FunnelRecoveryStep.FinalReminder => "attribute_exists(Reminder1SentAt) AND attribute_exists(Reminder2SentAt) AND Reminder2SentAt <= :spacing",
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };
        var request = new UpdateItemRequest
        {
            TableName = TableName, Key = Key(state.Id),
            ConditionExpression = $"attribute_exists(VideoReadyEmailSentAt) AND DeliveryStatus = :sent AND attribute_not_exists(SuppressedAt) AND attribute_not_exists(FinalReminderSentAt) AND attribute_not_exists({SentField(step)}) AND attribute_not_exists(ReminderClaim) AND (attribute_not_exists(ReminderAttempts) OR ReminderAttempts < :max) AND (attribute_not_exists(ReminderRetryAt) OR ReminderRetryAt <= :now) AND VideoReadyAt <= :cutoff AND {priorCondition}",
            UpdateExpression = "SET ReminderClaim = :claim, LastEmailAttemptAt = :now, UpdatedAt = :now ADD ReminderAttempts :one, Revision :one",
            ExpressionAttributeValues = new()
            {
                [":sent"] = S("Sent"), [":claim"] = S(state.ReminderClaim ?? throw new ArgumentException("Missing claim.")),
                [":max"] = new() { N = FunnelRecoveryTiming.MaximumAttempts.ToString() }, [":one"] = new() { N = "1" },
                [":now"] = S(now.ToUniversalTime().ToString("O")), [":cutoff"] = S(readyBefore.ToUniversalTime().ToString("O"))
            }
        };
        if (step != FunnelRecoveryStep.Reminder1)
            request.ExpressionAttributeValues[":spacing"] = S((now - FunnelRecoveryTiming.MinimumSpacing).ToUniversalTime().ToString("O"));
        return request;
    }

    public async Task<bool> ClaimReminderAsync(FunnelRecoveryState state, FunnelRecoveryStep step,
        DateTimeOffset now, DateTimeOffset readyBefore)
    {
        using var client = _createClient();
        try { await client.UpdateItemAsync(ReminderClaimRequest(state, step, now, readyBefore)); return true; }
        catch (ConditionalCheckFailedException) { return false; }
    }

    public async Task MarkReminderSentAsync(string id, FunnelRecoveryStep step, string claim, string messageId, DateTimeOffset now)
    {
        using var client = _createClient();
        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = TableName, Key = Key(id),
            ConditionExpression = $"ReminderClaim = :claim AND attribute_not_exists({SentField(step)})",
            // Record accepted delivery even if a suppression arrived during the SES call.
            UpdateExpression = $"SET {SentField(step)} = :now, {step}MessageId = :message, UpdatedAt = :now REMOVE ReminderClaim, ReminderAttempts, ReminderRetryAt ADD Revision :one",
            ExpressionAttributeValues = new()
            {
                [":claim"] = S(claim), [":message"] = S(messageId), [":now"] = S(now.ToUniversalTime().ToString("O")),
                [":one"] = new() { N = "1" }
            }
        });
    }

    public async Task RejectReminderAsync(string id, string claim, DateTimeOffset now)
    {
        using var client = _createClient();
        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = TableName, Key = Key(id), ConditionExpression = "ReminderClaim = :claim",
            UpdateExpression = "SET ReminderRetryAt = :retry, UpdatedAt = :now REMOVE ReminderClaim ADD Revision :one",
            ExpressionAttributeValues = new()
            {
                [":claim"] = S(claim), [":retry"] = S((now + FunnelRecoveryTiming.RetryDelay).ToUniversalTime().ToString("O")),
                [":now"] = S(now.ToUniversalTime().ToString("O")), [":one"] = new() { N = "1" }
            }
        });
    }

    public async Task SuppressSubscribedAsync(string userId, string subscriptionRecordId)
    {
        using var client = _createClient();
        var row = await GetAsync(client, "Users", userId);
        var user = await _findUser(Value(row, "Username"));
        if (user is null || user.Id != userId) return;
        // Strongly read the just-persisted canonical row so webhook suppression
        // does not depend on eventual visibility of a subscription query.
        var subscription = (await client.GetItemAsync(new GetItemRequest
        {
            TableName = "Subscriptions", ConsistentRead = true,
            Key = new() { ["Id"] = S(subscriptionRecordId), ["UserId"] = S(user.Id) }
        })).Item ?? new();
        if (Value(subscription, "UserId") != user.Id || !PostAuthRoutingStore.HasPaidAccess(subscription)) return;
        var request = new ScanRequest
        {
            TableName = TableName, Limit = 100, ProjectionExpression = "Id",
            FilterExpression = "UserId = :user AND attribute_not_exists(SuppressedAt)",
            ExpressionAttributeValues = new() { [":user"] = S(user.Id) }, ConsistentRead = true
        };
        do
        {
            var page = await client.ScanAsync(request);
            foreach (var item in page.Items) await SuppressAsync(Value(item, "Id"), "SUBSCRIBED");
            request.ExclusiveStartKey = page.LastEvaluatedKey;
        } while (request.ExclusiveStartKey is { Count: > 0 });
    }
}
