namespace DesignMode.AI.Shared.Services;

public static class WebsiteFunnelContext
{
    public static string WorkspaceUrl(string companyIdeaId, string projectId, string imageId,
        string name, string industry, string prompt, string inviteId, string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(companyIdeaId) || string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(imageId))
            throw new ArgumentException("A saved website concept and canonical project are required.");
        if (new[] { name, industry, prompt, inviteId, imageUrl }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("The saved brief, company and selected image are required for generation.");
        // Use the same fresh-generation inputs as Social, retaining the canonical Website project.
        return $"/init/gen/{Uri.EscapeDataString(projectId)}?cid={Uri.EscapeDataString(companyIdeaId)}&website=1&websiteImageId={Uri.EscapeDataString(imageId)}" +
            $"&name={Uri.EscapeDataString(name)}&industry={Uri.EscapeDataString(industry)}" +
            $"&prompt={Uri.EscapeDataString(":" + prompt)}&inviteId={Uri.EscapeDataString(inviteId)}&seed={Uri.EscapeDataString(imageUrl)}";
    }
}
