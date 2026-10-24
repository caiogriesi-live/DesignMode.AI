namespace DesignMode.AI.Shared.Navigation;

public sealed record PostAuthUser(string Id, string? CurrentPage);

public interface IPostAuthRoutingStore
{
    Task<PostAuthUser?> FindUserAsync(string username);
    Task<bool> HasPaidSubscriptionAsync(string userId);
    Task SaveCurrentPageAsync(string username, string currentPage);
}

public static class PostAuthDestinationResolver
{
    public const string DefaultDestination = "/social-landing.html";
    private static readonly string[] AuthRoutes = ["/login", "/loginbak", "/signup", "/logout", "/forgot"];

    public static async Task<string> ResolveAsync(
        string username, string? returnUrl, IPostAuthRoutingStore store, Action<string>? log = null,
        string? currentFunnelRoute = null)
    {
        log?.Invoke("[AUTH ROUTING] resolving destination");
        if (TryNormalizeLocalRoute(returnUrl, out var explicitDestination))
        {
            log?.Invoke("[AUTH ROUTING] destination=return-url");
            return explicitDestination;
        }

        PostAuthUser? user = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(username)) user = await store.FindUserAsync(username);
        }
        catch
        {
            log?.Invoke("[AUTH ROUTING] user lookup failed");
        }

        if (user is not null)
        {
            if (TryNormalizeResumeRoute(user.CurrentPage, out var currentPage))
            {
                log?.Invoke("[AUTH ROUTING] destination=current-page");
                return currentPage;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(user.Id) && await store.HasPaidSubscriptionAsync(user.Id))
                {
                    log?.Invoke("[AUTH ROUTING] destination=dashboard");
                    return "/init/dashboard";
                }
            }
            catch
            {
                log?.Invoke("[AUTH ROUTING] subscription lookup failed");
            }
        }

        // An account modal inside DesignerIntro is already partway through the
        // funnel, even when its progress has not yet reached Users.CurrentPage.
        if (TryNormalizeFunnelRoute(currentFunnelRoute, out var funnelRoute))
        {
            log?.Invoke("[AUTH ROUTING] destination=current-funnel");
            return funnelRoute;
        }

        log?.Invoke("[AUTH ROUTING] destination=social-landing");
        return DefaultDestination;
    }

    public static bool TryNormalizeLocalRoute(string? value, out string destination)
    {
        destination = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (!candidate.StartsWith('/') || candidate.StartsWith("//") ||
            candidate.Contains('\\') || candidate.Any(char.IsControl) || candidate.Any(char.IsWhiteSpace)) return false;

        // The outer query is already decoded. Keep escaped query values intact.
        for (var i = 0; i < candidate.Length; i++)
        {
            if (candidate[i] != '%') continue;
            if (i + 2 >= candidate.Length || !Uri.IsHexDigit(candidate[i + 1]) || !Uri.IsHexDigit(candidate[i + 2])) return false;
            i += 2;
        }

        var decoded = Uri.UnescapeDataString(candidate);
        if (decoded.Any(char.IsControl) || decoded.Contains('\\')) return false;
        var path = decoded.Split('?', '#')[0];
        // Prevent authority changes and path normalization bypasses of auth-loop checks.
        if (path.StartsWith("//") || path.Contains('%') || path.Contains(':') ||
            path.Split('/').Any(segment => segment is "." or "..")) return false;
        path = path.TrimEnd('/');
        if (AuthRoutes.Any(route => string.Equals(path, route, StringComparison.OrdinalIgnoreCase)) ||
            path.Contains("callback", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("error", StringComparison.OrdinalIgnoreCase)) return false;

        if (!Uri.TryCreate(new Uri("https://designmode.invalid"), candidate, out var local) ||
            local.Host != "designmode.invalid") return false;
        destination = candidate;
        return true;
    }

    public static bool TryNormalizeResumeRoute(string? value, out string destination) =>
        TryNormalizeLocalRoute(value, out destination);

    public static bool TryNormalizeFunnelRoute(string? value, out string destination) =>
        TryNormalizeLocalRoute(value, out destination) &&
        Uri.UnescapeDataString(destination.Split('?', '#')[0]).StartsWith("/designerintro", StringComparison.OrdinalIgnoreCase);
}
