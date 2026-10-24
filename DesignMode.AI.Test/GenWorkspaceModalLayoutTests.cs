namespace DesignMode.AI.Test;

[TestClass]
public sealed class GenWorkspaceModalLayoutTests
{
    [DataTestMethod]
    [DataRow("WebsiteModal.razor")]
    [DataRow("SocialPostModal.razor")]
    [DataRow("PrintMaterialModal.razor")]
    [DataRow("EventModal.razor")]
    [DataRow("MakeVideoModal.razor")]
    [DataRow("MakeVideoSelectImages.razor")]
    [DataRow("PackageLabelModal.razor")]
    [DataRow("DigitalMarketingModal.razor")]
    [DataRow("BrandLogoModal.razor")]
    public void LargeGenModal_OptsIntoWorkspaceLayout(string component)
    {
        var source = Read($"DesignMode.AI.Client/Pages/Init/{component}");
        StringAssert.Contains(source, "generation-workspace-overlay", component);
        StringAssert.Contains(source, "generation-workspace", component);
        StringAssert.Contains(source, "modal-close", component);
    }

    [DataTestMethod]
    [DataRow("ImageConfirm.razor")]
    [DataRow("ImageModal.razor")]
    [DataRow("ExpandModal.razor")]
    public void SmallDialog_DoesNotOptIntoWorkspaceLayout(string component)
    {
        Assert.IsFalse(Read($"DesignMode.AI.Client/Pages/Init/{component}").Contains("generation-workspace-overlay"));
    }

    [TestMethod]
    public void WorkspaceStyles_FillViewportAtEveryWidthAndKeepContentScrollable()
    {
        var css = Read("DesignMode.AI.Client/wwwroot/css/app.css");
        StringAssert.Contains(css, ".modal-overlay.generation-workspace-overlay");
        var desktopRules = css[..css.IndexOf("@media (max-width: 768px)", StringComparison.Ordinal)];
        StringAssert.Contains(desktopRules, "width: 100vw !important");
        StringAssert.Contains(desktopRules, "height: 100vh !important");
        StringAssert.Contains(desktopRules, "height: 100dvh !important");
        StringAssert.Contains(desktopRules, "max-width: none !important");
        StringAssert.Contains(desktopRules, "padding: 0 !important");
        StringAssert.Contains(desktopRules, "transform: none !important");
        Assert.IsFalse(css.Contains("height: 90dvh"));
        Assert.IsFalse(css.Contains("max-width: 800px !important"));
        Assert.IsFalse(css.Contains("max-width: 925px !important"));
        StringAssert.Contains(css, "overflow-y: auto !important");
        StringAssert.Contains(css, "overflow-x: hidden !important");
        StringAssert.Contains(css, "@media (max-width: 768px)");
        StringAssert.Contains(css, ".generation-workspace video");
        StringAssert.Contains(css, "grid-template-columns: minmax(0, 1fr) !important");
        StringAssert.Contains(css, "min-width: 44px");
        StringAssert.Contains(css, "min-height: 44px");
    }

    [DataTestMethod]
    [DataRow("WebsiteModal.razor")]
    [DataRow("SocialPostModal.razor")]
    [DataRow("PrintMaterialModal.razor")]
    [DataRow("EventModal.razor")]
    [DataRow("PackageLabelModal.razor")]
    [DataRow("DigitalMarketingModal.razor")]
    [DataRow("BrandLogoModal.razor")]
    public void WorkspaceResultsTab_UsesShortVisibleLabelWithAccessibleDescription(string component)
    {
        var source = Read($"DesignMode.AI.Client/Pages/Init/{component}");
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(source,
            "<span class=\"tab-icon\">[^<]+</span>\\s*Results"), component);
        StringAssert.Contains(source, "aria-label=\"Generated");
    }

    [TestMethod]
    public void WorkspaceTabs_StayOnOneRowAtMobileWidths()
    {
        var css = Read("DesignMode.AI.Client/wwwroot/css/app.css");
        StringAssert.Contains(css, "flex-wrap: nowrap !important");
        StringAssert.Contains(css, ".generation-workspace .modal-tabs .tab-button");
        StringAssert.Contains(css, "white-space: nowrap");
    }

    [DataTestMethod]
    [DataRow("WebsiteModal.razor")]
    [DataRow("SocialPostModal.razor")]
    [DataRow("PrintMaterialModal.razor")]
    [DataRow("EventModal.razor")]
    public void GenerationWorkspace_KeepsActionFooterOutsideScrollContent(string component)
    {
        var source = Read($"DesignMode.AI.Client/Pages/Init/{component}");
        StringAssert.Contains(source, "footer-actions sticky-footer");
        StringAssert.Contains(source, "save-button");
        StringAssert.Contains(source, "discard-button");
        Assert.IsTrue(source.IndexOf("footer-actions sticky-footer", StringComparison.Ordinal) >
            source.IndexOf("modal-scroll-content", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MobileWorkspace_FillsViewportWithoutPaddingAndShowsFooter()
    {
        var css = Read("DesignMode.AI.Client/wwwroot/css/app.css");
        StringAssert.Contains(css, ".generation-workspace-overlay .generation-workspace");
        StringAssert.Contains(css, "width: 100vw !important");
        StringAssert.Contains(css, "height: 100dvh !important");
        StringAssert.Contains(css, "min-height: 100dvh !important");
        StringAssert.Contains(css, "max-height: 100dvh !important");
        StringAssert.Contains(css, "position: fixed !important");
        StringAssert.Contains(css, "top: 0 !important");
        StringAssert.Contains(css, "right: 0 !important");
        StringAssert.Contains(css, "bottom: 0 !important");
        StringAssert.Contains(css, "left: 0 !important");
        StringAssert.Contains(css, "max-width: none !important");
        StringAssert.Contains(css, "transform: none !important");
        StringAssert.Contains(css, "padding: 0 !important");
        StringAssert.Contains(css, ".generation-workspace .footer-actions");
        StringAssert.Contains(css, "display: flex !important");
        StringAssert.Contains(css, "position: relative !important");
        StringAssert.Contains(css, "justify-content: center !important");
        StringAssert.Contains(css, "padding-bottom: 24px !important");
        var mobileFooter = css[css.LastIndexOf(".generation-workspace .footer-actions {", StringComparison.Ordinal)..];
        StringAssert.Contains(mobileFooter, "flex-direction: column;");
        StringAssert.Contains(mobileFooter, "align-items: center;");
        StringAssert.Contains(mobileFooter, "position: relative !important;");
        StringAssert.Contains(css, "flex: 0 0 auto !important");
        StringAssert.Contains(css, "padding-top: calc(56px + env(safe-area-inset-top)) !important");
        StringAssert.Contains(css, ".generation-workspace .modal-close");
        StringAssert.Contains(css, "env(safe-area-inset-bottom)");
        Assert.IsFalse(Read("DesignMode.AI.Client/Pages/Init/WebsiteModal.razor")
            .Contains("display: none !important;"));
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, path));
    }
}
