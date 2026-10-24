using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Blazored.LocalStorage;
using DesignMode.AI.Client.Pages.App.Account;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AccountComponentTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_RequiresConfirmation_PreventsDuplicateRequests_AndHandlesResult(bool fail)
    {
        using var handler = new AccountHttp(new("canonical@example.test", "Plus", "active", true, false));
        using var services = Services(handler);
        await using var renderer = new InteractionRenderer(services);
        var id = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = renderer.Attach();
            await renderer.Render(component);
            return component;
        });
        await renderer.Dispatcher.InvokeAsync(() => renderer.Click(id, "Cancel Subscription"));
        Assert.AreEqual(0, handler.Posts);
        await renderer.Dispatcher.InvokeAsync(() => renderer.Click(id, "Keep Subscription"));
        Assert.AreEqual(0, handler.Posts);
        await renderer.Dispatcher.InvokeAsync(() => renderer.Click(id, "Cancel Subscription"));
        Task? pending = null;
        await renderer.Dispatcher.InvokeAsync(() => { pending = renderer.Click(id, "Cancel at End of Billing Period"); });
        Assert.AreEqual(1, handler.Posts);
        await renderer.Dispatcher.InvokeAsync(() => renderer.Click(id, "Scheduling cancellation…"));
        Assert.AreEqual(1, handler.Posts);
        handler.Cancellation.SetResult(fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("provider-secret") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AccountSummary("canonical@example.test", "Plus", "active", false, true)) });
        await pending!;
        var content = await renderer.Dispatcher.InvokeAsync(() => renderer.Content(id));
        StringAssert.Contains(content, fail ? "Refresh your account before retrying" : "Cancellation scheduled for the end of your billing period.");
        Assert.IsFalse(content.Contains("provider-secret"));
    }

    [DataTestMethod]
    [DataRow(true, false, "Plus", true)]
    [DataRow(false, true, "Plus", false)]
    [DataRow(false, false, "None", false)]
    public async Task Account_RendersCanonicalDetailsAndAppropriateCancellation(bool cancellable, bool scheduled, string plan, bool button)
    {
        using var handler = new AccountHttp(new("canonical@example.test", plan, "active", cancellable, scheduled));
        using var services = Services(handler);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Account>()).ToHtmlString());
        StringAssert.Contains(html, "canonical@example.test");
        StringAssert.Contains(html, plan);
        StringAssert.Contains(html, "profile-icon");
        StringAssert.Contains(html, "href=\"/app/logout\"");
        StringAssert.Contains(html, ">Log Out</a>");
        Assert.AreEqual(button, html.Contains(">Cancel Subscription</button>"));
        Assert.AreEqual(scheduled, html.Contains("Cancellation scheduled for the end of your billing period."));
        Assert.IsFalse(html.Contains("victim") || html.Contains("sub_private"));
        Assert.AreEqual("/api/account", handler.Path);
    }

    [TestMethod]
    public async Task Anonymous_RedirectsWithoutRequest_AndRouteRequiresAuthorization()
    {
        Assert.IsTrue(typeof(Account).IsDefined(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false));
        using var handler = new AccountHttp(null);
        using var services = Services(handler, false);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Account>()).ToHtmlString());
        Assert.IsNull(handler.Path);
        Assert.IsFalse(html.Contains("account-card"));
        Assert.AreEqual("/login?returnUrl=%2Fapp%2Faccount", ((Navigation)services.GetRequiredService<NavigationManager>()).Destination);
    }

    [TestMethod]
    public async Task ProviderFailure_ShowsFriendlyErrorWithoutRawDetails()
    {
        using var handler = new AccountHttp(null);
        using var services = Services(handler);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Account>()).ToHtmlString());
        StringAssert.Contains(html, "Refresh Account");
        StringAssert.Contains(html, "href=\"/app/logout\"");
        Assert.IsFalse(html.Contains("provider-secret") || html.Contains(">Cancel Subscription</button>"));
    }

    private static ServiceProvider Services(AccountHttp handler, bool authenticated = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton<AuthenticationStateProvider>(new Authentication(authenticated));
        services.AddSingleton<IJSRuntime, StorageRuntime>();
        services.AddBlazoredLocalStorage();
        services.AddSingleton<ApiConfigService>();
        services.AddSingleton<DesignModeAnalyticsService>();
        services.AddSingleton<AIUsageRefreshService>();
        services.AddSingleton(new HttpClient(handler));
        return services.BuildServiceProvider();
    }

    private sealed class Authentication(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([], authenticated ? "test" : null))));
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/app/account?UserId=victim");
        public string? Destination { get; private set; }
        protected override void NavigateToCore(string uri, bool forceLoad) => Destination = uri;
    }
    private sealed class StorageRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
    private sealed class AccountHttp(AccountSummary? summary) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public int Posts { get; private set; }
        public TaskCompletionSource<HttpResponseMessage> Cancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Assert.AreEqual("/api/account/cancel", Path);
                Assert.IsNull(request.Content, "The client must not send a subscription ID.");
                Posts++;
                return Cancellation.Task;
            }
            Assert.AreEqual(HttpMethod.Get, request.Method);
            return Task.FromResult(summary is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("provider-secret sub_private") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(summary) });
        }
    }

    private sealed class InteractionRenderer(IServiceProvider services)
        : Microsoft.AspNetCore.Components.RenderTree.Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach() => AssignRootComponentId(InstantiateComponent(typeof(Account)));
        public Task Render(int id) => RenderRootComponentAsync(id, ParameterView.Empty);
        protected override Task UpdateDisplayAsync(in Microsoft.AspNetCore.Components.RenderTree.RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
        public string Content(int id) => string.Join(" ", GetCurrentRenderTreeFrames(id).Array.Take(GetCurrentRenderTreeFrames(id).Count)
            .Select(frame => frame.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Text ? frame.TextContent
                : frame.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Markup ? frame.MarkupContent : ""));
        public Task Click(int id, string label)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Element || frames.Array[i].ElementName != "button") continue;
                var button = frames.Array.Skip(i + 1).Take(frames.Array[i].ElementSubtreeLength - 1).ToArray();
                if (!button.Any(f => f.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Text && f.TextContent == label)) continue;
                var handler = button.Single(f => f.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
                return DispatchEventAsync(handler.AttributeEventHandlerId, null, new MouseEventArgs());
            }
            throw new AssertFailedException("Button missing: " + label);
        }
    }
}
