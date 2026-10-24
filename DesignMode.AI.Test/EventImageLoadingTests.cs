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

namespace DesignMode.AI.Test;

[TestClass]
public sealed class EventImageLoadingTests
{
    private static Task Generate(EventModal modal) => (Task)typeof(EventModal)
        .GetMethod("GoToGenerateImages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(modal, null)!;
    private static bool Busy(EventModal modal) => (bool)typeof(EventModal)
        .GetField("isGeneratingEventImages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(modal)!;
    private static EventModal Modal() => new()
    {
        MainPromptText = "Display|Event brief|Company|Industry|Details|Events & Activations|Brand|Context",
        PromptIdFromCID = "project-prompt",
        SelectedImages = [],
        CallRunwareImageFunction = (_, _, _, _) => Task.FromResult("https://example.test/event.png"),
        CallSaveImageIdeaFunction = (_, _, _, _, _, _, _) => Task.FromResult("saved-id")
    };

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GuardStartup_BlocksSecondInvocation_AndAlwaysClearsLoading(bool fail)
    {
        var browser = new PendingBrowser();
        await using var guard = new ActiveGenerationGuard(browser);
        var modal = Modal();
        modal.GenerationGuard = guard;
        var calls = 0;
        modal.CallRunwareImageFunction = (_, _, _, _) => { calls++; return Task.FromResult(""); };
        var generating = Generate(modal);
        Assert.IsTrue(Busy(modal));
        await Generate(modal);
        Assert.AreEqual(0, calls);
        if (fail) browser.Ready.SetException(new JSException("Guard setup failed"));
        else browser.Ready.SetResult(null);
        await generating;
        Assert.IsFalse(Busy(modal));
        Assert.IsFalse(guard.IsActive);
        Assert.AreEqual(fail ? 0 : 1, calls);
    }

    private sealed class PendingBrowser : IJSRuntime
    {
        public TaskCompletionSource<object?> Ready { get; } = new();
        private bool _first = true;
        public async ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            if (_first)
            {
                _first = false;
                await Ready.Task;
            }
            return default!;
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<T>(identifier, args);
    }

    [TestMethod]
    public async Task Generation_BlocksDuplicatesThroughProviderSaveAndCanonicalRefresh()
    {
        var modal = Modal();
        var provider = new TaskCompletionSource<string>();
        var save = new TaskCompletionSource<string>();
        var refresh = new TaskCompletionSource();
        var saveStarted = new TaskCompletionSource();
        var refreshStarted = new TaskCompletionSource();
        var calls = 0;
        var saves = 0;
        modal.CallRunwareImageFunction = (_, _, _, _) => { calls++; return provider.Task; };
        modal.CallSaveImageIdeaFunction = (_, _, _, _, promptId, _, _) =>
        {
            Assert.AreEqual("project-prompt", promptId);
            saves++;
            saveStarted.SetResult();
            return save.Task;
        };
        modal.OnModalClosed = EventCallback.Factory.Create(new object(), () =>
        {
            refreshStarted.SetResult();
            return refresh.Task;
        });
        var generating = Generate(modal);
        Assert.IsTrue(Busy(modal));
        await Generate(modal);
        Assert.AreEqual(1, calls);
        provider.SetResult("https://example.test/event.png");
        await saveStarted.Task;
        Assert.IsTrue(Busy(modal));
        save.SetResult("saved-id");
        await refreshStarted.Task;
        Assert.IsTrue(Busy(modal));
        await Generate(modal);
        refresh.SetResult();
        await generating;
        Assert.IsFalse(Busy(modal));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, saves);
        Assert.AreEqual("saved-id", modal.SelectedImages!.Single().Id);
        Assert.AreEqual("project-prompt", modal.SelectedImages.Single().PromptId);
    }

    [DataTestMethod]
    [DataRow("provider")]
    [DataRow("authorization")]
    [DataRow("quota")]
    [DataRow("save")]
    [DataRow("empty-save")]
    public async Task Failure_ClearsLoadingWithoutPublishingImage_AndAllowsRetry(string stage)
    {
        var modal = Modal();
        if (stage == "provider") modal.CallRunwareImageFunction = (_, _, _, _) => throw new HttpRequestException();
        if (stage is "authorization" or "quota") modal.CallRunwareImageFunction = (_, _, _, _) => Task.FromResult("");
        if (stage == "save") modal.CallSaveImageIdeaFunction = (_, _, _, _, _, _, _) => throw new HttpRequestException();
        if (stage == "empty-save") modal.CallSaveImageIdeaFunction = (_, _, _, _, _, _, _) => Task.FromResult("");
        await Generate(modal);
        Assert.IsFalse(Busy(modal));
        Assert.AreEqual(0, modal.SelectedImages!.Count);
        modal.CallRunwareImageFunction = (_, _, _, _) => Task.FromResult("https://example.test/retry.png");
        modal.CallSaveImageIdeaFunction = (_, _, _, _, _, _, _) => Task.FromResult("retry-id");
        await Generate(modal);
        Assert.IsFalse(Busy(modal));
        Assert.AreEqual("retry-id", modal.SelectedImages.Single().Id);
    }

    [TestMethod]
    public async Task RefreshFailure_RetainsPersistedImageWithoutRegenerating()
    {
        var modal = Modal();
        var calls = 0;
        modal.CallRunwareImageFunction = (_, _, _, _) => { calls++; return Task.FromResult("https://example.test/event.png"); };
        modal.OnModalClosed = EventCallback.Factory.Create(new object(), () => Task.FromException(new HttpRequestException()));
        await Generate(modal);
        Assert.IsFalse(Busy(modal));
        Assert.AreEqual(1, calls);
        Assert.AreEqual("saved-id", modal.SelectedImages!.Single().Id);
    }

    [DataTestMethod]
    [DataRow("Events & Activations")]
    [DataRow("- Events & Activations")]
    public async Task RenderedModal_IdleDoesNoWork_ActiveShowsSharedLoaderAndDisabledAction(string category)
    {
        await using var services = Services();
        await using var renderer = new ModalRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.Attach();
            var model = Modal();
            var pending = new TaskCompletionSource<string>();
            var calls = 0;
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(EventModal.Show)] = true,
                [nameof(EventModal.MainPromptText)] = DesignMode.AI.Shared.Services.EventActivationPromptResolver.Resolve(
                    [model.MainPromptText.Replace("Events & Activations", category)]),
                [nameof(EventModal.CallRunwareImageFunction)] = (Func<string, int, int, int, Task<string>>)((_, _, _, _) => { calls++; return pending.Task; }),
                [nameof(EventModal.CallSaveImageIdeaFunction)] = model.CallSaveImageIdeaFunction
            });
            await renderer.Render(id, parameters);
            Assert.AreEqual(0, calls, "Opening must not invoke generation or accounting.");
            Assert.IsFalse(renderer.Field<bool>("isGeneratingEventImages"));
            Assert.IsFalse(renderer.GenerateButtonDisabled(id));
            Assert.IsFalse(renderer.Content(id).Contains("Generating event reference image..."));
            var generating = Generate((EventModal)renderer.Owner);
            await renderer.Render(id, parameters);
            var active = renderer.Content(id);
            StringAssert.Contains(active, "Generating event reference image...");
            StringAssert.Contains(active, "spinner-border text-primary");
            StringAssert.Contains(active, "aria-busy=true");
            StringAssert.Contains(active, "role=status");
            StringAssert.Contains(active, "disabled=True");
            Assert.IsFalse(active.Contains("class=modal-busy-overlay"));
            pending.SetResult("https://example.test/event.png");
            await generating;
            await renderer.Render(id, parameters);
            StringAssert.Contains(renderer.Content(id), "https://example.test/event.png");
            Assert.IsFalse(renderer.Content(id).Contains("Generating event reference image..."));
        });
    }

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
        private EventModal _modal = null!;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach()
        {
            _modal = (EventModal)InstantiateComponent(typeof(EventModal));
            return AssignRootComponentId(_modal);
        }
        public object Owner => _modal;
        public T Field<T>(string name) => (T)typeof(EventModal).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_modal)!;
        public Task Render(int id, ParameterView parameters) => RenderRootComponentAsync(id, parameters);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
        public bool GenerateButtonDisabled(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "button") continue;
                var attributes = frames.Array.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
                if (attributes.Any(f => f.AttributeName == "data-testid" && Equals(f.AttributeValue, "events-generate-image")))
                    return attributes.Any(f => f.AttributeName == "disabled" && Equals(f.AttributeValue, true));
            }
            throw new AssertFailedException("Events generate button was not rendered.");
        }
        public string Content(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return string.Join(" ", frames.Array.Take(frames.Count).Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Component => Content(f.ComponentId),
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => f.MarkupContent,
                RenderTreeFrameType.Attribute when f.AttributeName != "Message" => f.AttributeName + "=" + f.AttributeValue?.ToString(),
                _ => ""
            }));
        }
    }

}
