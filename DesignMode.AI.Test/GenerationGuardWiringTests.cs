using System.Text.RegularExpressions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GenerationGuardWiringTests
{
    [DataTestMethod]
    [DataRow("WebsiteModal")]
    [DataRow("SocialPostModal")]
    [DataRow("PrintMaterialModal")]
    [DataRow("EventModal")]
    [DataRow("PackageLabelModal")]
    [DataRow("BrandLogoModal")]
    [DataRow("DigitalMarketingModal")]
    [DataRow("AIRecommendationsModal")]
    [DataRow("MakeVideoSelectImages")]
    public void GenerationModal_XAndBackdropCannotBypassSharedClose(string name)
    {
        var source = Read($"DesignMode.AI.Client/Pages/Init/{name}.razor");
        StringAssert.Contains(source, "await using var generationOperation = await BeginGenerationAsync()");
        StringAssert.Contains(source, "if (!await ConfirmGenerationCloseAsync()) return;");
        Assert.IsTrue(Regex.IsMatch(source, "<button[^>]*class=\"[^\"]*modal-close[^\"]*\"[^>]*@onclick=\"CloseModal\"", RegexOptions.Singleline));
        Assert.IsTrue(Regex.IsMatch(source, "<div[^>]*class=\"[^\"]*overlay[^\"]*\"[^>]*@onclick=\"CloseModal\"", RegexOptions.Singleline));
        // These custom workspaces have no independent Escape-close path.
        Assert.IsFalse(source.Contains("@onkeydown="));
        Assert.IsFalse(source.Contains("@onkeyup="));
    }

    [TestMethod]
    public void Confirmation_UsesExistingFocusTrapSafeDefaultAndEscapeStay()
    {
        var source = Read("DesignMode.AI.Client/Components/GenerationNavigationGuard.razor");
        StringAssert.Contains(source, "<dialog");
        StringAssert.Contains(source, "aria-labelledby=\"generation-guard-title\"");
        StringAssert.Contains(source, "@oncancel=\"Stay\"");
        StringAssert.Contains(source, "autofocus @onclick=\"Stay\"");
        StringAssert.Contains(source, "./js/ai-credits-modal.js");
        StringAssert.Contains(source, "RegisterLocationChangingHandler");
        StringAssert.Contains(source, "_navigation?.Dispose()");
        StringAssert.Contains(Read("DesignMode.AI.Client/wwwroot/js/ai-credits-modal.js"), "dialog.showModal()");
    }

    [TestMethod]
    public void Commercial_ProtectsClientCompositionNotUnrelatedDurableQueuePolling()
    {
        var source = Read("DesignMode.AI.Client/Pages/Init/MakeVideoSelectImages.razor");
        var start = source.IndexOf("private async Task FinalizeVideo()", StringComparison.Ordinal);
        var end = source.IndexOf("private string _finalizeStatus", start, StringComparison.Ordinal);
        var flow = source[start..end];
        StringAssert.Contains(flow, "await using var generationOperation");
        StringAssert.Contains(flow, "GenerateRunwareSceneVideoAsync");
        StringAssert.Contains(flow, "ComposeFinalCommercialAsync");
        StringAssert.Contains(flow, "SaveFinalVideoUrlAsync");
        Assert.IsFalse(Read("DesignMode.AI.Client/Services/ActiveGenerationGuard.cs").Contains("GenerationJobId"));
    }

    private static string Read(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relative));
    }
}
