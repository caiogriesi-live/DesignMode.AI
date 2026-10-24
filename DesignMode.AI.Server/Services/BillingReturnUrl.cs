using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Server.Services;

public static class BillingReturnUrl
{
    public static string Dashboard(string? origin, bool test, IConfiguration config)
    {
        var allowed = (config["BILLING_ALLOWED_ORIGINS"] ?? config["Values:BILLING_ALLOWED_ORIGINS"] ?? "https://designmode.ai,https://www.designmode.ai")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !(test && uri.IsLoopback && uri.Scheme is "http" or "https" || uri.Scheme == "https" && allowed.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Unsupported billing return origin.");
        return uri.GetLeftPart(UriPartial.Authority) + "/init/dashboard";
    }
}
