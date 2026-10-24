using System.Net.Http.Headers;
using System.Net.Http.Json;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Shared.Models.Attribution;

namespace DesignMode.AI.Client.Pages.Init;

public partial class Gen
{
    private readonly WebsiteImageState _websiteImageState = new();

    private void RequireWebsiteProject(string cid)
    {
        var currentCid = GetQueryParameter(new Uri(NavigationManager.Uri).Query, "cid", null);
        if (string.IsNullOrWhiteSpace(cid) || cid != currentCid || cid != CompanyIdeaId)
            throw new InvalidOperationException("The current project changed. Reopen Website for this project.");
    }

    private Task<ImageIdeaModel> EnsureWebsiteImageAsync(string cid)
    {
        RequireWebsiteProject(cid);
        var mainDeliverable = MainDeliverable;
        ImageIdeaModel? websiteIdea = null;
        var count = 1;
        var placement = WebsiteImageState.ResolvePlacement([], mainDeliverable, null);

        return _websiteImageState.EnsureAsync(cid,
            async () =>
            {
                RequireWebsiteProject(cid);
                var images = await CallGetImageIdeasFunction(cid);
                RequireWebsiteProject(cid);
                if (IsWebsiteFunnelWorkspace)
                    images = images.OrderByDescending(image => image.Id == WebsiteFunnelImageId).ToList();
                count = images.Select(i => int.TryParse(i.CountId, out var value) ? value : 0).DefaultIfEmpty().Max() + 1;
                return images;
            },
            images =>
            {
                RequireWebsiteProject(cid);
                websiteIdea = WebsiteImageState.ResolveImageIdea(images);
                placement = WebsiteImageState.ResolvePlacement([websiteIdea], mainDeliverable, websiteIdea.SubCategory);
                Console.WriteLine("[WEBSITE IMAGE] prompt-source=canonical-image-idea");
                return Task.FromResult(websiteIdea.PromptId);
            },
            async () =>
            {
                var context = await ResolveWebsiteProjectAsync(cid);
                RequireWebsiteProject(cid);
                return await CallRunwareImageFunction(websiteIdea!.ImagePrompt, 1344, 768, 1,
                    WebsiteImageState.FallbackImageFeature, context, websiteIdea.Id, cid);
            },
            // Persist against the captured prompt even if navigation changes during provider completion.
            (id, url) => CallSaveImageIdeaFunction(placement.DeliverableType, placement.SubCategory, websiteIdea!.ImagePrompt, url,
                id, DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"), count.ToString()),
            async images =>
            {
                RequireWebsiteProject(cid);
                await RefreshWebsiteProjectImagesAsync(images);
                Console.WriteLine("[WEBSITE IMAGE] gen state updated");
                try { await UsageRefresh.RequestRefreshAsync(); }
                catch (Exception) { Console.WriteLine("[WEBSITE IMAGE] failed stage=usage-display-refresh"); }
        });
    }
    private async Task HandleWebsiteImageModalClosedAsync(string imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId) ||
            allImagesGenerated.Any(image => image.Id == imageId && WebsiteImageState.IsUsable(image))) return;

        // Normally publication already put this image on Gen. Reload only if it is missing.
        await RefreshAllImages();
    }

    private async Task<ProjectContextResponse> ResolveWebsiteProjectAsync(string cid)
    {
        RequireWebsiteProject(cid);
        var context = await AttributionContext.AuthorizeGenerationAsync(cid);
        RequireWebsiteProject(cid);
        return context;
    }

}
