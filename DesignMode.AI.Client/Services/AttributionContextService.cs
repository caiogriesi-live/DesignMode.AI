using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.JSInterop;

namespace DesignMode.AI.Client.Services;

public sealed class AttributionContextService
{
    private readonly HttpClient _http;
    private readonly ILocalStorageService _storage;
    private readonly ApiConfigService _api;
    private readonly IJSRuntime _js;

    public AttributionContextService(HttpClient http, ILocalStorageService storage, ApiConfigService api, IJSRuntime js)
    {
        _http = http;
        _storage = storage;
        _api = api;
        _js = js;
    }

    public async Task<ProjectContextResponse?> GetContextAsync()
    {
        var directFunnelId = await GetRawLocalStorageValueAsync("dm_funnel_id");
        var directProjectId = await GetRawLocalStorageValueAsync("dm_project_id");
        StoredFunnel? value = null;
        try
        {
            var json = await GetRawLocalStorageValueAsync("dm_social_funnel");
            value = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<StoredFunnel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[ATTRIBUTION] funnel context invalid: {ex.GetType().Name}");
        }

        // CreateSocialProject writes the funnel/project pair together. Prefer that
        // atomic context over independently-written direct keys, then repair the
        // direct keys for older or interrupted browser sessions.
        var hasCompositePair = !string.IsNullOrWhiteSpace(value?.Id) && !string.IsNullOrWhiteSpace(value.ProjectId);
        var funnelId = hasCompositePair ? value!.Id : directFunnelId ?? value?.Id;
        var projectId = hasCompositePair ? value!.ProjectId : directProjectId ?? value?.ProjectId;
        Console.WriteLine($"[ATTRIBUTION] direct/composite funnel match: {string.IsNullOrWhiteSpace(directFunnelId) || string.Equals(directFunnelId, funnelId, StringComparison.Ordinal)}");
        Console.WriteLine($"[ATTRIBUTION] direct/composite project match: {string.IsNullOrWhiteSpace(directProjectId) || string.Equals(directProjectId, projectId, StringComparison.Ordinal)}");
        if (hasCompositePair)
        {
            await SetRawLocalStorageValueAsync("dm_funnel_id", funnelId);
            await SetRawLocalStorageValueAsync("dm_project_id", projectId);
        }
        return string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(funnelId)
            ? null
            : new ProjectContextResponse { ProjectId = projectId, FunnelId = funnelId, AcquisitionId = value?.AcquisitionId };
    }

