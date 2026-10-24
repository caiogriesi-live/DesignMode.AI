using DesignMode.AI.Shared.Navigation;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

public enum FunnelRecoveryStep { Reminder1, Reminder2, FinalReminder }
public enum RecoveryMarketingPermission { Unavailable, Denied, Allowed }

// Canonical user-level recovery permission; unavailable reads fail closed.
public interface IFunnelRecoveryMarketingPermission
{
    Task<RecoveryMarketingPermission> CheckAsync(string canonicalUserId);
}

public sealed class UnavailableRecoveryMarketingPermission : IFunnelRecoveryMarketingPermission
{
    public Task<RecoveryMarketingPermission> CheckAsync(string canonicalUserId) =>
        Task.FromResult(RecoveryMarketingPermission.Unavailable);
}

public sealed record FunnelRecoveryTiming(TimeSpan Reminder1, TimeSpan Reminder2, TimeSpan Final)
{
    public const int MaximumAttempts = 3;
    public static readonly TimeSpan MinimumSpacing = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(6);

    public void Validate()
    {
        if (Reminder1 <= TimeSpan.Zero || Reminder2 <= Reminder1 || Final <= Reminder2)
            throw new InvalidOperationException("Recovery delays must be positive and increasing.");
    }

    public TimeSpan Delay(FunnelRecoveryStep step) => step switch
    {
        FunnelRecoveryStep.Reminder1 => Reminder1,
        FunnelRecoveryStep.Reminder2 => Reminder2,
        FunnelRecoveryStep.FinalReminder => Final,
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };
}

public interface IFunnelRecoverySequenceStore
{
    IAsyncEnumerable<string> FindCandidatesAsync(DateTimeOffset readyBefore);
    Task<FunnelRecoveryState?> ReadAsync(string id);
    Task<FunnelRecoveryContext?> ResolveRecoveryAsync(FunnelRecoveryState state);
    Task SuppressAsync(string id, string reason);
    Task<bool> ClaimReminderAsync(FunnelRecoveryState state, FunnelRecoveryStep step, DateTimeOffset now, DateTimeOffset readyBefore);
    Task MarkReminderSentAsync(string id, FunnelRecoveryStep step, string claim, string messageId, DateTimeOffset now);
    Task RejectReminderAsync(string id, string claim, DateTimeOffset now);
    Task SuppressSubscribedAsync(string userId, string subscriptionRecordId);
}

