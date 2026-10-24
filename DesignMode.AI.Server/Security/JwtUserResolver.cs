using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace DesignMode.AI.Server.Security;

public sealed class JwtUserResolver
{
    public string? ResolveUsername(HttpRequest request)
        => ResolvePrincipal(request)?.FindFirstValue(ClaimTypes.Name);

    public string? ResolveUserId(HttpRequest request)
        => ResolvePrincipal(request)?.FindFirstValue(ClaimTypes.NameIdentifier);

    public ClaimsPrincipal? ResolvePrincipal(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var secret = Environment.GetEnvironmentVariable("JwtSecretKey");
        if (string.IsNullOrWhiteSpace(secret))
            return null;

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(
                authorization["Bearer ".Length..].Trim(),
                new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                    ValidateIssuer = true,
                    ValidIssuer = "DesignModeAI",
                    ValidateAudience = true,
                    ValidAudience = "DesignModeFunctions",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                },
                out _);

            return principal;
        }
        catch
        {
            return null;
        }
    }
}
