using System.Net;
using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelProgressServiceTests
{
    [TestMethod]
    public async Task FreshClient_RestoresTokenOnEveryNavigationAndPreservesQuery()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var service = new FunnelProgressService(http, new ApiConfigService(new TestNavigation()), new TokenJs("\"stored-token\""));
        await service.SaveAsync("http://localhost:7178/init/mainconcept?id=one&e=a%2Bb");
        await service.SaveAsync("http://localhost:7178/init/funnelend?id=one");
        Assert.AreEqual(2, handler.Bodies.Count);
        StringAssert.Contains(handler.Bodies[0], "/init/mainconcept?id=one");
        StringAssert.Contains(handler.Bodies[0], "a%2Bb");
        StringAssert.Contains(handler.Bodies[1], "/init/funnelend?id=one");
    }

    [TestMethod]
    public async Task AnonymousAndNonFunnelPages_DoNotWrite()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var service = new FunnelProgressService(http, new ApiConfigService(new TestNavigation()), new TokenJs(null));
        await service.SaveAsync("http://localhost:7178/init/mainconcept");
        await service.SaveAsync("http://localhost:7178/login");
        Assert.AreEqual(0, handler.Bodies.Count);
    }

    [TestMethod]
    public async Task FailedSave_IsSurfacedAndCanBeRetried()
    {
        var handler = new CaptureHandler { Status = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);
        var service = new FunnelProgressService(http, new ApiConfigService(new TestNavigation()), new TokenJs("stored-token"));
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => service.SaveAsync("http://localhost:7178/init/mainconcept"));
        handler.Status = HttpStatusCode.OK;
        await service.SaveAsync("http://localhost:7178/init/mainconcept");
        Assert.AreEqual(2, handler.Bodies.Count);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Bodies = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("Bearer stored-token", request.Headers.Authorization?.ToString());
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new HttpResponseMessage(Status);
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost:7178/", "http://localhost:7178/init/mainconcept");
    }
    private sealed class TokenJs(string? value) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult((TValue)(object?)value!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
