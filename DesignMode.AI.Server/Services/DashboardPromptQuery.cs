using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models;

namespace DesignMode.AI.Server.Services;

public static class DashboardPromptQuery
{
    // No Prompt UserId index is defined in the existing application. Match its PartiQL
    // access pattern, project only safe fields, and consume every page (including empty pages).
    public static async Task<IReadOnlyList<DashboardProject>> ListAsync(
        IAmazonDynamoDB client, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Identity is required.", nameof(userId));
        var (request, ownerIds) = await CreateOwnedRequestAsync(client, userId);
        var projects = new Dictionary<string, DashboardProject>(StringComparer.Ordinal);
        var checkedPrompts = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await client.ExecuteStatementAsync(request);
            foreach (var row in page.Items ?? [])
            {
                if (!ownerIds.Contains(row.GetValueOrDefault("UserId")?.S ?? string.Empty)) continue;
                var project = DashboardProject.FromCompanyIdeaId(row.GetValueOrDefault("CompanyIdeaId")?.S);
                if (project is null) continue;
                if (!projects.TryAdd(project.CompanyIdeaId, project)) project = projects[project.CompanyIdeaId];
                var promptId = row.GetValueOrDefault("Id")?.S;
                if (project.ThumbnailUrl is null && !string.IsNullOrWhiteSpace(promptId) && checkedPrompts.Add(promptId))
                    project.ThumbnailUrl = await FirstImageUrlAsync(client, promptId);
            }
            request.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));

        // Prompt.TimeStamp is client-supplied; do not treat it as a reliable update time.
        return projects.Values.OrderBy(p => p.CompanyIdeaId, StringComparer.Ordinal).ToList();
    }

    private static async Task<string?> FirstImageUrlAsync(IAmazonDynamoDB client, string promptId)
    {
        var request = new ExecuteStatementRequest
        {
            Statement = "SELECT ImageUrl FROM ImageIdeas.\"PromptId-index\" WHERE PromptId = ?",
            Parameters = [new AttributeValue { S = promptId }]
        };
        do
        {
            var page = await client.ExecuteStatementAsync(request);
            foreach (var row in page.Items ?? [])
            {
                var url = row.GetValueOrDefault("ImageUrl")?.S;
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) return url;
            }
            request.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));
        return null;
    }

    public static async Task<(ExecuteStatementRequest Request, HashSet<string> OwnerIds)> CreateOwnedRequestAsync(
        IAmazonDynamoDB client, string userId, string? companyIdeaId = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Identity is required.", nameof(userId));
        // Legacy SavePrompt stored User.Identity.Name (Username) in UserId. Resolve
        // that alias from the canonical Users row, never from request data or a JWT name alone.
        var user = await client.GetItemAsync(new GetItemRequest
        {
            TableName = "Users", Key = new() { ["Id"] = new() { S = userId } },
            ProjectionExpression = "Id, Username", ConsistentRead = true
        });
        var owners = new HashSet<string>(StringComparer.Ordinal) { userId };
        if (user.Item?.GetValueOrDefault("Id")?.S == userId &&
            user.Item.GetValueOrDefault("Username")?.S is { } username && !string.IsNullOrWhiteSpace(username))
            owners.Add(username);
        var parameters = owners.Select(owner => new AttributeValue { S = owner }).ToList();
        var predicate = string.Join(" OR ", owners.Select(_ => "UserId = ?"));
        var request = new ExecuteStatementRequest
        {
            Statement = $"SELECT Id, UserId, CompanyIdeaId FROM Prompt WHERE ({predicate})",
            Parameters = parameters
        };
        if (companyIdeaId is not null)
        {
            request.Statement += " AND CompanyIdeaId = ?";
            request.Parameters.Add(new AttributeValue { S = companyIdeaId });
        }
        return (request, owners);
    }
}
