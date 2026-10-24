using System.Net;
using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class ApiSessionHandlerTests
{
    [TestMethod]
    public async Task ReloadedDesignerIntro_RestoresTokenForResolverAndProjectSave()
    {
        var nav = new TestNavigation();
        var api = new ApiConfigService(nav);
        var storage = new TokenJs { Token = "\"saved-token\"" };
        var transport = new Transport();
        using var http = new HttpClient(new ApiSessionHandler(api, storage) { InnerHandler = transport });
        var routing = new PostAuthNavigationService(http, api, nav);
        Assert.IsNull(http.DefaultRequestHeaders.Authorization);
        Assert.IsTrue(await routing.NavigateAsync(resumeCurrentPage: true));
        using var save = await http.PostAsync("http://localhost:7032/api/SaveEmail", new StringContent("{}"));
        CollectionAssert.AreEqual(new[] { "Bearer saved-token", "Bearer saved-token" }, transport.Headers.ToArray());
    }

    [TestMethod]
    public async Task TokenIsRefreshedPerRequestAndNotAddedToOtherOrigins()
    {
        var storage = new TokenJs { Token = "first" };
        var transport = new Transport();
        using var http = new HttpClient(new ApiSessionHandler(new ApiConfigService(new TestNavigation()), storage) { InnerHandler = transport });
        using var one = await http.GetAsync("http://localhost:7032/api/one");
        storage.Token = "second";
        using var two = await http.GetAsync("http://localhost:7032/api/two");
        using var external = await http.GetAsync("https://example.com/api/three");
        storage.Token = null;
        using var loggedOut = await http.GetAsync("http://localhost:7032/api/four");
        CollectionAssert.AreEqual(new string?[] { "Bearer first", "Bearer second", null, null }, transport.Headers.ToArray());
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost:7178/", "http://localhost:7178/designerintro?companyIndustryId=project");
    }
    private sealed class Transport : HttpMessageHandler
    {
        public List<string?> Headers = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Headers.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"destination\":\"/designerintro?companyIndustryId=project\"}", System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TokenJs : IJSRuntime
    {
        public string? Token;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult((TValue)(object?)Token!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
