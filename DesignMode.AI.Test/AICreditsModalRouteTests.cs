using System.Security.Claims;
using DesignMode.AI.Client.Components;
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
public sealed class AICreditsModalRouteTests
{
    [DataTestMethod]
    [DataRow("dashboard", true, true)]
    [DataRow("gen", true, true)]
    [DataRow("home", true, false)]
    [DataRow("account", true, false)]
    [DataRow("dashboard", false, false)]
    [DataRow("gen", false, false)]
    [DataRow("home", false, false)]
    public async Task Modal_IsMountedOnlyForAuthenticatedWorkspacePages(string page, bool authenticated, bool expected)
    {
        var usage = new AIUsageRefreshService();
        usage.Open(); // Simulate stale state or an unfinished checkout from a previous page.
        using var services = new ServiceCollection().AddLogging().AddAuthorizationCore()
            .AddSingleton<AuthenticationStateProvider>(new Authentication(authenticated))
            .AddSingleton(usage).AddSingleton<IJSRuntime, Browser>()
            .AddSingleton<IAICreditPurchaseClient, PendingCheckout>()
            .AddSingleton<DesignModeAnalyticsService>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pageType = page switch
        {
            "dashboard" => typeof(DesignMode.AI.Client.Pages.Dashboard),
            "gen" => typeof(DesignMode.AI.Client.Pages.Init.Gen),
            "account" => typeof(DesignMode.AI.Client.Pages.App.Account.Account),
            _ => typeof(AICreditsModalRouteTests)
        };
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<AICreditsModalHost>(0);
                builder.AddAttribute(1, nameof(AICreditsModalHost.PageType), pageType);
                builder.CloseComponent();
            };
            return (await renderer.RenderComponentAsync<CascadingAuthenticationState>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = content }))).ToHtmlString();
        });
        Assert.AreEqual(expected, html.Contains("<dialog"));
    }

    private sealed class Authentication(bool authenticated) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([], authenticated ? "test" : null))));
    }
    private sealed class Browser : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
    private sealed class PendingCheckout : IAICreditPurchaseClient
    {
        public Task<AICreditCheckoutResponse> StartAsync(string id) => throw new NotSupportedException();
        public Task<string?> PendingAsync() => Task.FromResult<string?>("credit_pending");
        public Task<AICreditPurchaseStatus?> StatusAsync(string id, CancellationToken cancellationToken) => Task.FromResult<AICreditPurchaseStatus?>(null);
        public Task ClearAsync() => Task.CompletedTask;
    }
}
