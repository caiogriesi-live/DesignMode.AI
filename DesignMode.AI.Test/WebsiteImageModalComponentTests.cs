using System.Reflection;
using System.Security.Claims;
using Blazored.LocalStorage;
using DesignMode.AI.Client.Pages.Init;
using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Image = DesignMode.AI.Client.Pages.Init.Gen.ImageIdeaModel;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class WebsiteImageModalComponentTests
{
    [TestMethod]
    public async Task IdleWebsiteModal_ClosesWithoutPromptWhileAnotherModalGenerates()
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await using var guard = new ActiveGenerationGuard(new ActiveGenerationGuardTests.BrowserStub());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach();
            await renderer.Render(id, Parameters([WebsiteImageStateTests.ImageFor("existing")],
                _ => throw new AssertFailedException("Existing images must not regenerate.")));
            renderer.SetGuard(guard);
            var otherModal = new object();
            await guard.SetModalVisibleAsync(otherModal, true);
            await using var operation = await guard.BeginAsync(otherModal);
            await renderer.Close();
            Assert.IsFalse(renderer.IsShown);
            Assert.IsFalse(guard.IsConfirming);
            Assert.IsTrue(guard.ShouldWarn, "The other modal still owns the active generation.");
        });
    }

    [TestMethod]
    public async Task ActiveGeneration_CloseWaitAndLeave_KeepOperationAlive()
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await using var guard = new ActiveGenerationGuard(new ActiveGenerationGuardTests.BrowserStub());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach();
            await renderer.Render(id, Parameters([WebsiteImageStateTests.ImageFor("existing")],
                _ => throw new AssertFailedException("Existing images must not regenerate.")));
            renderer.SetGuard(guard);
            await using var operation = await guard.BeginAsync(renderer.Owner);
            var closing = renderer.Close();
            Assert.IsFalse(closing.IsCompleted);
            guard.Resolve(false);
            await closing;
            Assert.IsTrue(renderer.IsShown);
            closing = renderer.Close();
            guard.Resolve(true);
            await closing;
            Assert.IsFalse(renderer.IsShown);
            Assert.IsTrue(guard.IsActive);
            Assert.IsFalse(guard.ShouldWarn, "Closing the owning modal removes page warnings while work continues.");
        });
    }

    [TestMethod]
    public async Task CloseWithImageAlreadyPublishedToGen_DoesNotReloadOrShowUpdatingOverlay()
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var gen = new GenWithoutMarkup();
            renderer.Attach(gen);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var images = new List<Image> { WebsiteImageStateTests.ImageFor("already-published") };
            typeof(Gen).GetField("allImagesGenerated", flags)!.SetValue(gen, images);

            // No network/storage dependencies are installed on this page instance: a reload would fail.
            var close = typeof(Gen).GetMethod("HandleWebsiteImageModalClosedAsync", flags)!;
            await (Task)close.Invoke(gen, new object[] { "already-published" })!;
            await (Task)close.Invoke(gen, new object[] { "already-published" })!;
            await (Task)close.Invoke(gen, new object[] { "" })!;

            Assert.AreSame(images, typeof(Gen).GetField("allImagesGenerated", flags)!.GetValue(gen));
            Assert.IsFalse((bool)typeof(Gen).GetField("isRefreshingImages", flags)!.GetValue(gen)!);
        });
    }

    [DataTestMethod]
    [DataRow("Branding", "Website Experience")]
    [DataRow("Website UI/UX", "Website UI/UX")]
    public async Task CloseAfterImagePreparation_AwaitsParentRefreshAndUpdatesWebsiteDeliverableOnce(string mainDeliverable, string websiteSection)
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var gen = new GenWithoutMarkup();
            renderer.Attach(gen);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Gen).GetProperty("NavigationManager", flags)!.SetValue(gen, new Navigation());
            typeof(Gen).GetProperty("MainDeliverable", flags)!.SetValue(gen, mainDeliverable);
            var image = WebsiteImageStateTests.ImageFor("saved-on-close");
            var placement = WebsiteImageState.ResolvePlacement([], mainDeliverable, websiteSection);
            image.DeliverableType = placement.DeliverableType;
            image.SubCategory = placement.SubCategory;
            var refreshReady = new TaskCompletionSource();
            var refreshes = 0;
            var closed = EventCallback.Factory.Create<string>(new object(), async imageId =>
            {
                Assert.AreEqual(image.Id, imageId);
                refreshes++;
                await refreshReady.Task;
                await (Task)typeof(Gen).GetMethod("RefreshWebsiteProjectImagesAsync", flags)!
                    .Invoke(gen, new object[] { new List<Image> { image } })!;
            });
            var id = renderer.Attach();
            var generated = new TaskCompletionSource<Image>();
            var generationCalls = 0;
            Func<string, Task<Image>> ensure = _ => { generationCalls++; return generated.Task; };
            var opening = renderer.Render(id, Parameters([], ensure, onClosed: closed));
            Assert.AreEqual(1, generationCalls, "An empty Website modal must prepare an image.");
            Assert.IsTrue(renderer.Field<bool>("isGeneratingWebsiteImages"));
            var publishing = renderer.Render(id, Parameters([image], ensure, onClosed: closed));
            generated.SetResult(image);
            await opening;
            await publishing;
            StringAssert.Contains(renderer.Content(id), image.ImageUrl);
            CollectionAssert.AreEqual(new[] { image.Id }, renderer.Field<HashSet<string>>("SelectedImageIds").ToArray());

            var closing = renderer.Close();
            Assert.AreEqual(1, refreshes);
            Assert.IsFalse(closing.IsCompleted, "Closing must await the parent's refresh.");
            await renderer.Close();
            Assert.AreEqual(1, refreshes, "A second close event must not duplicate the refresh.");
            refreshReady.SetResult();
            await closing;

            var groups = (Dictionary<string, List<Image>>)typeof(Gen).GetField("groupedImages", flags)!.GetValue(gen)!;
            Assert.AreSame(image, groups[websiteSection].Single());
            var urlsProperty = placement.SubCategory == "Generic" ? "RunwareImageUrls" : "SecondaryRunwareImageUrls";
            var urls = (List<string>)typeof(Gen).GetProperty(urlsProperty, flags)!.GetValue(gen)!;
            CollectionAssert.Contains(urls, image.ImageUrl);
            Assert.AreEqual(1, generationCalls);
        });
    }

    [TestMethod]
    public async Task CanonicalReload_UpdatesActualGenPageAndModalCollections()
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var gen = new GenWithoutMarkup();
            renderer.Attach(gen);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Gen).GetProperty("NavigationManager", flags)!.SetValue(gen, new Navigation());
            typeof(Gen).GetProperty("MainDeliverable", flags)!.SetValue(gen, "Branding");
            var image = WebsiteImageStateTests.ImageFor("saved");
            await (Task)typeof(Gen).GetMethod("RefreshWebsiteProjectImagesAsync", flags)!.Invoke(gen, new object[] { new List<Image> { image } })!;
            var all = (List<Image>)typeof(Gen).GetField("allImagesGenerated", flags)!.GetValue(gen)!;
            var modal = (List<Image>)typeof(Gen).GetField("websiteImages", flags)!.GetValue(gen)!;
            var groups = (Dictionary<string, List<Image>>)typeof(Gen).GetField("groupedImages", flags)!.GetValue(gen)!;
            Assert.AreSame(image, all.Single());
            Assert.AreSame(image, modal.Single());
            Assert.AreSame(image, groups["Web & UX/UI Website"].Single());
        });
    }

    [TestMethod]
    public async Task Fallback_WaitsForCanonicalStateThenRendersSelectedImage_AndRetainsSelectionOnRerender()
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach();
            var ready = new TaskCompletionSource<Image>();
            var calls = 0;
            Func<string, Task<Image>> ensure = cid => { Assert.AreEqual("B", cid); calls++; return ready.Task; };
            var rendering = renderer.Render(id, Parameters([], ensure));
            Assert.IsFalse(rendering.IsCompleted);
            StringAssert.Contains(renderer.Content(id), "Generating website UI preview");
            Assert.IsTrue(renderer.Field<bool>("isGeneratingWebsiteImages"));

            var image = WebsiteImageStateTests.ImageFor("persisted");
            var publishing = renderer.Render(id, Parameters([image], ensure)); // parent publishes the canonical reload
            Assert.AreEqual(1, calls);
            ready.SetResult(image);
            await rendering;
            await publishing;
            Assert.IsFalse(renderer.Field<bool>("isGeneratingWebsiteImages"));
            CollectionAssert.AreEqual(new[] { "persisted" }, renderer.Field<HashSet<string>>("SelectedImageIds").ToArray());
            StringAssert.Contains(renderer.Content(id), "https://example.test/image.png");
            StringAssert.Contains(renderer.Content(id), "website-selected");

            await renderer.Render(id, Parameters([image], ensure));
            Assert.AreEqual(1, calls);
            CollectionAssert.AreEqual(new[] { "persisted" }, renderer.Field<HashSet<string>>("SelectedImageIds").ToArray());
        });
    }

    [TestMethod]
    public async Task PreparationFailure_ShowsSafeErrorClearsLoadingAndKeepsWebsiteDisabled()
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach();
            await renderer.Render(id, Parameters([], _ => throw new InvalidOperationException("secret-provider-url")));
            Assert.IsFalse(renderer.Field<bool>("isGeneratingWebsiteImages"));
            Assert.AreEqual(0, renderer.Field<HashSet<string>>("SelectedImageIds").Count);
            StringAssert.Contains(renderer.Content(id), "Unable to prepare the Website image");
            Assert.IsFalse(renderer.Content(id).Contains("secret-provider-url"));
        });
    }

    [TestMethod]
    public async Task ExistingWebsiteImage_OpenRerenderAndReopen_NeverInvokeFallback()
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach();
            var image = WebsiteImageStateTests.ImageFor("existing");
            var fallbackCalls = 0;
            var closeRefreshes = 0;
            var onClosed = EventCallback.Factory.Create<string>(new object(), _ => closeRefreshes++);
            Func<string, Task<Image>> ensure = _ => { fallbackCalls++; return Task.FromResult(image); };
            await renderer.Render(id, Parameters([image], ensure, onClosed: onClosed));
            await renderer.Render(id, Parameters([image], ensure, onClosed: onClosed));
            await renderer.Close();
            await renderer.Render(id, Parameters([image], ensure, onClosed: onClosed));
            await renderer.Close();
            CollectionAssert.AreEqual(new[] { "existing" }, renderer.Field<HashSet<string>>("SelectedImageIds").ToArray());
            Assert.AreEqual(0, fallbackCalls);
            Assert.AreEqual(0, closeRefreshes);
            Assert.IsFalse(renderer.Field<bool>("isGeneratingWebsiteImages"));
        });
    }

    private static ParameterView Parameters(List<Image> images, Func<string, Task<Image>> ensure, bool show = true, EventCallback<string> onClosed = default) =>
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(WebsiteModal.Show)] = show,
            [nameof(WebsiteModal.CompanyIdeaId)] = "B",
            [nameof(WebsiteModal.SelectedImages)] = images,
            [nameof(WebsiteModal.EnsureWebsiteImage)] = ensure,
            [nameof(WebsiteModal.OnModalClosed)] = onClosed
        });

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager, Navigation>()
            .AddSingleton<IJSRuntime, NoBrowser>()
            .AddSingleton<AuthenticationStateProvider, Authentication>()
            .AddSingleton<ApiConfigService>().AddSingleton<AIUsageRefreshService>().AddSingleton<DesignModeAnalyticsService>()
            .AddScoped<AttributionContextService>()
            .AddSingleton(new HttpClient(new NoNetwork()));
        services.AddBlazoredLocalStorage();
        return services.BuildServiceProvider();
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/init/gen?s=1&cid=B");
    }
    private sealed class Authentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))));
    }
    private sealed class NoBrowser : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new AssertFailedException("Browser access is forbidden.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("External HTTP access is forbidden.");
    }
    private sealed class ModalRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        private WebsiteModal _modal = null!;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach()
        {
            _modal = (WebsiteModal)InstantiateComponent(typeof(WebsiteModal));
            return AssignRootComponentId(_modal);
        }
        public int Attach(IComponent component) => AssignRootComponentId(component);
        public void SetGuard(ActiveGenerationGuard guard) => _modal.GenerationGuard = guard;
        public bool IsShown => _modal.Show;
        public object Owner => _modal;
        public Task Close() => (Task)typeof(WebsiteModal).GetMethod("CloseModal", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_modal, null)!;
        public T Field<T>(string name) => (T)typeof(WebsiteModal).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_modal)!;
        public Task Render(int id, ParameterView parameters) => RenderRootComponentAsync(id, parameters);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
        public string Content(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return string.Join(" ", frames.Array.Take(frames.Count).Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => f.MarkupContent,
                RenderTreeFrameType.Attribute => f.AttributeValue?.ToString(),
                _ => ""
            }));
        }
    }

    private sealed class GenWithoutMarkup : Gen, IDisposable, IAsyncDisposable
    {
        // Exercise the real state update without starting unrelated page initialization or child components.
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        void IDisposable.Dispose() { }
        ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
    }
}
