using Image = DesignMode.AI.Client.Pages.Init.Gen.ImageIdeaModel;

namespace DesignMode.AI.Client.Services;

/// <summary>Coordinates the existing image provider, persistence and project reload for a modal action.</summary>
public sealed class WebsiteImageState
{
    public const string FallbackImageFeature = "gen_secondary_image";

    public static Image ResolveImageIdea(IEnumerable<Image> images) => images
        .Where(image => !string.IsNullOrWhiteSpace(image.Id) && !string.IsNullOrWhiteSpace(image.PromptId) &&
            DesignMode.AI.Shared.Services.WebsiteImageIdeaPrompt.IsValid(image.DeliverableType, image.SubCategory, image.ImagePrompt))
        // Match the canonical Gen Image Ideas ordering; malformed/missing counts sort last.
        .OrderBy(image => int.TryParse(image.CountId, out var count) ? count : int.MaxValue)
        .FirstOrDefault() ?? throw new InvalidOperationException("The current project's Website Image Idea is unavailable.");

    public static string? ResolveProviderImage(DesignMode.AI.Shared.Models.RunwareResponse? result, string generationJobId) =>
        result?.Data?.FirstOrDefault(image => image.TaskUUID == generationJobId &&
            !string.IsNullOrWhiteSpace(image.ImageUUID) &&
            Uri.TryCreate(image.ImageURL, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))?.ImageURL;

    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    private sealed class Attempt
    {
        public Task<Image>? Pending;
        public string? SavedId;
        public bool ProviderCompleted;
    }

    public static bool IsUsable(Image? image)
    {
        if (image is null || string.IsNullOrWhiteSpace(image.Id) ||
            !Uri.TryCreate(image.ImageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        if (IsWebsiteDeliverable(image.DeliverableType) || IsWebsiteDeliverable(image.SubCategory)) return true;
        // Older fallbacks omitted both categories. Their structured prompt's deliverable slot
        // identifies Website images; incidental mentions in a brand/social brief do not.
        return string.IsNullOrWhiteSpace(image.DeliverableType) && string.IsNullOrWhiteSpace(image.SubCategory) &&
            IsWebsiteDeliverable(image.ImagePrompt?.Split('|').ElementAtOrDefault(5));
    }

    public static bool IsWebsiteDeliverable(string? value) =>
        value?.Contains("web", StringComparison.OrdinalIgnoreCase) == true;

    public static (string DeliverableType, string SubCategory) ResolvePlacement(
        IEnumerable<Image> images, string? mainDeliverable, string? recommendedTitle)
    {
        if (IsWebsiteDeliverable(mainDeliverable)) return (mainDeliverable!, "Generic");
        var section = images.Select(image => image.SubCategory).FirstOrDefault(IsWebsiteDeliverable);
        return ("Secondary", section ?? (IsWebsiteDeliverable(recommendedTitle) ? recommendedTitle! : "Web & UX/UI Website"));
    }

    public Task<Image> EnsureAsync(string companyIdeaId,
        Func<Task<List<Image>>> reload,
        Func<List<Image>, Task<string>> resolvePrompt,
        Func<Task<string>> generate,
        Func<string, string, Task<string>> save,
        Func<List<Image>, Task> publish)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyIdeaId);
        if (!_attempts.TryGetValue(companyIdeaId, out var attempt))
            _attempts[companyIdeaId] = attempt = new();
        if (attempt.Pending is { IsCompleted: false }) return attempt.Pending;

        // Install the task before invoking delegates (which can synchronously trigger a render).
        var completion = new TaskCompletionSource<Image>(TaskCreationOptions.RunContinuationsAsynchronously);
        attempt.Pending = completion.Task;
        return RunAsync();

        async Task<Image> RunAsync()
        {
            try
            {
                var images = await reload();
                var image = attempt.SavedId is null ? images.FirstOrDefault(IsUsable)
                    : images.FirstOrDefault(i => i.Id == attempt.SavedId && IsUsable(i));
                Console.WriteLine($"[WEBSITE IMAGE] availability checked hasImage={image is not null}");
                if (image is null)
                {
                    // A read/publish failure must never pay for a second image.
                    if (attempt.ProviderCompleted)
                        throw new InvalidOperationException("The generated image could not be resolved. Please try refreshing its saved state.");
                    var promptId = await resolvePrompt(images);
                    if (string.IsNullOrWhiteSpace(promptId) || promptId.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The current project prompt is unavailable.");
                    Console.WriteLine("[WEBSITE IMAGE] fallback generation started");
                    var url = await generate();
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                        throw new InvalidOperationException("Image generation did not return a usable image.");
                    attempt.ProviderCompleted = true;
                    Console.WriteLine("[WEBSITE IMAGE] provider completed");
                    attempt.SavedId = await save(promptId, url);
                    if (string.IsNullOrWhiteSpace(attempt.SavedId))
                    {
                        attempt.SavedId = null;
                        throw new InvalidOperationException("Image persistence was not confirmed.");
                    }
                    Console.WriteLine("[WEBSITE IMAGE] persistence completed");
                    images = await reload();
                    image = images.FirstOrDefault(i => i.Id == attempt.SavedId && i.PromptId == promptId && IsUsable(i))
                        ?? throw new InvalidOperationException("The saved image is not available in the current project.");
                }
                Console.WriteLine("[WEBSITE IMAGE] canonical image resolved");
                await publish(images);
                completion.SetResult(image);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WEBSITE IMAGE] failed stage={(attempt.SavedId is not null ? "refresh" : attempt.ProviderCompleted ? "persistence" : "generation-or-availability")}");
                completion.SetException(ex);
            }
            return await completion.Task;
        }
    }
}
