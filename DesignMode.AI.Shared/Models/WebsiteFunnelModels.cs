namespace DesignMode.AI.Shared.Models;

public sealed class WebsiteFunnelPreparation
{
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string FunnelId { get; set; } = "";
}

public sealed class WebsiteFunnelPrepared
{
    public string CompanyIdeaId { get; set; } = "";
    public string InviteId { get; set; } = "";
    public string PromptId { get; set; } = "";
}

public sealed class WebsiteFunnelReturn
{
    public string ProjectId { get; set; } = "";
    public string FunnelId { get; set; } = "";
    public string CompanyIdeaId { get; set; } = "";
    public string ImageId { get; set; } = "";
    public string WorkspaceUrl { get; set; } = "";
}
