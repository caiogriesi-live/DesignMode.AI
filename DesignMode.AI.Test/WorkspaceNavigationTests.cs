using System.Security.Claims;
using System.Text.RegularExpressions;
using DesignMode.AI.Client.Pages.Init;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class WorkspaceNavigationTests
{
    [DataTestMethod]
    [DataRow("/init/dashboard", "Dashboard")]
    [DataRow("/app/account", "Settings")]
    [DataRow("/init/gen", null)]
    [DataRow("/dashboard", null)]
    public async Task Navigation_HasFourLabeledLinksAndCorrectActiveState(string path, string? activeLabel)
    {
        var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "caiogriesi@gmail.com")], "jwt"));
        var html = await RenderNavigationAsync(path, owner);
        var links = Regex.Matches(html, "<a\\b[^>]*>.*?</a>", RegexOptions.Singleline)
            .Select(match => match.Value).ToArray();

        Assert.AreEqual(4, links.Length);
        var labels = new[] { "Dashboard", "Marketing Dashboard", "Chat", "Settings" };
        var targets = new[] { "/init/dashboard", "/init/marketing-dashboard", "mailto:contact@designmode.ai", "/app/account" };
        for (var index = 0; index < links.Length; index++)
        {
            StringAssert.Contains(links[index], $"href=\"{targets[index]}\"");
            StringAssert.Contains(links[index], $">{labels[index]}</span>");
            Assert.AreEqual(labels[index] == activeLabel, links[index].Contains("aria-current=\"page\""));
            Assert.IsFalse(links[index].Contains("hide-mobile"));
        }
        StringAssert.Contains(html, "class=\"client-nav\"");
        Assert.AreEqual(4, Regex.Matches(html, "class=\"nav-tooltip\"").Count);
    }

    [DataTestMethod]
    [DataRow("ordinary-user@example.com", true)]
    [DataRow("", true)]
    [DataRow("", false)]
    public async Task Navigation_HidesMarketingDashboardForOtherOrMissingIdentity(string email, bool authenticated)
    {
        var identity = authenticated
            ? new ClaimsIdentity([new Claim("email", email)], "jwt")
            : new ClaimsIdentity();
        var html = await RenderNavigationAsync("/init/dashboard", new ClaimsPrincipal(identity));
        var links = Regex.Matches(html, "<a\\b[^>]*>.*?</a>", RegexOptions.Singleline)
            .Select(match => match.Value).ToArray();

        Assert.AreEqual(3, links.Length);
        Assert.IsFalse(html.Contains("Marketing Dashboard", StringComparison.Ordinal));
        StringAssert.Contains(html, "href=\"/init/dashboard\"");
        StringAssert.Contains(html, "href=\"mailto:contact@designmode.ai\"");
        StringAssert.Contains(html, "href=\"/app/account\"");
    }

    private static async Task<string> RenderNavigationAsync(string path, ClaimsPrincipal user)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(new DesignMode.AI.Client.Services.DesignModeAnalyticsService(new NoJavaScript()));
        collection.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider(user));
        collection.AddSingleton<NavigationManager>(new TestNavigation(path));
        using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ClientLeftNav>()).ToHtmlString());
    }

    [TestMethod]
    public async Task Navigation_ShowsMarketingDashboardForConfiguredOwnerEmailCaseInsensitively()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "CaioGriesi@Gmail.com")], "jwt"));
        var html = await RenderNavigationAsync("/init/dashboard", user);

        StringAssert.Contains(html, "href=\"/init/marketing-dashboard\"");
        Assert.AreEqual(4, Regex.Matches(html, "<a\\b", RegexOptions.Singleline).Count);
    }

    private sealed class NoJavaScript : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Navigation rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class TestAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string path) => Initialize("http://localhost/", "http://localhost" + path);
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
