namespace DesignMode.AI.Client.Pages.Init;

public partial class FunnelEnd
{
    private string? _websiteCheckoutError;
    private string FunnelCopy(string text) => WebsiteFunnel
        ? text.Replace("campaign", "website", StringComparison.Ordinal).Replace("Campaign", "Website", StringComparison.Ordinal)
        : text;
}
