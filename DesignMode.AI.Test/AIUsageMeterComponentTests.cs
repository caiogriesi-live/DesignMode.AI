using System.Net;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using DesignMode.AI.Client.Components;
using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AIUsageMeterComponentTests
{
    [TestMethod]
    public async Task FailedBalanceRequest_KeepsCreditAction_AndRecoversOnRefresh()
    {
        using var handler = new BalanceHttp { Failing = true };
        using var services = Services(handler);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AIUsageMeter>();
            StringAssert.Contains(component.ToHtmlString(), "Balance unavailable");
            StringAssert.Contains(component.ToHtmlString(), ">Add Credits</button>");
            handler.Failing = false;
            await services.GetRequiredService<AIUsageRefreshService>().RequestRefreshAsync();
            StringAssert.Contains(component.ToHtmlString(), "5 remaining");
            Assert.IsFalse(component.ToHtmlString().Contains("Balance unavailable"));
        });
    }

    [TestMethod]
    public async Task FailedRefresh_PreservesLastKnownCountAndModalOptions()
    {
        using var handler = new BalanceHttp();
        using var services = Services(handler);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AIUsageMeter>();
            var usage = services.GetRequiredService<AIUsageRefreshService>();
            var summary = usage.Summary;
            usage.Open();
            handler.Failing = true;
            await usage.RequestRefreshAsync();
            StringAssert.Contains(component.ToHtmlString(), "5 remaining");
            StringAssert.Contains(component.ToHtmlString(), "Last known balance");
            StringAssert.Contains(component.ToHtmlString(), ">Add Credits</button>");
            Assert.AreSame(summary, usage.Summary);
            Assert.IsTrue(usage.IsModalOpen);
        });
    }

    [TestMethod]
    public async Task Anonymous_DoesNotRenderCreditsOrRequestBalance()
    {
        using var handler = new BalanceHttp();
        using var services = Services(handler, false);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AIUsageMeter>();
            Assert.IsFalse(component.ToHtmlString().Contains("Add Credits"));
            Assert.AreEqual(0, handler.Requests);
        });
    }

    private static ServiceProvider Services(BalanceHttp handler, bool authenticated = true)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton<IJSRuntime>(new StorageRuntime(authenticated));
        services.AddBlazoredLocalStorage();
        services.AddSingleton<ApiConfigService>();
        services.AddSingleton<AIUsageRefreshService>();
        services.AddSingleton<DesignModeAnalyticsService>();
        services.AddSingleton(new HttpClient(handler));
        return services.BuildServiceProvider();
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/gen");
    }

    private sealed class StorageRuntime(bool authenticated) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(authenticated ? (TValue)(object)"\"fixture-token\"" : default!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class BalanceHttp : HttpMessageHandler
    {
        public bool Failing { get; set; }
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.AreEqual("fixture-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Failing
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(AICreditsExhaustionTests.Summary(5, "starter")) });
        }
    }
}
