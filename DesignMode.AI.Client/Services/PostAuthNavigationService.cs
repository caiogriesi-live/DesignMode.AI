using System.Net.Http.Json;
using DesignMode.AI.Shared.Navigation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace DesignMode.AI.Client.Services;

public sealed class PostAuthNavigationService(HttpClient http, ApiConfigService apiConfig, NavigationManager navigation)
{
    public string AuthenticationLink(string path)
    {
        var returnUrl = GetReturnUrl();
        return returnUrl is null ? path : QueryHelpers.AddQueryString(path, "returnUrl", returnUrl);
    }

    public async Task<string> ResolveAsync(bool resumeCurrentPage = false)
    {
        var returnUrl = GetReturnUrl();
        var currentFunnelRoute = resumeCurrentPage ? new Uri(navigation.Uri).PathAndQuery + new Uri(navigation.Uri).Fragment : null;
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{apiConfig.ApiBaseUrl}api/ResolvePostAuthDestination", new { ReturnUrl = returnUrl, CurrentFunnelRoute = currentFunnelRoute });
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<DestinationResponse>();
                if (PostAuthDestinationResolver.TryNormalizeLocalRoute(result?.Destination, out var destination))
                    return destination;
            }
        }
        catch (HttpRequestException) { }
        catch (System.Text.Json.JsonException) { }
        catch (OperationCanceledException) { }

        // Do not advance the account modal while persistence is unconfirmed.
        if (PostAuthDestinationResolver.TryNormalizeFunnelRoute(currentFunnelRoute, out _))
            throw new InvalidOperationException("Your account progress could not be saved. Please try again.");

        // Ordinary login can still fall back safely without inferring paid access.
        return await PostAuthDestinationResolver.ResolveAsync(string.Empty, returnUrl, UnavailableStore.Instance,
            currentFunnelRoute: currentFunnelRoute);
    }

    public async Task<bool> NavigateAsync(bool replace = true, bool resumeCurrentPage = false)
    {
        var destination = await ResolveAsync(resumeCurrentPage);
        // A modal may continue its existing flow only when the canonical policy
        // explicitly selects the very page already open.
        if (resumeCurrentPage && navigation.ToAbsoluteUri(destination) == new Uri(navigation.Uri)) return true;
        navigation.NavigateTo(destination, forceLoad: destination.Split('?', '#')[0].EndsWith(".html", StringComparison.OrdinalIgnoreCase), replace: replace);
        return false;
    }

    private sealed class DestinationResponse
    {
        public string? Destination { get; set; }
    }

    private string? GetReturnUrl()
    {
        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        return query.TryGetValue("returnUrl", out var value) ? value.FirstOrDefault() : null;
    }

    private sealed class UnavailableStore : IPostAuthRoutingStore
    {
        public static readonly UnavailableStore Instance = new();
        public Task<PostAuthUser?> FindUserAsync(string username) => Task.FromResult<PostAuthUser?>(null);
        public Task<bool> HasPaidSubscriptionAsync(string userId) => Task.FromResult(false);
        public Task SaveCurrentPageAsync(string username, string currentPage) => throw new InvalidOperationException("Server unavailable.");
    }
}
