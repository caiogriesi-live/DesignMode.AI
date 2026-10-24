using DesignMode.AI.Client.Components;
using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GenerationNavigationGuardTests
{
    [TestMethod]
    public async Task Dialog_RenderedAttributesAreValidAndNativeCancelKeepsGenerationRunning()
    {
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime, ActiveGenerationGuardTests.BrowserStub>()
            .AddSingleton<DesignModeAnalyticsService>()
            .AddSingleton<NavigationManager>(new TestNavigation()).BuildServiceProvider();
        await using var renderer = new GuardRenderer(services);
        var guard = new ActiveGenerationGuard(new ActiveGenerationGuardTests.BrowserStub());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Attach(guard);
            var frames = renderer.Frames;
            var attributes = frames.Array.Take(frames.Count)
                .Where(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray();
            Assert.IsFalse(attributes.Any(frame => frame.AttributeName.StartsWith('@')),
                "Unrecognized Razor directives must not reach the DOM: WebKit rejects their qualified names.");
            var cancel = attributes.Single(frame => frame.AttributeName == "oncancel");
            Assert.AreNotEqual(0UL, cancel.AttributeEventHandlerId, "Cancel must be a compiled event callback.");

            await guard.SetModalVisibleAsync(this, true);
            await using var operation = await guard.BeginAsync(this);
            var navigation = guard.ConfirmLeaveAsync(navigation: true);
            Assert.IsFalse(navigation.IsCompleted);
            await renderer.DispatchEventAsync(cancel.AttributeEventHandlerId, null, EventArgs.Empty);
            Assert.IsFalse(await navigation, "Native Escape/cancel must choose Stay.");
            Assert.IsFalse(guard.IsConfirming);
            Assert.IsTrue(guard.IsActive, "Dismissing the confirmation must not stop generation.");
            await renderer.Remove();
        });
    }

    [TestMethod]
    public async Task FrameworkNavigation_StayPreventsLeaveAllowsAndDisposalUnsubscribes()
    {
        var navigation = new TestNavigation();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime, ActiveGenerationGuardTests.BrowserStub>()
            .AddSingleton<DesignModeAnalyticsService>()
            .AddSingleton<NavigationManager>(navigation).BuildServiceProvider();
        await using var renderer = new GuardRenderer(services);
        var browser = new ActiveGenerationGuardTests.BrowserStub();
        var guard = new ActiveGenerationGuard(browser);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Attach(guard);
            Assert.IsTrue(await navigation.Attempt());
            await guard.SetModalVisibleAsync(this, true);
            var operation = await guard.BeginAsync(this);
            var attempt = navigation.Attempt();
            Assert.IsFalse(attempt.IsCompleted);
            Assert.IsTrue(guard.IsNavigation);
            guard.Resolve(false);
            Assert.IsFalse(await attempt);
            attempt = navigation.Attempt();
            guard.Resolve(true);
            Assert.IsTrue(await attempt);
            await guard.SetModalVisibleAsync(this, false);
            Assert.IsFalse(browser.Active);
            Assert.IsTrue(await navigation.Attempt(), "Background work with no open modal must not block navigation.");
            await guard.SetModalVisibleAsync(this, true);
            await renderer.Remove();
            Assert.IsFalse(browser.Active);
            Assert.IsTrue(await navigation.Attempt());
            await operation.DisposeAsync();
        });
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/init/gen");
        protected override void SetNavigationLockState(bool value) { }
        public ValueTask<bool> Attempt() => NotifyLocationChangingAsync("http://localhost/dashboard", null, false);
    }

    // Render production markup and handlers, skipping only browser interop.
    private sealed class GuardWithoutBrowserInterop : GenerationNavigationGuard
    {
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }

    private sealed class GuardRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        private int _id;
        public ArrayRange<RenderTreeFrame> Frames => GetCurrentRenderTreeFrames(_id);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public Task Attach(ActiveGenerationGuard guard)
        {
            _id = AssignRootComponentId(InstantiateComponent(typeof(GuardWithoutBrowserInterop)));
            return RenderRootComponentAsync(_id, ParameterView.FromDictionary(new Dictionary<string, object>
            { [nameof(GenerationNavigationGuard.Guard)] = guard }));
        }
        public Task Remove() { RemoveRootComponent(_id); return Task.CompletedTask; }
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
}
