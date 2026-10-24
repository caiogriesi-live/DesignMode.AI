namespace DesignMode.AI.Shared.Services;

/// <summary>Validates the existing structured ImageIdeas.ImagePrompt contract for Website images.</summary>
public static class WebsiteImageIdeaPrompt
{
    public static bool IsWebsite(string? value) =>
        value?.Contains("web", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsValid(string? deliverableType, string? subCategory, string? imagePrompt)
    {
        var parts = imagePrompt?.Split('|');
        return parts is { Length: >= 7 } && !string.IsNullOrWhiteSpace(parts[1]) &&
            IsWebsite(parts[5]) &&
            (IsWebsite(deliverableType) || IsWebsite(subCategory) ||
             (string.IsNullOrWhiteSpace(deliverableType) && string.IsNullOrWhiteSpace(subCategory)));
    }
}
