using System.Net;
using Amazon;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using DesignMode.AI.Shared.Navigation;

namespace DesignMode.AI.Server.Services;

public sealed record FunnelRecoveryEmail(string Subject, string Html, string Text, string ResumeUrl)
{
    public static FunnelRecoveryEmail CreateReminder(FunnelRecoveryStep step, string? hostUrl,
        string? currentPage, bool allowLocalLinks = false, string? selectedImageUrl = null, string? unsubscribeUrl = null)
    {
        var url = Create(hostUrl, currentPage, allowLocalLinks).ResumeUrl;
        var (subject, body) = step switch
        {
            FunnelRecoveryStep.Reminder1 => ("Your DesignMode preview is waiting",
                "Your DesignMode preview is ready and waiting. Pick up where you left off and continue your design."),
            FunnelRecoveryStep.Reminder2 => ("Continue building your brand with DesignMode",
                "Start from your generated concept and take the next step toward your brand system. Your design is ready to continue."),
            FunnelRecoveryStep.FinalReminder => ("Finish your DesignMode project",
                "This is our final reminder about your preview. Whenever you are ready, you can return and continue your design."),
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };
        var headline = step switch
        {
            FunnelRecoveryStep.Reminder1 => "Your preview is waiting",
            FunnelRecoveryStep.Reminder2 => "Keep building your brand",
            _ => "Continue your DesignMode project"
        };
        return RecoveryEmailLayout.Render(subject, headline, body, "Continue your design", url, selectedImageUrl, unsubscribeUrl);
    }

    public static FunnelRecoveryEmail Create(string? hostUrl, string? currentPage, bool allowLocalLinks = false, string? selectedImageUrl = null, string? unsubscribeUrl = null)
    {
        if (!Uri.TryCreate(hostUrl, UriKind.Absolute, out var host) ||
            !string.IsNullOrEmpty(host.UserInfo) || host.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(host.Query) || !string.IsNullOrEmpty(host.Fragment))
            throw new InvalidOperationException("Funnel recovery requires a valid application HostUrl origin.");
        var localOrigin = allowLocalLinks && host.IsLoopback && host.Scheme is "http" or "https";
        var publicOrigin = host.Scheme == "https" && !host.IsLoopback &&
            host.HostNameType == UriHostNameType.Dns && host.IsDefaultPort;
        if (!localOrigin && !publicOrigin)
            throw new InvalidOperationException("Funnel recovery requires HTTPS; loopback origins are allowed only in local development.");
        var route = PostAuthDestinationResolver.TryNormalizeResumeRoute(currentPage, out var safe)
            ? safe : "/designerintro";
        var parts = route.Split('?', 2);
        var path = Uri.UnescapeDataString(parts[0]).TrimEnd('/');
        if (parts.Length == 2 && (path.Equals("/init/mainconcept", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/init/funnelend", StringComparison.OrdinalIgnoreCase)))
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(parts[1]);
            if (query.TryGetValue("id", out var invite) && !string.IsNullOrWhiteSpace(invite) &&
                query.TryGetValue("e", out var email) && !string.IsNullOrWhiteSpace(email))
                // Preserve the saved funnel parameters inside the login return URL.
                route = $"/init/funnelend?{parts[1]}";
        }
        var url = $"{host.GetLeftPart(UriPartial.Authority)}/login?returnUrl={Uri.EscapeDataString(route)}";
        const string body = "Your design preview is ready. Come back to see the generated concept and continue building your brand.";
        return RecoveryEmailLayout.Render("Your DesignMode preview is ready", "Your preview is ready!",
            body, "View your preview", url, selectedImageUrl, unsubscribeUrl);
    }
}

public sealed class SesFunnelRecoveryEmailSender(FunnelRecoveryConfiguration configuration) : IFunnelRecoveryEmailSender
{
    public async Task<string> SendAsync(string email, FunnelRecoveryEmail message)
    {
        var from = configuration.Get("SES_FROM_EMAIL");
        var region = configuration.Get("AWS_REGION");
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(region))
            throw new FunnelRecoveryEmailRejectedException();
        using var client = new AmazonSimpleEmailServiceClient(
            configuration.Get("AWS_ACCESS_KEY_ID"),
            configuration.Get("AWS_SECRET_ACCESS_KEY"),
            new AmazonSimpleEmailServiceConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region),
                MaxErrorRetry = 0, Timeout = TimeSpan.FromSeconds(20) });
        try
        {
            var result = await client.SendEmailAsync(new SendEmailRequest
            {
                Source = from,
                Destination = new Destination { ToAddresses = [email] },
                Message = new Message
                {
                    Subject = new Content(message.Subject),
                    Body = new Body { Html = new Content(message.Html), Text = new Content(message.Text) }
                }
            });
            return result.MessageId;
        }
        catch (AmazonSimpleEmailServiceException ex) when ((int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500)
        {
            // A received 4xx response is a definite rejection, unlike timeout/5xx.
            throw new FunnelRecoveryEmailRejectedException();
        }
    }
}