    public async Task<bool> BindAuthenticatedIdentityAsync(string? legacyCompanyIndustryId = null, string? legacyCompanyIdeaId = null, string? legacyInviteId = null)
    {
        Console.WriteLine("[ATTRIBUTION] identity binding starting");
        var token = await _storage.GetItemAsync<string>("authToken");
        var context = await GetContextAsync();
        if (!string.IsNullOrWhiteSpace(token) && context is not null &&
            await ValidateAndSetActiveProjectAsync(context.ProjectId))
        {
            // Returning users may have stale visitor/session attribution in local
            // storage. The authenticated project endpoint resolves ownership from
            // the JWT and returns the project's canonical funnel relationship.
            // Reuse that verified context without rebinding browser attribution.
            Console.WriteLine("[ATTRIBUTION] authenticated project ownership verified; identity binding skipped");
            return true;
        }

        var ids = await GetBrowserIdsAsync();
        Console.WriteLine($"[ATTRIBUTION] visitor id present: {ids?.VisitorId is not null}");
        Console.WriteLine($"[ATTRIBUTION] session id present: {ids?.SessionId is not null}");
        Console.WriteLine($"[ATTRIBUTION] funnel id present: {!string.IsNullOrWhiteSpace(context?.FunnelId)}");
        Console.WriteLine($"[ATTRIBUTION] project id present: {!string.IsNullOrWhiteSpace(context?.ProjectId)}");
        Console.WriteLine($"[ATTRIBUTION] auth token present: {!string.IsNullOrWhiteSpace(token)}");
        if (ids is null || context is null || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("[ATTRIBUTION] identity binding failed: required browser or authentication context is missing");
            return false;
        }

        var url = $"{_api.ApiBaseUrl.TrimEnd('/')}/api/BindSocialIdentity";
        Console.WriteLine($"[ATTRIBUTION] BindSocialIdentity URL: {url}");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new BindIdentityRequest
            {
                VisitorId = ids.Value.VisitorId, SessionId = ids.Value.SessionId, FunnelId = context.FunnelId, ProjectId = context.ProjectId,
                LegacyCompanyIndustryId = legacyCompanyIndustryId, LegacyCompanyIdeaId = legacyCompanyIdeaId, LegacyInviteId = legacyInviteId
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _http.SendAsync(request);
            Console.WriteLine($"[ATTRIBUTION] BindSocialIdentity response status: {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                var reason = detail.Trim().Trim('"') switch
                {
                    "funnel_not_found" => "funnel_not_found",
                    "jwt_validation_failed" or "canonical_user_not_found" => "authentication_failed",
                    "project_ownership_failure" => "project_ownership_failure",
                    _ => "binding_unresolved"
                };
                Console.Error.WriteLine($"[ATTRIBUTION] identity binding failed: HTTP {(int)response.StatusCode}; reason={reason}");
                return false;
            }

            Console.WriteLine("[ATTRIBUTION] identity binding complete");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ATTRIBUTION] identity binding failed: {ex.GetType().Name}");
            return false;
        }
    }

    public async Task<ProjectContextResponse> AuthorizeGenerationAsync(string companyIdeaId)
    {
        // Binding associates browser acquisition state with an account. It is not a
        // paid-workspace permission check and cannot authorize the requested CID.
        try { await BindAuthenticatedIdentityAsync(); }
        catch (Exception) { Console.WriteLine("[ATTRIBUTION] identity binding unresolved reason=browser_context_unavailable"); }

        Console.WriteLine("[GEN AUTH] canonical project verification starting");
        var token = await _storage.GetItemAsync<string>("authToken");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(companyIdeaId))
            throw new InvalidOperationException("Unable to verify the current project for AI generation.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_api.ApiBaseUrl.TrimEnd('/')}/api/AuthorizeGenerationProject")
        {
            Content = JsonContent.Create(new LegacyProjectResolutionRequest { LegacyCompanyIdeaId = companyIdeaId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Unable to verify the current project for AI generation.");
        var context = await response.Content.ReadFromJsonAsync<ProjectContextResponse>();
        if (string.IsNullOrWhiteSpace(context?.ProjectId))
            throw new InvalidOperationException("Unable to verify the current project for AI generation.");
        Console.WriteLine("[GEN AUTH] ownership verified=true");
        return context;
    }

    public async Task<ProjectContextResponse?> ResolveLegacyProjectAsync(LegacyProjectResolutionRequest legacyIds)
    {
        var token = await _storage.GetItemAsync<string>("authToken");
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_api.ApiBaseUrl.TrimEnd('/')}/api/ResolveLegacyProject")
        {
            Content = JsonContent.Create(legacyIds)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        var context = await response.Content.ReadFromJsonAsync<ProjectContextResponse>();
        if (context is null || string.IsNullOrWhiteSpace(context.ProjectId) || string.IsNullOrWhiteSpace(context.FunnelId)) return null;
        await SetActiveProjectAsync(context);
        return context;
    }

    public async Task SetActiveProjectAsync(ProjectContextResponse context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectId) || string.IsNullOrWhiteSpace(context.FunnelId))
            throw new ArgumentException("A canonical project and funnel are required.", nameof(context));
        await SetRawLocalStorageValueAsync("dm_project_id", context.ProjectId);
        await SetRawLocalStorageValueAsync("dm_funnel_id", context.FunnelId);
        var json = JsonSerializer.Serialize(new StoredFunnel { Id = context.FunnelId, ProjectId = context.ProjectId, AcquisitionId = context.AcquisitionId });
        await SetRawLocalStorageValueAsync("dm_social_funnel", json);
    }

    public async Task<bool> ValidateAndSetActiveProjectAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return false;
        var token = await _storage.GetItemAsync<string>("authToken");
        if (string.IsNullOrWhiteSpace(token)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_api.ApiBaseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return false;
        var context = await response.Content.ReadFromJsonAsync<ProjectContextResponse>();
        if (context is null || string.IsNullOrWhiteSpace(context.FunnelId) ||
            !string.Equals(context.ProjectId, projectId, StringComparison.Ordinal)) return false;
        await SetActiveProjectAsync(context);
        return true;
    }

    public async Task<bool> BindOrVerifyOwnedProjectAsync(
        string projectId,
        string funnelId,
        string? legacyCompanyIndustryId = null,
        string? legacyCompanyIdeaId = null,
        string? legacyInviteId = null)
    {
        var bindingSucceeded = await BindAuthenticatedIdentityAsync(
            legacyCompanyIndustryId, legacyCompanyIdeaId, legacyInviteId);

        // Binding reports acquisition association state, not authorization for this
        // particular project. Always verify the exact project and funnel with the JWT.
        var ownedContext = await GetOwnedProjectContextAsync(projectId);
        var matches = ownedContext is not null &&
            string.Equals(ownedContext.ProjectId, projectId, StringComparison.Ordinal) &&
            string.Equals(ownedContext.FunnelId, funnelId, StringComparison.Ordinal);
        Console.WriteLine($"[ATTRIBUTION] binding succeeded={bindingSucceeded}; canonical ownership verified={matches}");
        return matches;
    }

    private async Task<ProjectContextResponse?> GetOwnedProjectContextAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var token = await _storage.GetItemAsync<string>("authToken");
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_api.ApiBaseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        var context = await response.Content.ReadFromJsonAsync<ProjectContextResponse>();
        if (context is null || string.IsNullOrWhiteSpace(context.FunnelId) ||
            !string.Equals(context.ProjectId, projectId, StringComparison.Ordinal)) return null;
        await SetActiveProjectAsync(context);
        return context;
    }

    private async Task<(string VisitorId, string SessionId)?> GetBrowserIdsAsync()
    {
        var visitorId = await GetRawLocalStorageValueAsync("dm_visitor_id");
        var sessionId = await GetRawLocalStorageValueAsync("dm_session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                var json = await GetRawLocalStorageValueAsync("dm_session_context");
                sessionId = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<StoredSession>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Id;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[ATTRIBUTION] session context invalid: {ex.GetType().Name}");
            }
        }

        return string.IsNullOrWhiteSpace(visitorId) || string.IsNullOrWhiteSpace(sessionId) ? null : (visitorId, sessionId);
    }

    private ValueTask<string?> GetRawLocalStorageValueAsync(string key) =>
        _js.InvokeAsync<string?>("localStorage.getItem", key);

    private ValueTask SetRawLocalStorageValueAsync(string key, string? value) =>
        _js.InvokeVoidAsync("localStorage.setItem", key, value);

    private sealed class StoredFunnel { public string Id { get; set; } = string.Empty; public string ProjectId { get; set; } = string.Empty; public string? AcquisitionId { get; set; } }
    private sealed class StoredSession { public string Id { get; set; } = string.Empty; }
}
