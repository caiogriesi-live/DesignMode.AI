using System.Net;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using DesignMode.AI.Client.Services;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GenerationAttributionFallbackTests
{
    [DataTestMethod]
    [DataRow(200)]
    [DataRow(409)]
    [DataRow(503)]
    [DataRow(0)]
    public async Task BindingSuccessMissingFunnelOrNetworkFailure_AlwaysUsesCanonicalAuthorization(int bindingStatus)
    {
        var transport = new Transport { BindingStatus = bindingStatus };
        using var services = Services(transport);
        var context = await services.GetRequiredService<AttributionContextService>().AuthorizeGenerationAsync("requested-cid");
        Assert.AreEqual("server-project", context.ProjectId);
        Assert.AreEqual(1, transport.BindCalls);
        Assert.AreEqual(1, transport.AuthorizationCalls);
        Assert.AreEqual("requested-cid", transport.RequestedCid);
        Assert.AreEqual(0, transport.ProviderCalls, "Authorization must never start or retry a provider operation.");
    }

    [DataTestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(503)]
    public async Task CanonicalAuthorizationFails_GenerationRemainsBlocked(int status)
    {
        var transport = new Transport { BindingStatus = 409, AuthorizationStatus = status };
        using var services = Services(transport);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            services.GetRequiredService<AttributionContextService>().AuthorizeGenerationAsync("other-cid"));
        Assert.AreEqual(1, transport.AuthorizationCalls);
        Assert.AreEqual(0, transport.ProviderCalls);
    }

    [TestMethod]
    public async Task ReturningOwnerWithoutBrowserAttribution_StillChecksCanonicalProject()
    {
        var transport = new Transport();
        using var services = Services(transport, browserContext: false);
        Assert.AreEqual("server-project", (await services.GetRequiredService<AttributionContextService>()
            .AuthorizeGenerationAsync("requested-cid")).ProjectId);
        Assert.AreEqual(0, transport.BindCalls);
        Assert.AreEqual(1, transport.AuthorizationCalls);
    }

    [TestMethod]
    public async Task Binding_AlreadyOwnedProjectReusesAuthenticatedCanonicalContext()
    {
        var transport = new Transport { ProjectStatus = 200 };
        using var services = Services(transport);
        var result = await services.GetRequiredService<AttributionContextService>().BindAuthenticatedIdentityAsync();
        Assert.IsTrue(result);
        Assert.AreEqual(1, transport.OwnedProjectCalls);
        Assert.AreEqual(0, transport.BindCalls, "An already-owned project must not be rebound through stale browser attribution.");
    }

    [TestMethod]
    public async Task WebsiteBindingConflict_UsesOnlyMatchingAuthenticatedProjectFallback()
    {
        var transport = new Transport { BindingStatus = 409, VerifiedProjectId = "verified-project" };
        using var services = Services(transport);

        var result = await services.GetRequiredService<AttributionContextService>()
            .BindOrVerifyOwnedProjectAsync("verified-project", "server-funnel");

        Assert.IsTrue(result);
        Assert.AreEqual(1, transport.BindCalls);
        Assert.AreEqual(2, transport.OwnedProjectCalls, "The stale context is rejected and the explicit project is checked separately.");
    }

    [TestMethod]
    public async Task WebsiteBindingConflict_ProjectLookupMismatchRemainsBlocked()
    {
        var transport = new Transport
        {
            BindingStatus = 409,
            VerifiedProjectId = "verified-project",
            VerifiedProjectResponseId = "another-project"
        };
        using var services = Services(transport);

        var result = await services.GetRequiredService<AttributionContextService>()
            .BindOrVerifyOwnedProjectAsync("verified-project", "server-funnel");

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task MissingLegacyBrowserProject_ResolvesAuthenticatedProjectContext()
    {
        var transport = new Transport();
        using var services = Services(transport);

        var context = await services.GetRequiredService<AttributionContextService>().ResolveLegacyProjectAsync(
            new LegacyProjectResolutionRequest { LegacyCompanyIndustryId = "legacy-company", LegacyInviteId = "legacy-invite" });

        Assert.IsNotNull(context);
        Assert.AreEqual("resolved-project", context.ProjectId);
        Assert.AreEqual("server-funnel", context.FunnelId);
        Assert.AreEqual(1, transport.LegacyResolutionCalls);
    }

    [TestMethod]
    public async Task ExistingOwnedProjectWithDifferentFunnelCannotAuthorizeWebsiteGeneration()
    {
        var transport = new Transport { BindingStatus = 409, ProjectStatus = 200 };
        using var services = Services(transport);

        var result = await services.GetRequiredService<AttributionContextService>()
            .BindOrVerifyOwnedProjectAsync("stale-browser-project", "another-funnel");

        Assert.IsFalse(result);
        Assert.AreEqual(0, transport.BindCalls, "An already-owned stale context must not be rebound.");
        Assert.AreEqual(2, transport.OwnedProjectCalls, "The expected project/funnel pair is independently checked after the initial ownership check.");
    }

    [TestMethod]
    public async Task MissingToken_DoesNotAuthorizeOrCallProvider()
    {
        var transport = new Transport();
        using var services = Services(transport, token: false);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            services.GetRequiredService<AttributionContextService>().AuthorizeGenerationAsync("requested-cid"));
        Assert.AreEqual(0, transport.AuthorizationCalls);
        Assert.AreEqual(0, transport.ProviderCalls);
    }

    private static ServiceProvider Services(Transport transport, bool browserContext = true, bool token = true)
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager, Navigation>()
            .AddSingleton<IJSRuntime>(new Storage(browserContext, token))
            .AddSingleton<ApiConfigService>().AddSingleton(new HttpClient(transport))
            .AddScoped<AttributionContextService>();
        services.AddBlazoredLocalStorage();
        return services.BuildServiceProvider();
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/init/gen?cid=requested-cid");
    }
    private sealed class Storage(bool browserContext, bool token) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            if (identifier == "localStorage.setItem") return ValueTask.FromResult(default(T)!);
            var key = args?[0]?.ToString();
            object? value = key == "authToken" ? token ? "\"test-token\"" : null :
                !browserContext ? null : key switch
                {
                    "dm_visitor_id" => "visitor", "dm_session_id" => "session",
                    "dm_project_id" => "stale-browser-project", "dm_funnel_id" => "missing-funnel", _ => null
                };
            return ValueTask.FromResult((T)value!);
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
    private sealed class Transport : HttpMessageHandler
    {
        public int BindingStatus = 409, AuthorizationStatus = 200, ProjectStatus = 404;
        public int BindCalls, AuthorizationCalls, OwnedProjectCalls, ProviderCalls, LegacyResolutionCalls;
        public string? VerifiedProjectId, VerifiedProjectResponseId;
        public string? RequestedCid;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            if (request.RequestUri!.AbsolutePath.EndsWith("BindSocialIdentity"))
            {
                BindCalls++;
                if (BindingStatus == 0) throw new HttpRequestException("Simulated network failure");
                return new((HttpStatusCode)BindingStatus) { Content = new StringContent("funnel_not_found") };
            }
            if (request.RequestUri.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal))
            {
                OwnedProjectCalls++;
                var requestedProjectId = Uri.UnescapeDataString(request.RequestUri.Segments[^1]);
                var status = requestedProjectId == VerifiedProjectId ? HttpStatusCode.OK : (HttpStatusCode)ProjectStatus;
                return new(status)
                {
                    Content = status == HttpStatusCode.OK
                        ? JsonContent.Create(new ProjectContextResponse
                        {
                            ProjectId = requestedProjectId == VerifiedProjectId
                                ? VerifiedProjectResponseId ?? requestedProjectId
                                : requestedProjectId,
                            FunnelId = "server-funnel"
                        })
                        : new StringContent(string.Empty)
                };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("AuthorizeGenerationProject"))
            {
                AuthorizationCalls++;
                RequestedCid = (await request.Content!.ReadFromJsonAsync<LegacyProjectResolutionRequest>(cancellationToken: token))?.LegacyCompanyIdeaId;
                return new((HttpStatusCode)AuthorizationStatus) { Content = JsonContent.Create(new ProjectContextResponse { ProjectId = "server-project" }) };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("ResolveLegacyProject"))
            {
                LegacyResolutionCalls++;
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ProjectContextResponse { ProjectId = "resolved-project", FunnelId = "server-funnel" })
                };
            }
            ProviderCalls++;
            throw new AssertFailedException("Unexpected provider request");
        }
    }
}
