namespace DesignMode.AI.Test;

[TestClass]
public sealed class ProjectDashboardTests
{
    private static string Read(string path) => File.ReadAllText(Path.Combine(FindRoot(), path));
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [TestMethod]
    public void ProjectList_IsAuthenticatedAndFiltersByResolvedUser()
    {
        var functions = Read("DesignMode.AI.Server/AttributionFunctions.cs");
        var store = Read("DesignMode.AI.Server/Services/AttributionStore.cs");
        StringAssert.Contains(functions, "ResolveAuthenticatedUserIdAsync(request)");
        StringAssert.Contains(functions, "_store.ListProjectsAsync(userId)");
        StringAssert.Contains(store, "FilterExpression = \"UserId = :userId\"");
    }

    [TestMethod]
    public void ProjectOpen_RejectsUnownedIdsBeforeContextSwitch()
    {
        var store = Read("DesignMode.AI.Server/Services/AttributionStore.cs");
        var client = Read("DesignMode.AI.Client/Services/AttributionContextService.cs");
        StringAssert.Contains(store, "item.GetValueOrDefault(\"UserId\")?.S != userId");
        StringAssert.Contains(client, "if (!response.IsSuccessStatusCode) return false");
    }

    [TestMethod]
    public void AdditionalProjects_GetIndependentCanonicalIdsAndFunnels()
    {
        var store = Read("DesignMode.AI.Server/Services/AttributionStore.cs");
        Assert.IsTrue(store.Split("Guid.NewGuid().ToString()", StringSplitOptions.None).Length >= 3);
        StringAssert.Contains(store, "[\"OriginatingFunnelId\"] = new() { S = funnelId }");
        StringAssert.Contains(store, "[\"IsOriginalAcquisitionProject\"] = new() { BOOL = false }");
    }

    [TestMethod]
    public void NewProjectFlow_DoesNotInvokeStripeCheckout()
    {
        var dashboard = Read("DesignMode.AI.Client/Pages/Dashboard.razor");
        StringAssert.Contains(dashboard, "Navigation.NavigateTo(\"/init/init\")");
        Assert.IsFalse(dashboard.Contains("Stripe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(dashboard.Contains("checkout", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ExistingProject_UsesPromptCompanyIdeaIdAndDashboardEndpoint()
    {
        var dashboard = Read("DesignMode.AI.Client/Pages/Dashboard.razor");
        StringAssert.Contains(dashboard, "api/dashboard/projects");
        StringAssert.Contains(dashboard, "href=\"@project.Link\"");
        StringAssert.Contains(Read("DesignMode.AI.Shared/Models/DashboardProject.cs"), "/init/gen?s=1&cid={Uri.EscapeDataString(CompanyIdeaId)}");
        Assert.IsFalse(dashboard.Contains("api/projects"));
    }

    [TestMethod]
    public void Dashboard_HasResponsiveGridAndEmptyState()
    {
        StringAssert.Contains(Read("DesignMode.AI.Client/Pages/Dashboard.razor"), "Create your first project");
        var css = Read("DesignMode.AI.Client/Pages/Dashboard.razor.css");
        StringAssert.Contains(css, "grid-template-columns: repeat(auto-fill");
        StringAssert.Contains(css, "@media (max-width:640px)");
    }

    [TestMethod]
    public void Dashboard_IncludesWorkspaceNavigationAndCenteredLogo()
    {
        var dashboard = Read("DesignMode.AI.Client/Pages/Dashboard.razor");
        var navigation = Read("DesignMode.AI.Client/Pages/Init/ClientLeftNav.razor");
        var css = Read("DesignMode.AI.Client/Pages/Dashboard.razor.css");
        StringAssert.Contains(dashboard, "ClientLeftNav");
        StringAssert.Contains(navigation, "title=\"Dashboard\"");
        StringAssert.Contains(navigation, "title=\"Chat\"");
        StringAssert.Contains(css, "grid-column: 2");
        StringAssert.Contains(css, "margin-top: 30px");
    }

    [TestMethod]
    public void ExistingWorkspaceAndPurchaseSuccessRoutesRemainAvailable()
    {
        StringAssert.Contains(Read("DesignMode.AI.Client/Pages/Init/Gen.razor"), "@page \"/init/gen\"");
        StringAssert.Contains(Read("DesignMode.AI.Client/Pages/Init/Success.razor"), "NavigateTo(\"/dashboard\"");
    }
}
