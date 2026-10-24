using System.Net;
using System.Reflection;
using System.Text.Json;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using DesignMode.AI.Client.Pages.Init;
using Microsoft.AspNetCore.Components;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class EventActivationPromptTests
{
    private static string Prompt(string project, string category) =>
        $"Display|{project} {category} creative brief|{project}|Industry|Deliverable details|{category}|{project} branding|Packaging context";

    [TestMethod]
    public async Task ReferenceImage_ModalGeneratesAndSavesEventsIdea_ThenRefreshesOnClose()
    {
        var canonical = Prompt("B", "Events & Activations");
        string? generatedPrompt = null;
        var refreshed = false;
        var images = new List<Gen.ImageIdeaModel>();
        var modal = new EventModal
        {
            MainPromptText = canonical,
            PromptIdFromCID = "project-b-prompt",
            SelectedImages = images,
            CallRunwareImageFunction = (prompt, width, height, count) =>
            {
                generatedPrompt = prompt;
                Assert.AreEqual("Events & Activations", prompt.Split('|')[5]);
                StringAssert.StartsWith(prompt.Split('|')[1], canonical.Split('|')[1]);
                return Task.FromResult("https://example.invalid/events.png");
            },
            CallSaveImageIdeaFunction = (type, category, prompt, url, promptId, timestamp, countId) =>
            {
                Assert.AreEqual("Secondary", type);
                Assert.AreEqual("Events & Activations", category);
                Assert.AreEqual(generatedPrompt, prompt);
                Assert.AreEqual("project-b-prompt", promptId);
                return Task.FromResult("event-image-id");
            },
            OnModalClosed = EventCallback.Factory.Create(new object(), () => { refreshed = true; })
        };

        await (Task)typeof(EventModal).GetMethod("GoToGenerateImages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(modal, null)!;
        Assert.AreEqual(1, images.Count);
        Assert.AreEqual("Events & Activations", images[0].SubCategory);
        Assert.AreEqual("Secondary", images[0].DeliverableType);
        Assert.AreEqual("project-b-prompt", images[0].PromptId);
        await (Task)typeof(EventModal).GetMethod("CloseModal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(modal, null)!;
        Assert.IsTrue(refreshed, "Closing must refresh the parent's Events section from saved images.");
    }

    [TestMethod]
    public void ReferencePrompt_ImageApiFields_PreserveEventsAiIdeaAndProject()
    {
        var canonical = Prompt("B", "Events & Activations");
        var result = EventActivationPromptResolver.BuildReferencePrompt(canonical, "Pop-up; Venue | Hall; Event tagline");
        var originalFields = canonical.Split('|');
        var fields = result.Split('|');
        Assert.AreEqual(originalFields.Length, fields.Length, "Nested prompts corrupt the image API's positional contract.");
        StringAssert.StartsWith(fields[1], originalFields[1]);
        StringAssert.Contains(fields[1], "Event tagline");
        Assert.AreEqual("Events & Activations", fields[5]);
        foreach (var index in new[] { 0, 2, 3, 4, 5, 6, 7 })
            Assert.AreEqual(originalFields[index], fields[index]);
        Assert.ThrowsException<ArgumentException>(() => EventActivationPromptResolver.BuildReferencePrompt(
            Prompt("B", "Web & UX/UI Website"), "Event"));
    }

    [TestMethod]
    public void Resolve_EventsAfterOtherDeliverables_PreservesCanonicalPrompt()
    {
        var events = Prompt("B", "Events & Activations");
        var website = Prompt("B", "Web & UX/UI Website");
        var result = EventActivationPromptResolver.Resolve([website, Prompt("B", "Social Media Content"), events]);
        Assert.AreEqual(events, result);
        Assert.AreNotEqual(website, result, "The main-deliverable prompt must never be reused.");
    }

    [TestMethod]
    public void Resolve_SwitchRecommendations_DoesNotRetainPreviousPrompt()
    {
        var events = Prompt("B", "Events & Activations");
        Assert.AreEqual("", EventActivationPromptResolver.Resolve([Prompt("B", "Web & UX/UI Website")]));
        Assert.AreEqual(events, EventActivationPromptResolver.Resolve([events]));
        Assert.AreEqual("", EventActivationPromptResolver.Resolve([Prompt("B", "Social Media Content")]));
        Assert.AreEqual(events, EventActivationPromptResolver.Resolve([events]));
    }

    [TestMethod]
    public void Resolve_SwitchProjects_UsesCurrentProjectContext()
    {
        var projectA = Prompt("A", "Events & Activations");
        var projectB = Prompt("B", "Events & Activations");
        Assert.AreEqual(projectA, EventActivationPromptResolver.Resolve([projectA]));
        var result = EventActivationPromptResolver.Resolve([Prompt("B", "Web & UX/UI Website"), projectB]);
        Assert.AreEqual(projectB, result);
        Assert.AreNotEqual(projectA, result);
    }

    [TestMethod]
    public void Resolve_NoCanonicalPrompt_DoesNotMatchWordsInAnotherBrief()
    {
        Assert.AreEqual("", EventActivationPromptResolver.Resolve([
            Prompt("Events & Activations", "Web & UX/UI Website"), "Events & Activations"]));
        Assert.AreEqual("", EventActivationPromptResolver.Resolve(null));
    }

    [DataTestMethod]
    [DataRow("- Events & Activations")]
    [DataRow("  - events & activations  ")]
    public void Resolve_BulletedCategory_PreservesOriginalPrompt(string category)
    {
        var events = Prompt("B", category);
        var resolved = EventActivationPromptResolver.Resolve([Prompt("A", "Social Media Content"), events]);
        Assert.AreEqual(events, resolved);
        var reference = EventActivationPromptResolver.BuildReferencePrompt(resolved, "Conference");
        Assert.AreEqual(category, reference.Split('|')[5]);
        StringAssert.Contains(reference.Split('|')[1], "Conference");
    }

    [TestMethod]
    public void ModalWiring_PreviewAndSubmissionUseResolvedPrompt_AndProjectScopesState()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DesignMode.AI.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        var parent = File.ReadAllText(Path.Combine(root.FullName, "DesignMode.AI.Client/Pages/Init/Gen.razor"));
        var start = parent.IndexOf("<EventModal", StringComparison.Ordinal);
        var binding = parent[start..parent.IndexOf("/>", start, StringComparison.Ordinal)];
        StringAssert.Contains(binding, "@key=\"CompanyIdeaId\"");
        StringAssert.Contains(binding, "MainPromptText=\"@DesignMode.AI.Shared.Services.EventActivationPromptResolver.Resolve(new[] { promptText }.Concat(promptTexts))\"");
        var modal = File.ReadAllText(Path.Combine(root.FullName, "DesignMode.AI.Client/Pages/Init/EventModal.razor"));
        StringAssert.Contains(modal, "readonly>@MainPromptText</textarea>");
        StringAssert.Contains(modal, "Prompt = MainPromptText,");
        StringAssert.Contains(modal, "EventActivationPromptResolver.BuildReferencePrompt(MainPromptText, eventContent)");
        StringAssert.Contains(modal, "CallRunwareImageFunction(promptText, 1344, 768, 1)");
        StringAssert.Contains(modal, "CallSaveImageIdeaFunction(\"Secondary\", \"Events & Activations\", promptText, imageUrl, PromptIdFromCID");
        StringAssert.Contains(modal, "DeliverableType = \"Secondary\"");
        StringAssert.Contains(modal, "SubCategory = \"Events & Activations\"");
        StringAssert.Contains(modal, "if (OnModalClosed.HasDelegate && wasImageGenerated) await OnModalClosed.InvokeAsync()");
    }

    [TestMethod]
    public async Task GenerateAsset_MockedProvider_ReceivesEventsPromptAndBrandContext()
    {
        var events = Prompt("B", "Events & Activations");
        var website = Prompt("B", "Web & UX/UI Website");
        var resolved = EventActivationPromptResolver.Resolve([website, events]);
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        // Paid provider calls now require a durable accounting start, including child assets.
        using var database = new GenerationProjectAuthorizationTests.Database();
        var attribution = new AttributionStore(NullLogger<AttributionStore>.Instance, clients: () => database);
        var usageStore = new AIGenerationUsageStore(attribution, NullLogger<AIGenerationUsageStore>.Instance);
        var usage = new AIUsageRecorder(usageStore, new AIProviderPricingService(), NullLogger<AIUsageRecorder>.Instance);
        var function = new GenerateEventActivationFunction(NullLogger<GenerateEventActivationFunction>.Instance, usage, null!, null!);
        var method = typeof(GenerateEventActivationFunction).GetMethod("GenerateAssetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(function, [client, "https://example.invalid/reference.png", "B", "Industry",
            "B branding", resolved, "Conference", "FullKit", "2026-10-01", "Venue", "Event description",
            "poster", "user", "project-b", "funnel-b", "job-b"])!;
        await task;
        Assert.IsNotNull(handler.Body);
        using var payload = JsonDocument.Parse(handler.Body);
        var submitted = payload.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        StringAssert.Contains(submitted, events);
        Assert.IsFalse(submitted.Contains(website, StringComparison.Ordinal));
        StringAssert.Contains(submitted, "Company description: B branding");
        StringAssert.Contains(submitted, "Create a finished event poster");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"output\":[]}") };
        }
    }
}
