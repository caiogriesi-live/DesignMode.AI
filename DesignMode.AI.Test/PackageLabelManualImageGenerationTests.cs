using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PackageLabelManualImageGenerationTests
{
    [TestMethod]
    public void OpeningPackageLabel_DoesNotStartImageGeneration()
    {
        var source = Read("DesignMode.AI.Client/Pages/Init/PackageLabelModal.razor");
        var lifecycle = Between(source, "protected override async Task OnParametersSetAsync()", "public class IdeaDto");

        Assert.IsFalse(lifecycle.Contains("GeneratePackageLabelImageOnClickAsync("));
        StringAssert.Contains(source, "@onclick=\"GeneratePackageLabelImageOnClickAsync\"");
        StringAssert.Contains(source, "No Package Label Images Yet");
        StringAssert.Contains(source, "Generate Package Label Image");

        var page = Read("DesignMode.AI.Client/Pages/Init/Gen.razor");
        var open = Between(page, "private async void OpenPackageLabelModal()", "private async Task HandleLabelGenerated");
        Assert.IsFalse(open.Contains("RefreshAllImages("));
        Assert.IsFalse(open.Contains("CallRunwareImageFunction("));
    }

    [TestMethod]
    public void ManualGeneration_PersistsBeforeRefreshingAndSelecting()
    {
        var source = Read("DesignMode.AI.Client/Pages/Init/PackageLabelModal.razor");
        var flow = Between(source, "private async Task GeneratePackageLabelImageOnClickAsync()", "private async Task<string> FetchDeliverableDetail");

        Assert.IsTrue(flow.IndexOf("isGeneratingLabelImages = true", StringComparison.Ordinal) <
                      flow.IndexOf("CallRunwareImageFunction(promptText", StringComparison.Ordinal));
        Assert.IsTrue(flow.IndexOf("CallSaveImageIdeaFunction(", StringComparison.Ordinal) <
                      flow.IndexOf("OnModalClosed.InvokeAsync()", StringComparison.Ordinal));
        Assert.IsTrue(flow.IndexOf("OnModalClosed.InvokeAsync()", StringComparison.Ordinal) <
                      flow.IndexOf("SelectedImageIds.Add(newImage.Id)", StringComparison.Ordinal));
        Assert.IsFalse(flow.Contains("Guid.NewGuid().ToString()"));
        StringAssert.Contains(flow, "finally");
    }

    [TestMethod]
    public void PackageLabel_UsesServerValidatedRecommendationQuotaAndOwnership()
    {
        var page = Read("DesignMode.AI.Client/Pages/Init/Gen.razor");
        var binding = Between(page, "<PackageLabelModal Show=", "<!-- Add Print Materials modal -->");
        StringAssert.Contains(binding, "CallRunwareImageFunction=\"CallPackageLabelImageFunction\"");

        var client = Read("DesignMode.AI.Client/Pages/Init/Gen.razor.cs");
        var call = Between(client, "private async Task<string> CallPackageLabelImageFunction(",
            "private async Task<string> CallRunwareImageFunction(");
        StringAssert.Contains(call, "AttributionContext.AuthorizeGenerationAsync(CompanyIdeaId)");
        StringAssert.Contains(call, "\"package_label_image\", projectContext");

        var server = Read("DesignMode.AI.Server/DesignModeFunctions.cs");
        var endpoint = Between(server, "[Function(\"CallRunwareAPI\")]", "[Function(\"CallRunwareVideoAPI\")]");
        StringAssert.Contains(endpoint, "GetOwnedGenerationProjectAsync");
        StringAssert.Contains(endpoint, "if (!validContext)");
        Assert.IsTrue(endpoint.IndexOf("if (!imageQuota.Allowed)", StringComparison.Ordinal) <
                      endpoint.IndexOf("httpClient.PostAsync(apiUrl, content)", StringComparison.Ordinal));
        StringAssert.Contains(Read("DesignMode.AI.Server/Services/AIUsageQuotaOptions.cs"),
            "\"package_label_image\" => \"AI_GENERATION_UNITS_IMAGE\"");
    }

    [TestMethod]
    public void PackageLabelImage_UsesConfiguredImageUnits()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_GENERATION_UNITS_IMAGE"] = "0.37",
                ["AI_GENERATION_UNITS_RECOMMENDATIONS"] = "2"
            })
            .Build();
        var options = DesignMode.AI.Server.Services.AIUsageQuotaOptions.FromConfiguration(configuration);

        Assert.AreEqual(0.37m, options.UnitsFor("package_label_image"));
    }

    [TestMethod]
    public void OnlyLabelAndPackagingImages_SuppressTheGenerateButton()
    {
        var method = typeof(DesignMode.AI.Client.Pages.Init.PackageLabelModal)
            .GetMethod("IsPackageLabelImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method);

        bool Matches(string deliverable, string subcategory) => (bool)method.Invoke(null,
            [new DesignMode.AI.Client.Pages.Init.Gen.ImageIdeaModel
            {
                ImageUrl = "https://example.invalid/image.png", DeliverableType = deliverable, SubCategory = subcategory
            }])!;

        Assert.IsTrue(Matches("Secondary", "Package Label"));
        Assert.IsTrue(Matches("Packaging", "Generic"));
        Assert.IsFalse(Matches("Brand Logo", "Generic"));
        Assert.IsFalse(Matches("Product Photography", "Generic"));

        var modal = new DesignMode.AI.Client.Pages.Init.PackageLabelModal
        {
            LabelImages = [new DesignMode.AI.Client.Pages.Init.Gen.ImageIdeaModel
            {
                ImageUrl = "https://example.invalid/logo.png", DeliverableType = "Brand Logo", SubCategory = "Generic"
            }]
        };
        var hasImages = typeof(DesignMode.AI.Client.Pages.Init.PackageLabelModal)
            .GetMethod("HasLabelImages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(hasImages);
        Assert.IsFalse((bool)hasImages.Invoke(modal, null)!);
    }

    private static string Between(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        var last = source.IndexOf(end, first, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && last > first);
        return source[first..last];
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
