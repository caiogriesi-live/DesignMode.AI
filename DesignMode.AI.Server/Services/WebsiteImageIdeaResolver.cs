using Amazon.DynamoDBv2;
using DesignMode.AI.Shared.Models.Attribution;
using DesignMode.AI.Shared.Services;
using Image = DesignMode.AI.Server.DesignModeFunctions.ImageIdeaModel;

namespace DesignMode.AI.Server.Services;

public static class WebsiteImageIdeaResolver
{
    public sealed record Resolution(ProjectContextResponse Project, Image Idea);

    public static async Task<Resolution?> ResolveAsync(IAmazonDynamoDB client, string userId,
        string companyIdeaId, string projectId, string imageIdeaId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(companyIdeaId) ||
            string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(imageIdeaId)) return null;

        var project = await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(client, userId, companyIdeaId);
        if (project is null || project.ProjectId != projectId) return null;

        var result = await GenProjectImagesQuery.LoadAsync(client, userId, companyIdeaId);
        var idea = result.Images.FirstOrDefault(image => image.Id == imageIdeaId);
        return idea is not null && WebsiteImageIdeaPrompt.IsValid(idea.DeliverableType, idea.SubCategory, idea.ImagePrompt)
            ? new(project, idea) : null;
    }
}
