using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DesignMode.AI.Client.Services;
using Microsoft.AspNetCore.Components;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PostAuthNavigationServiceTests
{
    [DataTestMethod]
    [DataRow("/designerintro?funnelId=f&name=A%26B#step2")]
    [DataRow("/designerintro2?name=Acme")]
    [DataRow("/designerintrowebsite?funnelId=f")]
    public async Task DesignerModal_ServerUnavailableDoesNotAdvanceWithoutSaving(string route)
    {
        var navigation = new Navigation(route);
        var handler = new Handler(HttpStatusCode.ServiceUnavailable, "{}");
        var service = new PostAuthNavigationService(new HttpClient(handler), new ApiConfigService(navigation), navigation);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.NavigateAsync(resumeCurrentPage: true));
        Assert.IsNull(navigation.Destination);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual(route, body.RootElement.GetProperty("currentFunnelRoute").GetString());
    }

    [TestMethod]
    public async Task Request_UsesBearerIdentityAndPreservesNestedQueryEncoding()
    {
        var handler = new Handler(HttpStatusCode.OK, """{"destination":"/init/gen?s=1&cid=ABC&label=A%26B"}""");
        var navigation = new Navigation("/login?returnUrl=%2Finit%2Fgen%3Fs%3D1%26cid%3DABC%26label%3DA%2526B");
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "signed-token");
        var service = new PostAuthNavigationService(http, new ApiConfigService(navigation), navigation);
        await service.NavigateAsync();
        Assert.AreEqual("/init/gen?s=1&cid=ABC&label=A%26B", navigation.Destination);
        Assert.AreEqual("Bearer signed-token", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual(2, body.RootElement.EnumerateObject().Count());
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("currentFunnelRoute").ValueKind);
        Assert.AreEqual("/init/gen?s=1&cid=ABC&label=A%26B", body.RootElement.GetProperty("returnUrl").GetString());
        Assert.AreEqual("/api/ResolvePostAuthDestination", handler.Uri!.AbsolutePath);
    }

    [DataTestMethod]
    [DataRow("/login", "/social-landing.html")]
    [DataRow("/signup?returnUrl=%2Finit%2Fgen%3Fs%3D1%26cid%3DABC", "/init/gen?s=1&cid=ABC")]
    [DataRow("/login?returnUrl=https%3A%2F%2Fevil.example", "/social-landing.html")]
    public async Task ServerUnavailable_UsesCanonicalSafeFallback(string uri, string expected)
    {
        var navigation = new Navigation(uri);
        var service = new PostAuthNavigationService(
            new HttpClient(new Handler(HttpStatusCode.ServiceUnavailable, "{}")), new ApiConfigService(navigation), navigation);
        await service.NavigateAsync();
        Assert.AreEqual(expected, navigation.Destination);
        Assert.AreEqual(expected == "/social-landing.html", navigation.Options!.Value.ForceLoad);
        Assert.IsTrue(navigation.Options.Value.ReplaceHistoryEntry);
    }

    [DataTestMethod]
    [DataRow("/designerintro?funnelId=f", true)]
    [DataRow("/social-landing.html", false)]
    [DataRow("/init/dashboard", false)]
    public async Task DesignerModal_ContinuesOnlyWhenResolverSelectsCurrentPage(string destination, bool resumes)
    {
        var navigation = new Navigation("/designerintro?funnelId=f");
        var service = new PostAuthNavigationService(
            new HttpClient(new Handler(HttpStatusCode.OK, JsonSerializer.Serialize(new { destination }))),
            new ApiConfigService(navigation), navigation);
        Assert.AreEqual(resumes, await service.NavigateAsync(resumeCurrentPage: true));
        Assert.AreEqual(resumes ? null : destination, navigation.Destination);
    }

    [DataTestMethod]
    [DataRow("/login")]
    [DataRow("/signup")]
    public void SwitchingAuthMethods_PreservesReturnQuery(string path)
    {
        var navigation = new Navigation("/login?returnUrl=%2Finit%2Fgen%3Fs%3D1%26cid%3DABC%26label%3DA%2526B");
        var service = new PostAuthNavigationService(new HttpClient(), new ApiConfigService(navigation), navigation);
        var link = service.AuthenticationLink(path);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("http://localhost" + link).Query);
        Assert.AreEqual("/init/gen?s=1&cid=ABC&label=A%26B", query["returnUrl"].ToString());
    }

    [DataTestMethod]
    [DataRow("Login.razor", "RedirectLoggedInUser")]
    [DataRow("Signup.razor", "RedirectAfterAuthentication")]
    [DataRow("App/Loginbak.razor", "HandleLogin")]
    public void LoginAndSignup_UseCanonicalNavigation(string file, string method)
    {
        var source = Read("DesignMode.AI.Client/Pages/" + file);
        StringAssert.Contains(source, method);
        StringAssert.Contains(source, "PostAuthNavigation.NavigateAsync(");
        Assert.IsFalse(source.Contains("Nav.NavigateTo(\"init/"));
        Assert.IsFalse(source.Contains("PostAuthEntryFlow"));
        Assert.IsFalse(source.Contains("#pragma warning disable CS0162"));
    }

    [DataTestMethod]
    [DataRow("DesignerIntro.razor")]
    [DataRow("DesignerIntro2.razor")]
    public void DesignerAuthMethods_ShareCanonicalGateBeforeGeneration(string file)
    {
        var source = Read("DesignMode.AI.Client/Pages/" + file);
        StringAssert.Contains(source, "'CompleteWorkspaceGoogleLogin'");
        StringAssert.Contains(source, "'LoginWorkspaceAccount'");
        StringAssert.Contains(source, "'CreateWorkspaceAccount'");
        StringAssert.Contains(source, "PostAuthNavigation.NavigateAsync(resumeCurrentPage: true)");
        var functionStart = source.IndexOf("async function completeAuthenticatedAccount(email)");
        var routing = source.IndexOf("invokeMethodAsync('NavigateAfterWorkspaceAuthentication')", functionStart);
        var generation = source.IndexOf("startImageGenerationProgress();", functionStart);
        Assert.IsTrue(routing > functionStart && generation > routing);
    }

    private static string Read(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesignMode.AI.sln")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, relative));
    }

    private sealed class Navigation : NavigationManager
    {
        public string? Destination;
        public NavigationOptions? Options;
        public Navigation(string path) => Initialize("http://localhost/", "http://localhost" + path);
        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Destination = uri;
            Options = options;
        }
    }

    private sealed class Handler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public string? Body;
        public string? Authorization;
        public Uri? Uri;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            Uri = request.RequestUri;
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
