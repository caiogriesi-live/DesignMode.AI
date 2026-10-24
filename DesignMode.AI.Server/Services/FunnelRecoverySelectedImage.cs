using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace DesignMode.AI.Server.Services;

public static class FunnelRecoverySelectedImage
{
    public static string? PublicUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (!host.Contains('.') || new[] { ".localhost", ".local", ".internal", ".test", ".invalid", ".azurewebsites.net" }
            .Any(host.EndsWith) || host.Contains("azurite")) return null;
        return uri.AbsoluteUri;
    }

    // Called only after recovery's canonical project/funnel/user ownership checks.
    // MainConcept persists selection in InviteClient.ImageId, not image ordering.
    public static async Task<string?> ResolveAsync(IAmazonDynamoDB client, string userId,
        IReadOnlyDictionary<string, AttributeValue> project)
    {
        try
        {
            if (project.GetValueOrDefault("UserId")?.S != userId) return null;
            var inviteId = project.GetValueOrDefault("LegacyInviteId")?.S;
            HashSet<string>? ownedImages = null;
            if (string.IsNullOrWhiteSpace(inviteId))
            {
                // Acquisition projects can predate invite creation. Their generation jobs
                // already carry canonical ownership even when LegacyInviteId was never saved.
                (inviteId, ownedImages) = await ResolveJobInviteAsync(client, userId, project);
            }
            if (string.IsNullOrWhiteSpace(inviteId)) return null;
            var invites = await ReadAsync(client, "SELECT Id, ImageId FROM InviteClient WHERE Id = ?", inviteId);
            if (invites.Count != 1 || invites[0].GetValueOrDefault("Id")?.S != inviteId) return null;
            var imageId = invites[0].GetValueOrDefault("ImageId")?.S;
            if (string.IsNullOrWhiteSpace(imageId)) return null;
            if (ownedImages is not null && !ownedImages.Contains(imageId)) return null;
            // Match FunnelEnd.FetchInviteClientImages: MainConcept's selected ImageId
            // identifies a row within this project's invite, whose ImageUrl is the displayed artwork.
            // ImageIdeas/Prompt may be absent or differ from that saved funnel image.
            var images = await ReadAsync(client, new ExecuteStatementRequest
            {
                Statement = "SELECT InviteId, ImageId, ImageUrl FROM InviteClientImages WHERE InviteId = ? AND ImageId = ?",
                Parameters = [new() { S = inviteId }, new() { S = imageId }], ConsistentRead = true
            });
            if (images.Count != 1 || images[0].GetValueOrDefault("InviteId")?.S != inviteId ||
                images[0].GetValueOrDefault("ImageId")?.S != imageId) return null;
            return PublicUrl(images[0].GetValueOrDefault("ImageUrl")?.S);
        }
        catch
        {
            // Optional presentation data must never prevent an otherwise eligible send.
            // Do not log exceptions: provider messages can contain URLs and identities.
            return null;
        }
    }

    private static async Task<(string? InviteId, HashSet<string> Images)> ResolveJobInviteAsync(
        IAmazonDynamoDB client, string userId, IReadOnlyDictionary<string, AttributeValue> project)
    {
        var images = new HashSet<string>(StringComparer.Ordinal);
        var projectId = project.GetValueOrDefault("Id")?.S;
        var funnelId = project.GetValueOrDefault("OriginatingFunnelId")?.S;
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(funnelId)) return (null, images);
        var table = Environment.GetEnvironmentVariable("DynamoDbTableName") ?? "DesignModeJobs";
        var jobs = await ReadAsync(client, new ExecuteStatementRequest
        {
            Statement = $"SELECT UserId, ProjectId, FunnelId, ImageId, PreviewKind FROM \"{table.Replace("\"", "\"\"")}\" WHERE UserId = ? AND ProjectId = ? AND FunnelId = ?",
            Parameters = [new() { S = userId }, new() { S = projectId }, new() { S = funnelId }], ConsistentRead = true
        });
        foreach (var job in jobs)
        {
            if (job.GetValueOrDefault("UserId")?.S == userId && job.GetValueOrDefault("ProjectId")?.S == projectId &&
                job.GetValueOrDefault("FunnelId")?.S == funnelId && job.GetValueOrDefault("PreviewKind")?.S == "Social" &&
                job.GetValueOrDefault("ImageId")?.S is { Length: > 0 } imageId) images.Add(imageId);
        }
        var invites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var imageId in images)
        {
            var rows = await ReadAsync(client, "SELECT InviteId, ImageId FROM InviteClientImages WHERE ImageId = ?", imageId);
            foreach (var row in rows)
                if (row.GetValueOrDefault("ImageId")?.S == imageId && row.GetValueOrDefault("InviteId")?.S is { Length: > 0 } id)
                    invites.Add(id);
        }
        // Never choose the first/newest invite or cross to another project's selection.
        return (invites.Count == 1 ? invites.Single() : null, images);
    }

    private static Task<List<Dictionary<string, AttributeValue>>> ReadAsync(IAmazonDynamoDB client,
        string statement, string value) => ReadAsync(client, new ExecuteStatementRequest
        { Statement = statement, Parameters = [new() { S = value }], ConsistentRead = true });

    private static async Task<List<Dictionary<string, AttributeValue>>> ReadAsync(IAmazonDynamoDB client,
        ExecuteStatementRequest request)
    {
        var rows = new List<Dictionary<string, AttributeValue>>();
        do
        {
            var page = await client.ExecuteStatementAsync(request);
            rows.AddRange(page.Items ?? []);
            request.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));
        return rows;
    }
}
