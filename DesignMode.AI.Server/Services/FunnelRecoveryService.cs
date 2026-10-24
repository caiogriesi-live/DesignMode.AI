using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using DesignMode.AI.Shared.Navigation;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

public sealed record FunnelVideoReadyEvent(string JobId, DateTimeOffset ObservedAt);

// No recipient, content, URL, or provider payload is persisted here.
public sealed record FunnelRecoveryState(string Id, string UserId, string FunnelId, string ProjectId,
    DateTimeOffset VideoReadyAt, DateTimeOffset? VideoReadyEmailSentAt = null,
    DateTimeOffset? SuppressedAt = null, string DeliveryStatus = "Ready", int Attempts = 0,
    DateTimeOffset? Reminder1SentAt = null, DateTimeOffset? Reminder2SentAt = null,
    DateTimeOffset? FinalReminderSentAt = null, string? SuppressionReason = null,
    string? ReminderClaim = null, int ReminderAttempts = 0, DateTimeOffset? ReminderRetryAt = null);

public sealed record FunnelRecoveryContext(string UserId, string FunnelId, string ProjectId,
    string Email, string? CurrentPage, bool VideoReady, bool Paid, bool Unfinished, string? SelectedImageUrl = null);

public interface IFunnelRecoveryStore
{
    Task<FunnelRecoveryContext?> ResolveAsync(string jobId);
    Task<FunnelRecoveryState> RecordReadyAsync(FunnelRecoveryContext context, DateTimeOffset readyAt);
    Task<FunnelRecoveryState?> ReadAsync(string id);
    Task<bool> ClaimAsync(string id);
    Task SuppressAsync(string id);
    Task MarkSentAsync(string id, string messageId);
    Task RejectAsync(string id);
}

public interface IFunnelRecoveryEmailSender
{
    Task<string> SendAsync(string email, FunnelRecoveryEmail message);
}

// Only a definitive rejection is safe to retry. Timeouts and transport failures
// may have happened after SES accepted the email and must retain the claim.
public sealed class FunnelRecoveryEmailRejectedException : Exception { }

public sealed class FunnelRecoveryService(IFunnelRecoveryStore store, IFunnelRecoveryEmailSender sender,
    ILogger<FunnelRecoveryService> logger, FunnelRecoveryUnsubscribe? unsubscribe = null)
{
    public static string RecoveryId(FunnelRecoveryContext context) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"video-ready:{context.UserId}:{context.FunnelId}:{context.ProjectId}")));

    public static bool IsUnfinished(string? currentPage)
    {
        if (!PostAuthDestinationResolver.TryNormalizeResumeRoute(currentPage, out var route)) return true;
        var path = Uri.UnescapeDataString(route.Split('?', '#')[0]).TrimEnd('/').ToLowerInvariant();
        // Existing pre-checkout routes. Success, Gen, dashboard and all other
        // post-preview destinations fail closed even before subscription sync.
        return path is "/designerintro" or "/designerintro2" or "/init/mainconcept"
            or "/init/funnelend" or "/init/upgrade";
    }

    public static bool Eligible(FunnelRecoveryContext? context) => context is not null &&
        context.VideoReady && context.Unfinished && !context.Paid &&
        MailAddress.TryCreate(context.Email, out var address) && address.Address == context.Email &&
        !string.IsNullOrWhiteSpace(context.UserId) && !string.IsNullOrWhiteSpace(context.FunnelId) &&
        !string.IsNullOrWhiteSpace(context.ProjectId);

    public async Task ProcessAsync(FunnelVideoReadyEvent notification, bool enabled, string? hostUrl, bool allowLocalLinks = false)
    {
        if (!enabled)
        {
            logger.LogInformation("[FUNNEL RECOVERY] skipped reason=FEATURE_DISABLED");
            return;
        }
        var context = await store.ResolveAsync(notification.JobId);
        if (context is null || !context.VideoReady)
        {
            logger.LogInformation("[FUNNEL RECOVERY] skipped reason=CANONICAL_VIDEO_UNAVAILABLE");
            return;
        }
        var state = await store.RecordReadyAsync(context, notification.ObservedAt);
        logger.LogInformation("[FUNNEL RECOVERY] recovery state persisted");
        if (state.VideoReadyEmailSentAt.HasValue || state.SuppressedAt.HasValue) return;
        if (!Eligible(context))
        {
            await store.SuppressAsync(state.Id);
            logger.LogInformation("[FUNNEL RECOVERY] eligible=false");
            return;
        }
        // Validate configuration before taking a durable delivery claim.
        _ = FunnelRecoveryEmail.Create(hostUrl, context.CurrentPage, allowLocalLinks);
        _ = unsubscribe?.CreateUrl(hostUrl, state.Id, allowLocalLinks);
        if (!await store.ClaimAsync(state.Id)) return;

        // A queued job can outlive checkout or an administrative suppression.
        try
        {
            context = await store.ResolveAsync(notification.JobId);
            state = await store.ReadAsync(state.Id) ?? throw new InvalidOperationException("Recovery state missing.");
        }
        catch
        {
            // No email request has started, so a bounded retry is safe here.
            await store.RejectAsync(state.Id);
            throw;
        }
        if (!Eligible(context) || RecoveryId(context!) != state.Id ||
            state.SuppressedAt.HasValue || state.VideoReadyEmailSentAt.HasValue || state.DeliveryStatus != "Sending")
        {
            await store.SuppressAsync(state.Id);
            logger.LogInformation("[FUNNEL RECOVERY] eligible=false");
            return;
        }
        logger.LogInformation("[FUNNEL RECOVERY] eligible=true");
        string messageId;
        try
        {
            logger.LogInformation("[RECOVERY EMAIL] selected concept resolved={Resolved}", context!.SelectedImageUrl is not null);
            var message = FunnelRecoveryEmail.Create(hostUrl, context.CurrentPage, allowLocalLinks, context.SelectedImageUrl,
                unsubscribe?.CreateUrl(hostUrl, state.Id, allowLocalLinks));
            logger.LogInformation("[RECOVERY EMAIL] template rendered");
            logger.LogInformation("[FUNNEL RECOVERY] email send started");
            messageId = await sender.SendAsync(context.Email, message);
        }
        catch (FunnelRecoveryEmailRejectedException)
        {
            await store.RejectAsync(state.Id);
            logger.LogWarning("[FUNNEL RECOVERY] email rejected");
            throw;
        }
        catch
        {
            logger.LogWarning("[FUNNEL RECOVERY] email outcome unknown; delivery claim retained");
            return;
        }
        // If this write fails, the claim remains held. Never resend an accepted email.
        await store.MarkSentAsync(state.Id, messageId);
        logger.LogInformation("[FUNNEL RECOVERY] video-ready email sent");
    }
}
