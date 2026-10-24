using DesignMode.AI.Client.Services;
using Microsoft.Extensions.Configuration;
using Image = DesignMode.AI.Client.Pages.Init.Gen.ImageIdeaModel;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class WebsiteImageStateTests
{
    [TestMethod]
    public void FallbackUsesImageCredits_WebsiteChargeRemainsWithSuccessfulWebsiteJob()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI_GENERATION_UNITS_WEBSITE"] = "3" }).Build();
        var options = DesignMode.AI.Server.Services.AIUsageQuotaOptions.FromConfiguration(configuration);
        Assert.AreEqual(0.1m, options.UnitsFor(WebsiteImageState.FallbackImageFeature));
        Assert.AreEqual(3m, options.UnitsFor("website_generation"));
        Assert.AreEqual(3m, DesignMode.AI.Server.Services.AIUsageSummaryService.AggregateGenerationUnits(
        [
            ("website", "actual-website-job", "website_generation", options.UnitsFor("website_generation")),
            ("description", "actual-website-job", "website_generation", 0m)
        ]));
    }

    [TestMethod]
    public void ProviderResponse_RequiresImageIdentityUrlAndMatchingGenerationJob()
    {
        var result = new DesignMode.AI.Shared.Models.RunwareResponse
        {
            Data = [new() { TaskUUID = "job", ImageUUID = "provider-image", ImageURL = "https://example.test/generated.png" }]
        };
        Assert.AreEqual("https://example.test/generated.png", WebsiteImageState.ResolveProviderImage(result, "job"));
        Assert.IsNull(WebsiteImageState.ResolveProviderImage(result, "other-job"));
        result.Data[0].ImageUUID = "";
        Assert.IsNull(WebsiteImageState.ResolveProviderImage(result, "job"));
        Assert.IsNull(WebsiteImageState.ResolveProviderImage(null, "job"));
    }

    [TestMethod]
    public async Task ExistingImage_IsPublishedWithoutProviderOrPersistence()
    {
        var flow = new Flow();
        flow.Database.Add(ImageFor("existing"));
        var image = await flow.Run();
        Assert.AreEqual("existing", image.Id);
        Assert.AreEqual(0, flow.ProviderCalls);
        Assert.AreEqual(0, flow.SaveCalls);
        Assert.AreSame(image, flow.Published.Single());
    }

    [TestMethod]
    public async Task EmptyProject_AwaitsProviderAndPersistenceBeforeReloadAndPublish()
    {
        var flow = new Flow();
        var provider = new TaskCompletionSource<string>();
        var persistence = new TaskCompletionSource<string>();
        flow.Generate = () => provider.Task;
        flow.Persist = async (prompt, url) =>
        {
            var id = await persistence.Task;
            flow.Database.Add(ImageFor(id, prompt, url));
            return id;
        };
        var first = flow.Run();
        var duplicate = flow.Run();
        Assert.AreEqual(1, flow.ProviderCalls);
        Assert.AreEqual(0, flow.SaveCalls);
        Assert.AreEqual(1, flow.Reads);
        provider.SetResult("https://example.test/generated.png");
        Assert.AreEqual(1, flow.SaveCalls);
        Assert.AreEqual(1, flow.Reads);
        Assert.AreEqual(0, flow.Published.Count);
        persistence.SetResult("saved");
        var image = await first;
        Assert.AreSame(image, await duplicate);
        Assert.AreEqual("prompt-B", image.PromptId);
        Assert.AreEqual(2, flow.Reads);
        Assert.AreSame(image, flow.Published.Single());
        await flow.Run(); // rerender/reopen
        Assert.AreEqual(1, flow.ProviderCalls);
        Assert.AreEqual(1, flow.SaveCalls);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not-an-image")]
    [DataRow("javascript:alert(1)")]
    public async Task InvalidProviderResult_DoesNotSaveOrPublish(string url)
    {
        var flow = new Flow { Generate = () => Task.FromResult(url) };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flow.Run());
        Assert.AreEqual(0, flow.SaveCalls);
        Assert.AreEqual(0, flow.Published.Count);
    }

    [TestMethod]
    public async Task ProviderFailure_DoesNotCreateCanonicalState()
    {
        var flow = new Flow { Generate = () => throw new HttpRequestException("provider") };
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => flow.Run());
        Assert.AreEqual(0, flow.SaveCalls);
        Assert.AreEqual(0, flow.Published.Count);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PersistenceFailure_DoesNotFabricateImageOrGenerateAgain(bool throws)
    {
        var flow = new Flow { Persist = (_, _) => throws
            ? throw new InvalidOperationException("persistence") : Task.FromResult("") };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flow.Run());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flow.Run());
        Assert.AreEqual(1, flow.ProviderCalls);
        Assert.AreEqual(1, flow.SaveCalls);
        Assert.AreEqual(0, flow.Published.Count);
    }

    [TestMethod]
    public async Task RefreshFailure_ReopenResolvesSavedIdentityWithoutGeneratingAgain()
    {
        var flow = new Flow { FailRead = 2 };
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => flow.Run());
        Assert.AreEqual(0, flow.Published.Count);
        var image = await flow.Run();
        Assert.AreEqual("saved", image.Id);
        Assert.AreEqual(1, flow.ProviderCalls);
        Assert.AreEqual(1, flow.SaveCalls);
    }

    [TestMethod]
    public async Task SavedImageMissingFromQuery_DoesNotGenerateAgain()
    {
        var flow = new Flow { Persist = (_, _) => Task.FromResult("saved-but-not-visible") };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flow.Run());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flow.Run());
        Assert.AreEqual(1, flow.ProviderCalls);
        Assert.AreEqual(0, flow.Published.Count);
    }

    [TestMethod]
    public async Task InitialRetrievalFailure_DoesNotInterpretFailureAsEmptyProject()
    {
        var flow = new Flow { FailRead = 1 };
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => flow.Run());
        Assert.AreEqual(0, flow.ProviderCalls);
    }

    [TestMethod]
    public async Task MissingPrompt_DoesNotCallProvider()
    {
        var flow = new Flow { Prompt = "Error: missing prompt" };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flow.Run());
        Assert.AreEqual(0, flow.ProviderCalls);
    }

    [TestMethod]
    public async Task PublishFailure_RetryUsesCanonicalSavedImage()
    {
        var flow = new Flow { FailPublish = true };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => flow.Run());
        flow.FailPublish = false;
        await flow.Run();
        Assert.AreEqual(1, flow.ProviderCalls);
        Assert.AreEqual(1, flow.SaveCalls);
    }

    [TestMethod]
    public async Task TwoProjects_HaveIndependentIdentityAndInFlightState()
    {
        var state = new WebsiteImageState();
        var a = new Flow(state) { Cid = "A", Prompt = "prompt-A" };
        a.Database.Add(ImageFor("A-image", "prompt-A"));
        var b = new Flow(state);
        await b.Run();
        Assert.AreEqual("prompt-B", b.Published.Single().PromptId);
        Assert.AreEqual("A-image", a.Database.Single().Id);
        Assert.AreEqual("A-image", (await a.Run()).Id);
        Assert.AreEqual(0, a.ProviderCalls);
        Assert.AreEqual(1, b.ProviderCalls);
    }

    [TestMethod]
    public void WebsiteEligibility_RequiresCanonicalIdentityAndRenderableUrl()
    {
        Assert.IsTrue(WebsiteImageState.IsUsable(ImageFor("saved")));
        Assert.IsFalse(WebsiteImageState.IsUsable(ImageFor("")));
        Assert.IsFalse(WebsiteImageState.IsUsable(ImageFor("saved", url: "")));
        var legacy = ImageFor("legacy");
        legacy.DeliverableType = "";
        legacy.SubCategory = "";
        legacy.ImagePrompt = "Display|Brief|Name|Industry|Detail|Web & UX/UI Website|Description|";
        Assert.IsTrue(WebsiteImageState.IsUsable(legacy));
    }

    [TestMethod]
    public async Task NonWebsiteImage_MentioningWebsite_DoesNotPreventFallbackGeneration()
    {
        var flow = new Flow();
        var marketing = ImageFor("marketing");
        marketing.SubCategory = "Digital Marketing";
        marketing.ImagePrompt = "Create a social ad for our online website";
        flow.Database.Add(marketing);

        Assert.IsFalse(WebsiteImageState.IsUsable(marketing));
        Assert.AreEqual("saved", (await flow.Run()).Id);
        Assert.AreEqual(1, flow.ProviderCalls);
    }

    [TestMethod]
    public void FallbackPlacement_UsesExistingWebsiteSectionIncludingEmptyPlaceholder()
    {
        var placeholder = ImageFor("placeholder", url: "");
        placeholder.SubCategory = "Website Experience";
        var target = WebsiteImageState.ResolvePlacement([placeholder], "Branding", "Web & UX/UI Website");
        Assert.AreEqual("Secondary", target.DeliverableType);
        Assert.AreEqual("Website Experience", target.SubCategory);
        Assert.IsFalse(WebsiteImageState.IsUsable(placeholder));
    }

    [TestMethod]
    public void FallbackPlacement_MainWebsite_GoesIntoMainWebsiteSection()
    {
        var target = WebsiteImageState.ResolvePlacement([], "Website UI/UX", null);
        Assert.AreEqual("Website UI/UX", target.DeliverableType);
        Assert.AreEqual("Generic", target.SubCategory);
    }

    internal static Image ImageFor(string id, string prompt = "prompt-B", string url = "https://example.test/image.png") =>
        new() { Id = id, PromptId = prompt, ImageUrl = url, DeliverableType = "Secondary", SubCategory = "Web & UX/UI Website", CountId = "1" };

    private sealed class Flow(WebsiteImageState? state = null)
    {
        private readonly WebsiteImageState _state = state ?? new();
        public string Cid = "B", Prompt = "prompt-B";
        public int ProviderCalls, SaveCalls, Reads, FailRead;
        public bool FailPublish;
        public List<Image> Database = [], Published = [];
        public Func<Task<string>> Generate = () => Task.FromResult("https://example.test/generated.png");
        public Func<string, string, Task<string>>? Persist;
        public Task<Image> Run() => _state.EnsureAsync(Cid,
            () => ++Reads == FailRead ? throw new HttpRequestException("retrieval") : Task.FromResult(Database.ToList()),
            _ => Task.FromResult(Prompt),
            () => { ProviderCalls++; return Generate(); },
            (prompt, url) =>
            {
                SaveCalls++;
                if (Persist is not null) return Persist(prompt, url);
                Database.Add(ImageFor("saved", prompt, url));
                return Task.FromResult("saved");
            },
            images =>
            {
                if (FailPublish) throw new InvalidOperationException("publish");
                Published = images;
                return Task.CompletedTask;
            });
    }
}
