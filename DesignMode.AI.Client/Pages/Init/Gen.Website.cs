using Microsoft.AspNetCore.WebUtilities;

namespace DesignMode.AI.Client.Pages.Init;

public partial class Gen
{
    private bool IsWebsiteFunnelWorkspace =>
        QueryHelpers.ParseQuery(new Uri(NavigationManager.Uri).Query).TryGetValue("website", out var value) && value == "1";

    private string WebsiteFunnelImageId =>
        QueryHelpers.ParseQuery(new Uri(NavigationManager.Uri).Query).TryGetValue("websiteImageId", out var value) ? value.ToString() : "";
}