public sealed class FunnelRecoverySequence(IFunnelRecoverySequenceStore store, IFunnelRecoveryEmailSender sender,
    IFunnelRecoveryMarketingPermission marketing, FunnelRecoveryConfiguration configuration,
    ILogger<FunnelRecoverySequence> logger, FunnelRecoveryUnsubscribe? unsubscribe = null)
{
    public static FunnelRecoveryStep? Due(FunnelRecoveryState state, DateTimeOffset now, FunnelRecoveryTiming timing)
    {
        timing.Validate();
        if (state.SuppressedAt.HasValue || !state.VideoReadyEmailSentAt.HasValue || state.DeliveryStatus != "Sent" ||
            state.VideoReadyEmailSentAt < state.VideoReadyAt || state.VideoReadyEmailSentAt > now ||
            state.FinalReminderSentAt.HasValue || !string.IsNullOrEmpty(state.ReminderClaim) ||
            state.ReminderAttempts >= FunnelRecoveryTiming.MaximumAttempts || state.ReminderRetryAt > now) return null;
        if (state.Reminder2SentAt.HasValue && !state.Reminder1SentAt.HasValue) return null;
        if (state.Reminder1SentAt < state.VideoReadyEmailSentAt || state.Reminder1SentAt > now ||
            state.Reminder2SentAt < state.Reminder1SentAt || state.Reminder2SentAt > now) return null;
        var step = !state.Reminder1SentAt.HasValue ? FunnelRecoveryStep.Reminder1 :
            !state.Reminder2SentAt.HasValue ? FunnelRecoveryStep.Reminder2 : FunnelRecoveryStep.FinalReminder;
        // A late scheduler advances one step at a time, at least 24h apart.
        var prior = state.Reminder2SentAt ?? state.Reminder1SentAt;
        return now - state.VideoReadyAt >= timing.Delay(step) &&
            (!prior.HasValue || now - prior.Value >= FunnelRecoveryTiming.MinimumSpacing) ? step : null;
    }

    public async Task RunAsync(DateTimeOffset now)
    {
        if (!configuration.Enabled) return;
        var timing = configuration.Timing;
        timing.Validate();
        logger.LogInformation("[FUNNEL RECOVERY] scheduler started");
        var candidates = 0;
        await foreach (var id in store.FindCandidatesAsync(now - timing.Reminder1))
        {
            candidates++;
            try { await ProcessAsync(id, now, timing); }
            catch { logger.LogWarning("[FUNNEL RECOVERY] candidate evaluation failed"); }
        }
        logger.LogInformation("[FUNNEL RECOVERY] due candidates={Count}", candidates);
    }

    private async Task<bool> CheckAsync(FunnelRecoveryState state, FunnelRecoveryContext? context)
    {
        if (state.SuppressedAt.HasValue) return false;
        string? reason = context is null || FunnelRecoveryService.RecoveryId(context) != state.Id ? "CANONICAL_STATE_UNAVAILABLE" :
            context.Paid ? "SUBSCRIBED" : !context.Unfinished ? "FUNNEL_COMPLETED" :
            !FunnelRecoveryService.Eligible(context) ? "EMAIL_UNAVAILABLE" : null;
        if (reason is not null)
        {
            await store.SuppressAsync(state.Id, reason);
            logger.LogInformation("[FUNNEL RECOVERY] suppressed {Reason}", reason);
            return false;
        }
        if (!PostAuthDestinationResolver.TryNormalizeResumeRoute(context!.CurrentPage, out _)) return false;
        if (configuration.LocalTestMode && !configuration.CanSendLocalTestReminder(context.Email))
        {
            logger.LogInformation("[FUNNEL RECOVERY] local test recipient or environment not allowed");
            return false;
        }
        var permission = await marketing.CheckAsync(context.UserId);
        if (permission == RecoveryMarketingPermission.Unavailable)
        {
            if (configuration.CanSendLocalTestReminder(context.Email))
            {
                logger.LogInformation("[FUNNEL RECOVERY] local test delivery authorized");
                return true;
            }
            logger.LogWarning("[FUNNEL RECOVERY] MARKETING_SUPPRESSION_REQUIRED");
            return false;
        }
        if (permission != RecoveryMarketingPermission.Allowed)
        {
            await store.SuppressAsync(state.Id, "UNSUBSCRIBED");
            logger.LogInformation("[FUNNEL RECOVERY] suppressed marketing opt-out");
            return false;
        }
        return true;
    }

    public async Task ProcessAsync(string id, DateTimeOffset now, FunnelRecoveryTiming timing)
    {
        if (!configuration.Enabled) return;
        var state = await store.ReadAsync(id);
        if (state is null || state.SuppressedAt.HasValue || state.FinalReminderSentAt.HasValue) return;
        var context = await store.ResolveRecoveryAsync(state);
        // Suppression is checked even for held claims or exhausted retries.
        if (!await CheckAsync(state, context)) return;
        var step = Due(state, now, timing);
        if (!step.HasValue) return;
        _ = FunnelRecoveryEmail.CreateReminder(step.Value, configuration.HostUrl, context!.CurrentPage, configuration.AllowLocalLinks);
        _ = unsubscribe?.CreateUrl(configuration.HostUrl, state.Id, configuration.AllowLocalLinks);
        var claim = Guid.NewGuid().ToString("N");
        if (!await store.ClaimReminderAsync(state with { ReminderClaim = claim }, step.Value, now, now - timing.Delay(step.Value))) return;
        string messageId;
        try
        {
            state = await store.ReadAsync(id);
            if (state is null || state.ReminderClaim != claim) return;
            context = await store.ResolveRecoveryAsync(state);
            if (!await CheckAsync(state, context))
            {
                await store.RejectReminderAsync(id, claim, now);
                return;
            }
            // Reread prior/sent state after the durable claim as well as canonical eligibility.
            if (Due(state with { ReminderClaim = null, ReminderAttempts = 0, ReminderRetryAt = null }, now, timing) != step)
            {
                await store.RejectReminderAsync(id, claim, now);
                return;
            }
        }
        catch
        {
            await store.RejectReminderAsync(id, claim, now);
            throw;
        }
        try
        {
            logger.LogInformation("[RECOVERY EMAIL] selected concept resolved={Resolved}", context!.SelectedImageUrl is not null);
            var message = FunnelRecoveryEmail.CreateReminder(step.Value, configuration.HostUrl, context.CurrentPage,
                configuration.AllowLocalLinks, context.SelectedImageUrl,
                unsubscribe?.CreateUrl(configuration.HostUrl, state.Id, configuration.AllowLocalLinks));
            logger.LogInformation("[RECOVERY EMAIL] template rendered");
            if (configuration.LocalTestMode)
            {
                if (!configuration.CanSendLocalTestReminder(context.Email))
                {
                    await store.RejectReminderAsync(id, claim, now);
                    return;
                }
            }
            messageId = await sender.SendAsync(context.Email, message);
        }
        catch (FunnelRecoveryEmailRejectedException)
        {
            await store.RejectReminderAsync(id, claim, now);
            logger.LogWarning("[FUNNEL RECOVERY] delivery rejected; bounded retry scheduled");
            return;
        }
        catch
        {
            logger.LogWarning("[FUNNEL RECOVERY] delivery outcome unknown; claim retained for review");
            return;
        }
        await store.MarkReminderSentAsync(id, step.Value, claim, messageId, now);
        logger.LogInformation("[FUNNEL RECOVERY] {Step} sent", step.Value);
    }

    public async Task SuppressSubscribedSafelyAsync(string? userId, string subscriptionRecordId)
    {
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(userId)) return;
        try { await store.SuppressSubscribedAsync(userId, subscriptionRecordId); }
        catch { logger.LogWarning("[FUNNEL RECOVERY] subscription suppression deferred to scheduler"); }
    }
}
