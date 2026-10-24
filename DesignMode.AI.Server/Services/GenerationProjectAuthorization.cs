using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models.Attribution;

namespace DesignMode.AI.Server.Services;

/// <summary>Read-only paid-workspace authorization. Acquisition Funnels are not ownership records.</summary>
public static class GenerationProjectAuthorization
{
    public static async Task<ProjectContextResponse?> ResolveCompanyIdeaAsync(
        IAmazonDynamoDB client, string userId, string companyIdeaId)
    {
        var prompts = await OwnedPromptsAsync(client, userId, companyIdeaId);
        if (prompts.Count == 0) return null;

        // Preserve the accounting identity of projects already mapped to this legacy CID.
        var request = new ExecuteStatementRequest
        {
            Statement = "SELECT Id, UserId, OriginatingFunnelId, AcquisitionId FROM Projects WHERE LegacyCompanyIdeaId = ?",
            Parameters = [new() { S = companyIdeaId }], ConsistentRead = true
        };
        ProjectContextResponse? context = null;
        do
        {
            var page = await client.ExecuteStatementAsync(request);
            foreach (var row in page.Items ?? [])
            {
                if (row.GetValueOrDefault("UserId")?.S != userId) return null;
                var id = row.GetValueOrDefault("Id")?.S;
                if (string.IsNullOrWhiteSpace(id)) return null;
                if (context is not null && context.ProjectId != id) return null;
                context = Context(id, row);
            }
            request.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));

        if (context is not null) return context;

        // Older acquisition projects only saved LegacyCompanyIndustryId. Their
        // checkout still persisted the exact InviteId -> ProjectId relationship.
        // Follow that server-side link from an owned Prompt; never infer a project
        // from a browser ID, company name, or the user's most recent subscription.
        var inviteIds = prompts.Select(row => row.GetValueOrDefault("InviteId")?.S)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal);
        foreach (var inviteId in inviteIds)
        {
            var subscriptions = new ExecuteStatementRequest
            {
                Statement = "SELECT UserId, InviteId, ProjectId, FunnelId FROM Subscriptions WHERE InviteId = ? AND UserId = ?",
                Parameters = [new() { S = inviteId }, new() { S = userId }], ConsistentRead = true
            };
            do
            {
                var page = await client.ExecuteStatementAsync(subscriptions);
                foreach (var subscription in page.Items ?? [])
                {
                    if (subscription.GetValueOrDefault("UserId")?.S != userId ||
                        subscription.GetValueOrDefault("InviteId")?.S != inviteId) return null;
                    var projectId = subscription.GetValueOrDefault("ProjectId")?.S;
                    // Pre-attribution subscriptions have no canonical Project link.
                    if (string.IsNullOrWhiteSpace(projectId)) continue;
                    var project = await client.GetItemAsync(new GetItemRequest
                    {
                        TableName = "Projects", Key = new() { ["Id"] = new() { S = projectId } }, ConsistentRead = true
                    });
                    var row = project.Item;
                    if (row is not { Count: > 0 } || row.GetValueOrDefault("UserId")?.S != userId) return null;
                    var mappedCid = row.GetValueOrDefault("LegacyCompanyIdeaId")?.S;
                    if (!string.IsNullOrWhiteSpace(mappedCid) && mappedCid != companyIdeaId) return null;
                    var savedFunnel = subscription.GetValueOrDefault("FunnelId")?.S;
                    if (!string.IsNullOrWhiteSpace(savedFunnel) && savedFunnel != row.GetValueOrDefault("OriginatingFunnelId")?.S)
                        return null;
                    if (context is not null && context.ProjectId != projectId) return null;
                    context = Context(projectId, row);
                }
                subscriptions.NextToken = page.NextToken;
            } while (!string.IsNullOrEmpty(subscriptions.NextToken));
        }

        // Old Dashboard projects are represented by Prompt.CompanyIdeaId. Do not create
        // a Projects/Funnels record or invent attribution merely to authorize generation.
        return context ?? new ProjectContextResponse { ProjectId = companyIdeaId };
    }

    public static async Task<ProjectContextResponse?> ResolveProjectAsync(
        IAmazonDynamoDB client, string userId, string projectId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(projectId)) return null;
        var user = await client.GetItemAsync(new GetItemRequest
        {
            TableName = "Users", Key = new() { ["Id"] = new() { S = userId } },
            ProjectionExpression = "Id", ConsistentRead = true
        });
        if (user.Item?.GetValueOrDefault("Id")?.S != userId) return null;
        var project = await client.GetItemAsync(new GetItemRequest
        {
            TableName = "Projects", Key = new() { ["Id"] = new() { S = projectId } }, ConsistentRead = true
        });
        if (project.Item is { Count: > 0 })
        {
            if (project.Item.GetValueOrDefault("UserId")?.S != userId) return null;
            // Older mapping APIs accepted a client-supplied CID. A mapping alone
            // cannot grant access to somebody else's persisted Prompt workspace.
            var cid = project.Item.GetValueOrDefault("LegacyCompanyIdeaId")?.S;
            if (!string.IsNullOrWhiteSpace(cid) && !await OwnsCompanyIdeaAsync(client, userId, cid)) return null;
            return Context(projectId, project.Item);
        }

        return await ResolveCompanyIdeaAsync(client, userId, projectId);
    }

    private static async Task<bool> OwnsCompanyIdeaAsync(IAmazonDynamoDB client, string userId, string cid)
        => (await OwnedPromptsAsync(client, userId, cid)).Count > 0;

    private static async Task<List<Dictionary<string, AttributeValue>>> OwnedPromptsAsync(
        IAmazonDynamoDB client, string userId, string cid)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(cid)) return [];
        var user = await client.GetItemAsync(new GetItemRequest
        {
            TableName = "Users", Key = new() { ["Id"] = new() { S = userId } },
            ProjectionExpression = "Id", ConsistentRead = true
        });
        if (user.Item?.GetValueOrDefault("Id")?.S != userId) return [];
        var (request, owners) = await DashboardPromptQuery.CreateOwnedRequestAsync(client, userId, cid);
        request.Statement = request.Statement.Replace("SELECT Id, UserId, CompanyIdeaId FROM Prompt",
            "SELECT Id, UserId, CompanyIdeaId, InviteId FROM Prompt", StringComparison.Ordinal);
        request.ConsistentRead = true;
        var prompts = new List<Dictionary<string, AttributeValue>>();
        do
        {
            var page = await client.ExecuteStatementAsync(request);
            prompts.AddRange((page.Items ?? []).Where(row => owners.Contains(row.GetValueOrDefault("UserId")?.S ?? "") &&
                row.GetValueOrDefault("CompanyIdeaId")?.S == cid &&
                !string.IsNullOrWhiteSpace(row.GetValueOrDefault("Id")?.S)));
            request.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));
        return prompts;
    }

    private static ProjectContextResponse Context(string id, Dictionary<string, AttributeValue> row) => new()
    {
        ProjectId = id, FunnelId = row.GetValueOrDefault("OriginatingFunnelId")?.S ?? "",
        AcquisitionId = row.GetValueOrDefault("AcquisitionId")?.S
    };
}
