using Blazored.LocalStorage;
using DesignMode.AI.Client.Pages;
using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Net;
using System.Security.Claims;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class DashboardComponentTests
{
    [TestMethod]
    public void RootDashboard_HasItsOwnAuthenticatedRoute()
    {
        var routes = typeof(RootDashboard).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>();
        CollectionAssert.AreEqual(new[] { "/dashboard" }, routes.Select(route => route.Template).ToArray());
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)typeof(RootDashboard)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).Single();
        Assert.IsNull(authorize.Policy);
        Assert.IsNull(authorize.Roles);
        CollectionAssert.AreEqual(new[] { "/init/dashboard" }, typeof(Dashboard)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Select(route => route.Template).ToArray());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RootDashboard_WaitsForIdentityBeforeRenderingOrRequestingData(bool authenticated)
    {
        var handler = new ProjectHandler("[{\"name\":\"Private project\"}]");
        var authentication = new PendingAuthentication();
        using var services = CreateServices(authenticated, handler, authentication);
        var navigation = (TestNavigation)services.GetRequiredService<NavigationManager>();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<RootDashboard>());
        Assert.AreEqual(string.Empty, await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.IsNull(handler.RequestUri);
        Assert.IsNull(navigation.Destination);

        authentication.Completion.SetResult(new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([], authenticated ? "test" : null))));
        await root.QuiescenceTask;
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        if (authenticated)
        {
            StringAssert.Contains(html, "Private project");
            StringAssert.Contains(html, "workspace-brand-header");
            Assert.AreEqual("http://localhost:7032/api/dashboard/projects", handler.RequestUri);
            Assert.IsNull(navigation.Destination);
        }
        else
        {
            Assert.AreEqual(string.Empty, html);
            Assert.IsNull(handler.RequestUri);
            Assert.AreEqual("/login?returnUrl=%2Fdashboard", navigation.Destination);
            Assert.AreEqual("/dashboard", await DesignMode.AI.Shared.Navigation.PostAuthDestinationResolver.ResolveAsync(
                "name", "/dashboard", new PostAuthDestinationResolverTests.RoutingStore()));
        }
    }

    private sealed class PendingAuthentication : AuthenticationStateProvider
    {
        public TaskCompletionSource<AuthenticationState> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Completion.Task;
    }

    [DataTestMethod]
    [DataRow("[]", "Create your first project")]
    [DataRow("[{\"companyIdeaId\":\"12345678-1234-1234-1234-123456789abc\",\"name\":\"Orange Blast\"}]", "Orange Blast")]
    [DataRow("[{\"companyIdeaId\":\"12345678-1234-1234-1234-123456789abc\",\"name\":\"Orange Blast\",\"thumbnailUrl\":\"https://example.test/preview.png\"}]", "https://example.test/preview.png")]
    public async Task Dashboard_RendersProjectsOrEmptyStateAndSharedHeader(string json, string expected)
    {
        var handler = new ProjectHandler(json);
        using var services = CreateServices(true, handler);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Dashboard>()).ToHtmlString());
        StringAssert.Contains(html, expected);
        StringAssert.Contains(html, "New Project");
        StringAssert.Contains(html, "workspace-brand-header");
        Assert.AreEqual("http://localhost:7032/api/dashboard/projects", handler.RequestUri);
        if (json != "[]")
        {
            StringAssert.Contains(html, "<a class=\"project-card\" href=\"/init/gen?s=1&amp;cid=12345678-1234-1234-1234-123456789abc\"");
            StringAssert.Contains(html, "Open project</span></div></a>");
        }
        if (json.Contains("thumbnailUrl"))
        {
            StringAssert.Contains(html, "src=\"https://example.test/preview.png\"");
            Assert.IsFalse(html.Contains("class=\"placeholder-mark\""));
        }
        else StringAssert.Contains(html, "class=\"placeholder-mark\"");
    }

    [TestMethod]
    public async Task Dashboard_Anonymous_RedirectsWithoutProjectRequest()
    {
        var handler = new ProjectHandler("[]");
        using var services = CreateServices(false, handler);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<Dashboard>());
        Assert.AreEqual("/login", ((TestNavigation)services.GetRequiredService<NavigationManager>()).Destination);
        Assert.IsNull(handler.RequestUri);
    }

    private static ServiceProvider CreateServices(bool authenticated, ProjectHandler handler, AuthenticationStateProvider? authentication = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<AuthenticationStateProvider>(authentication ?? new TestAuthentication(authenticated));
        services.AddSingleton<IJSRuntime, EmptyStorageRuntime>();
        services.AddBlazoredLocalStorage();
        services.AddSingleton<ApiConfigService>();
        services.AddSingleton<AIUsageRefreshService>();
        services.AddSingleton<DesignModeAnalyticsService>();
        services.AddSingleton(new HttpClient(handler));
        return services.BuildServiceProvider();
    }

    private sealed class TestAuthentication(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([], authenticated ? "test" : null))));
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard?UserId=fake&authenticated=true");
        public string? Destination { get; private set; }
        protected override void NavigateToCore(string uri, bool forceLoad) => Destination = uri;
    }

    private sealed class EmptyStorageRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class ProjectHandler(string json) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
