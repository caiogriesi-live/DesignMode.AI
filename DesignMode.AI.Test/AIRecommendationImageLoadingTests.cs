namespace DesignMode.AI.Test;

[TestClass]
public sealed class AIRecommendationImageLoadingTests
{
    [TestMethod]
    public void SharedLoader_IsAccessibleAndOnlyAppearsWhileVisible()
    {
        var source = Read("AIRecommendationGenerationLoader.razor");
        StringAssert.Contains(source, "@if (IsVisible)");
        StringAssert.Contains(source, "spinner-border text-primary");
        StringAssert.Contains(source, "role=\"status\"");
        StringAssert.Contains(source, "aria-busy=\"true\"");
    }

    [DataTestMethod]
    [DataRow("EventModal.razor", "isGeneratingEventImages")]
    [DataRow("BrandLogoModal.razor", "isGeneratingBrandingImages")]
    [DataRow("PackageLabelModal.razor", "isGeneratingLabelImages")]
    [DataRow("PrintMaterialModal.razor", "isGeneratingPrintImages")]
    [DataRow("WebsiteModal.razor", "isGeneratingWebsiteImages")]
    [DataRow("SocialPostModal.razor", "isGeneratingSocialImages")]
    [DataRow("DigitalMarketingModal.razor", "isGeneratingMarketingImages")]
    public void ImageGeneration_UsesModalLoaderAndClearsState(string file, string flag)
    {
        var source = Read(file);
        StringAssert.Contains(source, "<AIRecommendationGenerationLoader");
        StringAssert.Contains(source, flag + " = true;");
        StringAssert.Contains(source, flag + " = false;");
    }

    private static string Read(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "DesignMode.AI.Client", "Pages", "Init", file));
    }
}
