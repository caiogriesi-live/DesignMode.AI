using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using DesignMode.AI.Shared.Services;
using DesignMode.AI.Server;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PrintMaterialPromptTests
{
    private static string Prompt(string project, string category) =>
        $"Print|{project} {category} brief|{project}|Industry|Print details|{category}|{project} brand|Packaging";

    [TestMethod]
    public void Resolve_UsesCanonicalCategory_NotMainDeliverableOrPosition()
    {
        var print = Prompt("B", "Print Materials");
        var website = Prompt("B", "Web & UX/UI Website");
        Assert.AreEqual(print, PrintMaterialPromptResolver.Resolve([website, Prompt("B", "Social"), print]));
        Assert.AreNotEqual(website, PrintMaterialPromptResolver.Resolve([website, print]));
        Assert.AreEqual(print, PrintMaterialPromptResolver.Resolve([print, website]));
    }

    [TestMethod]
    public void Resolve_LegacyCategoryBullet_PreservesCanonicalText()
    {
        var print = Prompt("B", "- Print Materials");
        Assert.AreEqual(print, PrintMaterialPromptResolver.Resolve([print]));
    }

    [TestMethod]
    public void Resolve_MissingCategory_DoesNotInventOrReusePrompt()
    {
        Assert.AreEqual("", PrintMaterialPromptResolver.Resolve([Prompt("Print Materials", "Website"), "Print Materials"]));
        Assert.AreEqual("", PrintMaterialPromptResolver.Resolve(null));
    }

    [TestMethod]
    public void Resolve_SwitchRecommendations_EventsAndWebsiteRemainIsolated()
    {
        var print = Prompt("B", "Print Materials");
        var events = Prompt("B", "Events & Activations");
        var website = Prompt("B", "Web & UX/UI Website");
        foreach (var previous in new[] { website, events, print, website })
        {
            Assert.AreEqual(print, PrintMaterialPromptResolver.Resolve([previous, print]));
            Assert.AreEqual(events, EventActivationPromptResolver.Resolve([previous, print, events]));
        }
        Assert.AreEqual("", PrintMaterialPromptResolver.Resolve([website]));
    }

    [TestMethod]
    public void Resolve_ProjectSwitch_UsesCurrentProject()
    {
        var a = Prompt("A", "Print Materials");
        var b = Prompt("B", "Print Materials");
        Assert.AreEqual(a, PrintMaterialPromptResolver.Resolve([a]));
        Assert.AreEqual(b, PrintMaterialPromptResolver.Resolve([Prompt("B", "Website"), b]));
    }

    [TestMethod]
    public void ModalWiring_PreviewAndBothGenerationPathsUseCanonicalPrompt()
    {
        var parent = ReadSource("DesignMode.AI.Client/Pages/Init/Gen.razor");
        var start = parent.IndexOf("<PrintMaterialModal", StringComparison.Ordinal);
        var binding = parent[start..parent.IndexOf("/>", start, StringComparison.Ordinal)];
        StringAssert.Contains(binding, "@key=\"(typeof(PrintMaterialModal), CompanyIdeaId)\"");
        StringAssert.Contains(binding, "PrintMaterialPromptResolver.Resolve(new[] { promptText }.Concat(promptTexts))");
        var modal = ReadSource("DesignMode.AI.Client/Pages/Init/PrintMaterialModal.razor");
        StringAssert.Contains(modal, "@MainPromptText</p>");
        StringAssert.Contains(modal, "Prompt = MainPromptText,");
        StringAssert.Contains(modal, "promptText = MainPromptText;");
        StringAssert.Contains(modal, "CallRunwareImageFunction(promptText, 1344, 768, 1)");
        StringAssert.Contains(modal, "ProjectId = attribution?.ProjectId");
        StringAssert.Contains(modal, "GenerationJobId = Guid.NewGuid().ToString()");
        var server = ReadSource("DesignMode.AI.Server/PrintMaterialsFunctions.cs");
        StringAssert.Contains(server, ": getData.Prompt.Trim()");
        StringAssert.Contains(server, "text = prompt");
    }

    [TestMethod]
    public async Task ProviderPrompt_RealBuilder_MockedHttpContainsPrintAndProjectContext()
    {
        var print = PrintMaterialPromptResolver.Resolve([Prompt("B", "Website"), Prompt("B", "Print Materials")]);
        var request = new GeneratePrintMaterialRequest { Prompt = print, ProjectId = "project-b", FunnelId = "funnel-b", GenerationJobId = "job-b" };
        var builder = typeof(PrintMaterialsFunctions).GetMethod("BuildPrintMaterialPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
        var prompt = (string)builder.Invoke(null, [request, "B", "Industry", "B brand", request.Prompt, "Brochure", "A4", "Portrait", "CMYK", "B content"])!;
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        await client.PostAsJsonAsync("https://example.invalid/provider", new { prompt });
        StringAssert.Contains(handler.Body!, "Print Materials");
        StringAssert.Contains(prompt, print);
        StringAssert.Contains(prompt, "B brand");
        Assert.IsFalse(prompt.Contains("B Website brief"));
    }

    [TestMethod]
    public async Task Modal_ReferenceImageApiReceivesCanonicalPromptAndSavesProjectIdentity()
    {
        var canonical = PrintMaterialPromptResolver.Resolve([Prompt("B", "Website"), Prompt("B", "Print Materials")]);
        var images = new List<DesignMode.AI.Client.Pages.Init.Gen.ImageIdeaModel>();
        var calls = 0;
        var modal = new DesignMode.AI.Client.Pages.Init.PrintMaterialModal
        {
            MainPromptText = canonical,
            PromptIdFromCID = "project-b-prompt",
            SelectedImages = images,
            CallRunwareImageFunction = (prompt, width, height, count) =>
            {
                Assert.AreEqual(canonical, prompt);
                Assert.AreEqual(1344, width);
                Assert.AreEqual(768, height);
                Assert.AreEqual(1, count);
                calls++;
                return Task.FromResult("https://example.invalid/print.png");
            },
            CallSaveImageIdeaFunction = (type, category, prompt, url, promptId, timestamp, countId) =>
            {
                Assert.AreEqual("Secondary", type);
                Assert.AreEqual("Print Materials", category);
                Assert.AreEqual(canonical, prompt);
                Assert.AreEqual("project-b-prompt", promptId);
                return Task.FromResult("print-b");
            }
        };
        using var renderer = new TestRenderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            renderer.Attach(modal);
            await (Task)modal.GetType().GetMethod("GoToGenerateImages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(modal, null)!;
        });
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, images.Count);
        Assert.AreEqual(canonical, images[0].ImagePrompt);
    }

    [TestMethod]
    public async Task ModalKeys_SiblingModalsRenderAndUpdateWithoutCollision()
    {
        using var renderer = new TestRenderer();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach(new ModalKeyHost());
            foreach (var project in new[] { "project-a", "project-a", "project-b" })
                await renderer.Render(id, Microsoft.AspNetCore.Components.ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(ModalKeyHost.Project)] = project }));
        });
    }

    private sealed class ModalKeyHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter] public string Project { get; set; } = "";
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            // Match Gen's sibling key shapes without requiring either modal's injected services.
            builder.OpenComponent<KeyedModalStub>(0);
            builder.SetKey(Project);
            builder.CloseComponent();
            builder.OpenComponent<KeyedModalStub>(1);
            builder.SetKey((typeof(DesignMode.AI.Client.Pages.Init.PrintMaterialModal), Project));
            builder.CloseComponent();
        }
    }

    private sealed class KeyedModalStub : Microsoft.AspNetCore.Components.ComponentBase { }

    private sealed class TestRenderer : Microsoft.AspNetCore.Components.RenderTree.Renderer
    {
        public TestRenderer() : base(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance) { }
        public override Microsoft.AspNetCore.Components.Dispatcher Dispatcher { get; } = Microsoft.AspNetCore.Components.Dispatcher.CreateDefault();
        public int Attach(Microsoft.AspNetCore.Components.IComponent component) => AssignRootComponentId(component);
        public Task Render(int id, Microsoft.AspNetCore.Components.ParameterView parameters) => RenderRootComponentAsync(id, parameters);
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
    private static string ReadSource(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DesignMode.AI.sln"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root!.FullName, path));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
