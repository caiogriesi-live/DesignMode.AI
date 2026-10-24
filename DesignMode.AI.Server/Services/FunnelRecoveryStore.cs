using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Navigation;

namespace DesignMode.AI.Server.Services;

public sealed partial class FunnelRecoveryStore : IFunnelRecoveryStore, IFunnelRecoverySequenceStore
{
    public const string TableName = "FunnelRecovery";
    private readonly Func<IAmazonDynamoDB> _createClient;
    private readonly Func<string, Task<CanonicalUserStore.CanonicalUser?>> _findUser;
    private readonly IPostAuthRoutingStore _routing;

    public FunnelRecoveryStore(CanonicalUserStore users, IPostAuthRoutingStore routing)
        : this(() => DynamoDbClientFactory.CreateClient(), email => users.FindByEmailAsync(email), routing) { }

    public FunnelRecoveryStore(Func<IAmazonDynamoDB> createClient,
        Func<string, Task<CanonicalUserStore.CanonicalUser?>> findUser, IPostAuthRoutingStore routing)
    {
        _createClient = createClient;
        _findUser = findUser;
        _routing = routing;
    }

    private static AttributeValue S(string value) => new() { S = value };
    private static Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = S(id) };
    private static string Value(Dictionary<string, AttributeValue> row, string name) => row.GetValueOrDefault(name)?.S ?? "";
    private static DateTimeOffset? Date(Dictionary<string, AttributeValue> row, string name) =>
        DateTimeOffset.TryParse(Value(row, name), out var date) ? date : null;

    private static async Task<Dictionary<string, AttributeValue>> GetAsync(IAmazonDynamoDB client,
        string table, string id, string key = "Id") => (await client.GetItemAsync(new GetItemRequest
        {
            TableName = table, Key = new() { [key] = S(id) }, ConsistentRead = true
        })).Item ?? new();

    public async Task<FunnelRecoveryContext?> ResolveAsync(string jobId)
    {
        using var client = _createClient();
        var job = await GetAsync(client, Environment.GetEnvironmentVariable("DynamoDbTableName") ?? "DesignModeJobs", jobId, "JobId");
        // New jobs carry the existing generation path's kind. Legacy jobs and
        // website previews cannot be enrolled by a delayed/replayed completion.
        if (Value(job, "PreviewKind") != "Social" || Value(job, "Status") != "Completed" ||
            string.IsNullOrWhiteSpace(Value(job, "VideoUrl"))) return null;
        var projectId = Value(job, "ProjectId");
        var funnelId = Value(job, "FunnelId");
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(funnelId)) return null;
        return await ResolveOwnershipAsync(client, Value(job, "UserId"), funnelId, projectId);
    }

    private async Task<FunnelRecoveryContext?> ResolveOwnershipAsync(IAmazonDynamoDB client,
        string expectedUserId, string funnelId, string projectId)
    {
        var project = await GetAsync(client, "Projects", projectId);
        var funnel = await GetAsync(client, "Funnels", funnelId);
        // Identity comes from both canonical ownership records, never message/browser identity.
        var userId = Value(project, "UserId");
        if (string.IsNullOrWhiteSpace(userId) || Value(funnel, "UserId") != userId ||
            expectedUserId != userId || Value(funnel, "ProjectId") != projectId ||
            Value(project, "OriginatingFunnelId") != funnelId ||
            project.GetValueOrDefault("IsOriginalAcquisitionProject")?.BOOL != true) return null;
        var row = await GetAsync(client, "Users", userId);
        var user = await _findUser(Value(row, "Username"));
        if (user is null || user.Id != userId) return null;
        var resume = await _routing.FindUserAsync(user.Username);
        if (resume is null || resume.Id != userId) return null;
        var paid = await _routing.HasPaidSubscriptionAsync(userId);
        // Authenticated is the existing acquisition state written by BindWithResultAsync.
        // Unknown/advanced states fail closed; no new funnel-completion flag is introduced.
        var unfinished = Value(funnel, "Status") == "Authenticated" &&
            FunnelRecoveryService.IsUnfinished(user.CurrentPage);
        var selectedImageUrl = await FunnelRecoverySelectedImage.ResolveAsync(client, userId, project);
        return new(userId, funnelId, projectId, user.Username, resume.CurrentPage, true, paid, unfinished, selectedImageUrl);
    }

    public async Task<FunnelRecoveryState> RecordReadyAsync(FunnelRecoveryContext context, DateTimeOffset readyAt)
    {
        var id = FunnelRecoveryService.RecoveryId(context);
        using var client = _createClient();
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = TableName, ConditionExpression = "attribute_not_exists(Id)",
                Item = new()
                {
                    ["Id"] = S(id), ["UserId"] = S(context.UserId), ["FunnelId"] = S(context.FunnelId),
                    ["ProjectId"] = S(context.ProjectId), ["VideoReadyAt"] = S(readyAt.ToString("O")),
                    ["CreatedAt"] = S(now), ["UpdatedAt"] = S(now), ["DeliveryStatus"] = S("Ready"),
                    ["Attempts"] = new() { N = "0" }, ["Revision"] = new() { N = "0" }
                }
            });
        }
        catch (ConditionalCheckFailedException) { }
        return await ReadAsync(id) ?? throw new InvalidOperationException("Recovery state missing.");
    }

    public async Task<FunnelRecoveryState?> ReadAsync(string id)
    {
        using var client = _createClient();
        var row = await GetAsync(client, TableName, id);
        if (row.Count == 0) return null;
        return new(id, Value(row, "UserId"), Value(row, "FunnelId"), Value(row, "ProjectId"),
            Date(row, "VideoReadyAt") ?? throw new InvalidOperationException("Missing video readiness."),
            Date(row, "VideoReadyEmailSentAt"), Date(row, "SuppressedAt"), Value(row, "DeliveryStatus"),
            int.Parse(row["Attempts"].N), Date(row, "Reminder1SentAt"), Date(row, "Reminder2SentAt"),
            Date(row, "FinalReminderSentAt"), Value(row, "SuppressionReason"), Value(row, "ReminderClaim"),
            int.TryParse(row.GetValueOrDefault("ReminderAttempts")?.N, out var attempts) ? attempts : 0,
            Date(row, "ReminderRetryAt"));
    }

    public static UpdateItemRequest ClaimRequest(string id) => new()
    {
        TableName = TableName, Key = Key(id),
        ConditionExpression = "attribute_exists(VideoReadyAt) AND attribute_not_exists(VideoReadyEmailSentAt) AND attribute_not_exists(SuppressedAt) AND DeliveryStatus = :ready AND Attempts < :max",
        UpdateExpression = "SET DeliveryStatus = :sending, UpdatedAt = :now ADD Attempts :one, Revision :one",
        ExpressionAttributeValues = new()
        {
            [":ready"] = S("Ready"), [":sending"] = S("Sending"), [":max"] = new() { N = "3" },
            [":one"] = new() { N = "1" }, [":now"] = S(DateTimeOffset.UtcNow.ToString("O"))
        }
    };

    public async Task<bool> ClaimAsync(string id)
    {
        using var client = _createClient();
        try { await client.UpdateItemAsync(ClaimRequest(id)); return true; }
        catch (ConditionalCheckFailedException) { return false; }
    }

    public Task SuppressAsync(string id) => UpdateAsync(id,
        "SET SuppressedAt = if_not_exists(SuppressedAt, :now), UpdatedAt = :now ADD Revision :one",
        "attribute_exists(Id)", new());

    public Task MarkSentAsync(string id, string messageId) => UpdateAsync(id,
        "SET VideoReadyEmailSentAt = :now, VideoReadyEmailMessageId = :message, DeliveryStatus = :sent, UpdatedAt = :now ADD Revision :one",
        "DeliveryStatus = :sending AND attribute_not_exists(VideoReadyEmailSentAt)",
        new() { [":message"] = S(messageId), [":sent"] = S("Sent"), [":sending"] = S("Sending") });

    public Task RejectAsync(string id) => UpdateAsync(id,
        "SET DeliveryStatus = :ready, UpdatedAt = :now ADD Revision :one", "DeliveryStatus = :sending",
        new() { [":ready"] = S("Ready"), [":sending"] = S("Sending") });

    private async Task UpdateAsync(string id, string update, string condition, Dictionary<string, AttributeValue> values)
    {
        using var client = _createClient();
        values[":now"] = S(DateTimeOffset.UtcNow.ToString("O"));
        values[":one"] = new() { N = "1" };
        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = TableName, Key = Key(id), UpdateExpression = update,
            ConditionExpression = condition, ExpressionAttributeValues = values
        });
    }
}
