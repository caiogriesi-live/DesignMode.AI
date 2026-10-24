using Microsoft.JSInterop;
using DesignMode.AI.Shared.Models.Attribution;

namespace DesignMode.AI.Client.Services;

public sealed class DesignModeAnalyticsService(IJSRuntime js)
{
    public Task TrackImageGenerationStartedAsync(string generationId) =>
        TrackImageGenerationAsync("image_generation_started", generationId);

    public Task TrackImageGenerationSucceededAsync(string generationId, long durationMs) =>
        TrackImageGenerationAsync("image_generation_success", generationId, durationMs: durationMs);

    public Task TrackImageGenerationFailedAsync(string generationId, string errorType, long durationMs) =>
        TrackImageGenerationAsync("image_generation_failed", generationId, errorType, durationMs);

    private Task TrackImageGenerationAsync(string eventName, string generationId, string? errorType = null, long durationMs = 0)
    {
        if (!Guid.TryParseExact(generationId, "D", out var id) || id == Guid.Empty) return Task.CompletedTask;
        var parameters = new Dictionary<string, object>
        {
            ["generation_id"] = id.ToString("D"),
            ["provider"] = "runware",
            ["model"] = "runware:101@1"
        };
        if (eventName != "image_generation_started")
            parameters["duration_ms"] = (int)Math.Clamp(durationMs, 0, 3_600_000);
        if (errorType is "provider_error" or "empty_result" or "image_load" or "request_error")
            parameters["error_type"] = errorType;
        return TrackAsync(eventName, parameters);
    }

    public async Task<bool> ImageLoadedAsync(string imageUrl)
    {
        try { return await js.InvokeAsync<bool>("designModeAnalytics.imageLoaded", imageUrl); }
        catch { return false; /* Image measurement must never interrupt application behavior. */ }
    }

    public async Task TrackAsync(string name, object? parameters = null, bool once = false)
    {
        try { await js.InvokeVoidAsync(once ? "designModeAnalytics.once" : "designModeAnalytics.track", name, parameters); }
        catch { /* Measurement must never interrupt application behavior. */ }
    }
    public async Task<bool> TrackAndFlushAsync(string name, object? parameters = null)
    {
        try { return await js.InvokeAsync<bool>("designModeAnalytics.trackAndFlush", name, parameters); }
        catch { return false; /* Measurement must never interrupt checkout. */ }
    }
    public async Task PageAsync(string uri)
    {
        try { await js.InvokeVoidAsync("designModeAnalytics.page", uri); } catch { }
    }
    public async Task<MeasurementContext?> CheckoutContextAsync()
    {
        // Both advertising measurement fields are enforced prospectively by the server; analytics remains independent.
        try { return await js.InvokeAsync<MeasurementContext>("designModeAnalytics.checkoutContext"); } catch { return null; }
    }
    public async Task PaidWorkspaceAsync(string apiBaseUrl)
    {
        try
        {
            var token = await js.InvokeAsync<string?>("localStorage.getItem", "authToken");
            if (string.IsNullOrWhiteSpace(token)) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await http.GetAsync(apiBaseUrl.TrimEnd('/') + "/api/measurement/paid-workspace");
            if (response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "true")
                await TrackAsync("paid_workspace_viewed", new { auth_state = "authenticated" }, once: true);
        }
        catch { }
    }
}
