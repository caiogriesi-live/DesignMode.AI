using System.Security.Claims;

namespace DesignMode.AI.Client.Services;

public static class WorkspaceSession
{
    public static string? GetEmail(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var expiry = user.FindFirst("exp")?.Value;
        if (expiry is not null && (!long.TryParse(expiry, out var seconds) || seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            return null;
        return new[] { user.FindFirst("email")?.Value, user.FindFirst(ClaimTypes.Email)?.Value,
            user.Identity.Name, user.FindFirst("unique_name")?.Value, user.FindFirst("name")?.Value }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? throw new InvalidOperationException("The signed-in account has no email address.");
    }
}
