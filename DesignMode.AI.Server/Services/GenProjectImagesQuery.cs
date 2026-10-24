using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ImageIdeaModel = DesignMode.AI.Server.DesignModeFunctions.ImageIdeaModel;

namespace DesignMode.AI.Server.Services;

public static class GenProjectImagesQuery
{
    public static async Task<(bool HasOwnedPrompts, List<ImageIdeaModel> Images)> LoadAsync(
        IAmazonDynamoDB client, string userId, string companyIdeaId)
    {
        if (string.IsNullOrWhiteSpace(companyIdeaId)) throw new ArgumentException("Project is required.", nameof(companyIdeaId));
        var (request, owners) = await DashboardPromptQuery.CreateOwnedRequestAsync(client, userId, companyIdeaId);
        request.ConsistentRead = true;
        var promptIds = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await client.ExecuteStatementAsync(request);
            foreach (var row in page.Items ?? [])
            {
                if (owners.Contains(row.GetValueOrDefault("UserId")?.S ?? "") &&
                    row.GetValueOrDefault("CompanyIdeaId")?.S == companyIdeaId &&
                    row.GetValueOrDefault("Id")?.S is { Length: > 0 } id)
                    promptIds.Add(id);
            }
            request.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));

        var images = new Dictionary<string, ImageIdeaModel>(StringComparer.Ordinal);
        foreach (var promptId in promptIds)
        {
            var imageRequest = new ExecuteStatementRequest
            {
                Statement = "SELECT * FROM ImageIdeas WHERE PromptId = ?",
                ConsistentRead = true,
                Parameters = [new() { S = promptId }]
            };
            do
            {
                var page = await client.ExecuteStatementAsync(imageRequest);
                foreach (var row in page.Items ?? [])
                {
                    if (row.GetValueOrDefault("PromptId")?.S != promptId ||
                        row.GetValueOrDefault("Id")?.S is not { Length: > 0 } id) continue;
                    images[id] = new ImageIdeaModel
                    {
                        Id = id,
                        DeliverableType = row.GetValueOrDefault("DeliverableType")?.S ?? "",
                        SubCategory = row.GetValueOrDefault("SubCategory")?.S ?? "",
                        ImagePrompt = row.GetValueOrDefault("ImagePrompt")?.S ?? "",
                        ImageUrl = row.GetValueOrDefault("ImageUrl")?.S ?? "",
                        PromptId = promptId,
                        TimeStamp = row.GetValueOrDefault("TimeStamp")?.S ?? "",
                        CountId = row.GetValueOrDefault("CountId")?.S ?? ""
                    };
                }
                imageRequest.NextToken = page.NextToken;
            } while (!string.IsNullOrEmpty(imageRequest.NextToken));
        }
        return (promptIds.Count > 0, images.Values.ToList());
    }
}
