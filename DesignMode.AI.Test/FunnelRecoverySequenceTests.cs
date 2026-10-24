using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoverySequenceTests
{
    private static readonly DateTimeOffset ReadyAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FunnelRecoveryTiming Timing = new(TimeSpan.FromHours(24), TimeSpan.FromHours(72), TimeSpan.FromHours(168));
    private static FunnelRecoveryContext Context => new("canonical-user", "funnel", "project", "canonical@example.test", "/init/mainconcept", true, false, true);
    private static FunnelRecoveryState Initial => new(FunnelRecoveryService.RecoveryId(Context), Context.UserId,
        Context.FunnelId, Context.ProjectId, ReadyAt, ReadyAt, DeliveryStatus: "Sent");

    [DataTestMethod]
    [DataRow(23, 0, -1)]
    [DataRow(24, 0, 0)]
    [DataRow(71, 1, -1)]
    [DataRow(72, 1, 1)]
    [DataRow(167, 2, -1)]
    [DataRow(168, 2, 2)]
    [DataRow(240, 3, -1)]
    public void Due_UsesReadinessAndOrderedFiniteSteps(int hours, int completed, int expected)
    {
        var result = FunnelRecoverySequence.Due(AtStep(completed), ReadyAt.AddHours(hours), Timing);
        Assert.AreEqual(expected, result.HasValue ? (int)result.Value : -1);
    }

    private static FunnelRecoveryState AtStep(int completed) => Initial with
    {
        Reminder1SentAt = completed >= 1 ? ReadyAt.AddHours(24) : null,
        Reminder2SentAt = completed >= 2 ? ReadyAt.AddHours(72) : null,
        FinalReminderSentAt = completed >= 3 ? ReadyAt.AddHours(168) : null
    };

    [TestMethod]
    public void InconsistentOrHeldState_IsNotDue()
    {
        foreach (var state in new[]
        {
            Initial with { VideoReadyEmailSentAt = null },
            Initial with { DeliveryStatus = "Sending" },
            Initial with { VideoReadyEmailSentAt = ReadyAt.AddMinutes(-1) },
            Initial with { Reminder2SentAt = ReadyAt.AddHours(72) },
            Initial with { Reminder1SentAt = ReadyAt.AddMinutes(-1) },
            Initial with { ReminderClaim = "durable-claim" },
            Initial with { ReminderAttempts = 3 },
            Initial with { SuppressedAt = ReadyAt },
            Initial with { ReminderRetryAt = ReadyAt.AddDays(10) }
        }) Assert.IsNull(FunnelRecoverySequence.Due(state, ReadyAt.AddDays(7), Timing));
    }

    [TestMethod]
    public void LateWorker_DoesNotBurstCatchUpReminders()
    {
        var state = Initial with { Reminder1SentAt = ReadyAt.AddDays(10) };
        Assert.IsNull(FunnelRecoverySequence.Due(state, ReadyAt.AddDays(10).AddHours(23), Timing));
        Assert.AreEqual(FunnelRecoveryStep.Reminder2, FunnelRecoverySequence.Due(state, ReadyAt.AddDays(11), Timing));
    }

    [DataTestMethod]
    [DataRow(0, "Your DesignMode preview is waiting")]
    [DataRow(1, "Continue building your brand with DesignMode")]
    [DataRow(2, "Finish your DesignMode project")]
    public async Task DueReminder_UsesTemplateAndCurrentSafeResume(int completed, string subject)
    {
        var store = new Store { State = AtStep(completed), Current = Context with
        { CurrentPage = "/init/upgrade?source=resume", SelectedImageUrl = "https://cdn.designmode.ai/selected.png" } };
        var sender = new Sender();
        var permission = new Permission();
        await Service(store, sender, permission).RunAsync(ReadyAt.AddDays(7));
        Assert.AreEqual(1, sender.Messages.Count);
        Assert.AreEqual(subject, sender.Messages[0].Subject);
        Assert.AreEqual(FunnelRecoveryEmail.Create("https://app.example.test", store.Current.CurrentPage).ResumeUrl, sender.Messages[0].ResumeUrl);
        StringAssert.Contains(sender.Messages[0].Text, "Continue your design");
        StringAssert.Contains(sender.Messages[0].Html, "https://cdn.designmode.ai/selected.png");
        StringAssert.Contains(sender.Messages[0].Html, "email-play-overlay");
        Assert.AreEqual("canonical-user", permission.UserId);
        Assert.AreEqual(Context.Email, sender.Recipient);
        Assert.AreEqual(2, store.Resolutions);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task SubscriptionBeforeEachStep_PermanentlySuppresses(int completed)
    {
        var store = new Store { State = AtStep(completed), Current = Context with { Paid = true } };
        var sender = new Sender();
        await Service(store, sender).RunAsync(ReadyAt.AddDays(7));
        Assert.AreEqual("SUBSCRIBED", store.State!.SuppressionReason);
        Assert.AreEqual(0, sender.Messages.Count);
        store.Current = Context;
        await Service(store, sender).RunAsync(ReadyAt.AddDays(8));
        Assert.AreEqual(0, sender.Messages.Count);
    }

    [TestMethod]
    public async Task CompletedFunnel_SuppressesWithoutSubscription()
    {
        var store = new Store { Current = Context with { Unfinished = false } };
        var sender = new Sender();
        await Service(store, sender).RunAsync(ReadyAt.AddDays(1));
        Assert.AreEqual("FUNNEL_COMPLETED", store.State!.SuppressionReason);
        Assert.AreEqual(0, sender.Messages.Count);
    }

    [DataTestMethod]
    [DataRow(RecoveryMarketingPermission.Denied)]
    [DataRow(RecoveryMarketingPermission.Unavailable)]
    public async Task MissingOrDeniedMarketingPermission_NeverSends(RecoveryMarketingPermission permission)
    {
        var store = new Store();
        var sender = new Sender();
        await Service(store, sender, new Permission { Value = permission }).RunAsync(ReadyAt.AddDays(1));
        Assert.AreEqual(0, sender.Messages.Count);
        Assert.AreEqual(0, store.Claims);
        Assert.AreEqual(permission == RecoveryMarketingPermission.Denied ? "UNSUBSCRIBED" : null, store.State!.SuppressionReason);
        Assert.AreEqual(RecoveryMarketingPermission.Unavailable, await new UnavailableRecoveryMarketingPermission().CheckAsync(Context.UserId));
    }

    [DataTestMethod]
    [DataRow("subscription")]
    [DataRow("completion")]
    [DataRow("optout")]
    [DataRow("suppression")]
    [DataRow("email")]
    [DataRow("identity")]
    [DataRow("route")]
    public async Task ChangesAfterClaim_AreRechecked(string change)
    {
        var store = new Store();
        var sender = new Sender();
        var permission = new Permission();
        store.AfterClaim = () =>
        {
            switch (change)
            {
                case "subscription": store.Current = Context with { Paid = true }; break;
                case "completion": store.Current = Context with { Unfinished = false }; break;
                case "optout": permission.Value = RecoveryMarketingPermission.Denied; break;
                case "suppression": store.State = store.State! with { SuppressedAt = ReadyAt }; break;
                case "email": store.Current = Context with { Email = "" }; break;
                case "identity": store.Current = Context with { UserId = "other" }; break;
                case "route": store.Current = Context with { CurrentPage = "https://evil.example" }; break;
            }
        };
        await Service(store, sender, permission).RunAsync(ReadyAt.AddDays(1));
        Assert.AreEqual(0, sender.Messages.Count);
    }

    [TestMethod]
    public async Task DuplicateAndOverlappingWorkers_SendOnce()
    {
        var store = new Store();
        var sender = new Sender { Yield = true };
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Service(store, sender).RunAsync(ReadyAt.AddDays(1))));
        await Service(store, sender).RunAsync(ReadyAt.AddDays(1));
        Assert.AreEqual(1, sender.Messages.Count);
        Assert.AreEqual(1, store.Claims);
        Assert.IsNotNull(store.State!.Reminder1SentAt);
    }

    [TestMethod]
    public async Task DefiniteRejection_RetriesSixHoursApartAtMostThreeTimes()
    {
        var store = new Store();
        var sender = new Sender { Failure = new FunnelRecoveryEmailRejectedException() };
        var service = Service(store, sender);
        for (var hour = 24; hour < 72; hour++) await service.RunAsync(ReadyAt.AddHours(hour));
        Assert.AreEqual(3, sender.Attempts);
        Assert.IsNull(store.State!.Reminder1SentAt);
        Assert.IsNull(store.State.ReminderClaim);
        Assert.AreEqual(3, store.State.ReminderAttempts);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnknownOutcomeOrFailedFinalization_RetainsClaim(bool finalizationFailure)
    {
        var store = new Store { FailMarkSent = finalizationFailure };
        var sender = new Sender { Failure = finalizationFailure ? null : new TimeoutException() };
        var service = Service(store, sender);
        await service.RunAsync(ReadyAt.AddDays(1));
        await service.RunAsync(ReadyAt.AddDays(2));
        Assert.AreEqual(1, sender.Attempts);
        Assert.IsNotNull(store.State!.ReminderClaim);
        Assert.IsNull(store.State.Reminder1SentAt);
    }

    [TestMethod]
    public async Task HistoricalFunnelWithoutRecoveryState_IsNotEnrolled()
    {
        var store = new Store { State = null };
        var sender = new Sender();
        await Service(store, sender).RunAsync(ReadyAt.AddDays(30));
        Assert.IsNull(store.State);
        Assert.AreEqual(0, store.Resolutions);
        Assert.AreEqual(0, sender.Attempts);
    }

    [TestMethod]
    public async Task DisabledFlag_PerformsNoWork()
    {
        var store = new Store();
        var sender = new Sender();
        await Service(store, sender, enabled: false).RunAsync(ReadyAt.AddDays(1));
        Assert.AreEqual(0, store.Scans);
        Assert.AreEqual(0, sender.Attempts);
    }

    [TestMethod]
    public async Task SubscriptionHookFailure_DoesNotFailBillingCaller()
    {
        var store = new Store { FailSuppression = true };
        await Service(store, new Sender()).SuppressSubscribedSafelyAsync(Context.UserId, "subscription");
        Assert.AreEqual(1, store.SubscriptionCallbacks);
    }

    [TestMethod]
    public async Task ProductionPermissionAdapter_BlocksDeliveryEvenWithFlagEnabled()
    {
        var store = new Store();
        var sender = new Sender();
        var service = new FunnelRecoverySequence(store, sender, new UnavailableRecoveryMarketingPermission(), Config(),
            NullLogger<FunnelRecoverySequence>.Instance);
        await service.RunAsync(ReadyAt.AddDays(30));
        Assert.AreEqual(0, sender.Attempts);
        Assert.AreEqual(0, store.Claims);
    }

    [TestMethod]
    public void Configuration_DefaultsOverridesAndInvalidOrdering()
    {
        var configuration = Config();
        Assert.AreEqual(Timing, configuration.Timing);
        var custom = new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Values:FUNNEL_RECOVERY_REMINDER1_DELAY_HOURS"] = "48" }).Build());
        Assert.AreEqual(TimeSpan.FromHours(48), custom.Timing.Reminder1);
        Assert.ThrowsException<InvalidOperationException>(() => new FunnelRecoveryTiming(TimeSpan.FromDays(3), TimeSpan.FromDays(1), TimeSpan.FromDays(7)).Validate());
    }

    private static FunnelRecoveryConfiguration Config(bool enabled = true) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["FUNNEL_RECOVERY_EMAIL_ENABLED"] = enabled.ToString(), ["HostUrl"] = "https://app.example.test" }).Build());

    private static FunnelRecoverySequence ProductionService(Store store, Sender sender, FunnelRecoveryUnsubscribeTests.DynamoStub db)
    {
        var config = new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Production",
                ["FUNNEL_RECOVERY_EMAIL_ENABLED"] = "true", ["HostUrl"] = "https://www.designmode.ai",
                ["JwtSecretKey"] = "test-only-signing-secret" }).Build());
        var permission = new FunnelRecoveryUnsubscribe(config, new FunnelRecoveryUnsubscribeTests.Store(), () => db);
        return new(store, sender, permission, config, NullLogger<FunnelRecoverySequence>.Instance, permission);
    }

    [TestMethod]
    public async Task ProductionCanonicalPermission_SendsAt24_72_168Hours_OnlyOncePerStep()
    {
        var store = new Store();
        var sender = new Sender();
        var service = ProductionService(store, sender, new());
        foreach (var (hours, count) in new[] { (23, 0), (24, 1), (71, 1), (72, 2), (167, 2), (168, 3), (240, 3) })
        {
            await service.RunAsync(ReadyAt.AddHours(hours));
            await service.RunAsync(ReadyAt.AddHours(hours));
            Assert.AreEqual(count, sender.Attempts, $"At hour {hours}");
        }
        Assert.AreEqual(ReadyAt.AddHours(24), store.State!.Reminder1SentAt);
        Assert.AreEqual(ReadyAt.AddHours(72), store.State.Reminder2SentAt);
        Assert.AreEqual(ReadyAt.AddHours(168), store.State.FinalReminderSentAt);
        Assert.AreEqual(Context.Email, sender.Recipient);
        foreach (var message in sender.Messages)
        {
            Assert.IsFalse(message.Subject.Contains("[LOCAL TEST]"));
            StringAssert.StartsWith(message.ResumeUrl, "https://www.designmode.ai/");
            StringAssert.Contains(message.Text, "https://www.designmode.ai/unsubscribe?token=");
        }
    }

    [DataTestMethod]
    [DataRow(0, "unsubscribed")]
    [DataRow(1, "unsubscribed")]
    [DataRow(2, "unsubscribed")]
    [DataRow(0, "missing")]
    [DataRow(0, "read-failure")]
    [DataRow(0, "unsubscribe-after-claim")]
    [DataRow(1, "unsubscribe-after-claim")]
    [DataRow(2, "unsubscribe-after-claim")]
    [DataRow(0, "read-failure-after-claim")]
    public async Task ProductionCanonicalPermission_BlocksUnsafeDelivery(int completed, string scenario)
    {
        var store = new Store { State = AtStep(completed) };
        var sender = new Sender();
        var db = new FunnelRecoveryUnsubscribeTests.DynamoStub
        {
            Unsubscribed = scenario == "unsubscribed", MissingUser = scenario == "missing", FailRead = scenario == "read-failure"
        };
        if (scenario == "unsubscribe-after-claim") store.AfterClaim = () => db.Unsubscribed = true;
        if (scenario == "read-failure-after-claim") store.AfterClaim = () => db.FailRead = true;
        await ProductionService(store, sender, db).RunAsync(ReadyAt.AddDays(7));
        Assert.AreEqual(0, sender.Attempts);
        Assert.IsNull(store.State!.ReminderClaim);
        if (scenario.Contains("unsubscrib")) Assert.AreEqual("UNSUBSCRIBED", store.State.SuppressionReason);
        else Assert.IsNull(store.State.SuppressionReason);
    }

    [DataTestMethod]
    [DataRow("allowed", 1)]
    [DataRow("other-inbox", 0)]
    [DataRow("optout", 0)]
    [DataRow("subscribed", 0)]
    [DataRow("completed", 0)]
    [DataRow("suppressed", 0)]
    [DataRow("changed-after-claim", 0)]
    [DoNotParallelize]
    public async Task LocalTestDelivery_RespectsInboxAndSuppression(string scenario, int expected)
    {
        var settings = FunnelRecoveryLocalDeliveryTests.Settings();
        var keys = new[] { "USE_LOCAL_DYNAMODB", "LOCAL_DYNAMODB_URL", "WEBSITE_INSTANCE_ID", "WEBSITE_SITE_NAME" };
        var original = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, settings.GetValueOrDefault(key));
            var store = new Store();
            if (scenario == "other-inbox") store.Current = Context with { Email = "other@example.test" };
            if (scenario == "subscribed") store.Current = Context with { Paid = true };
            if (scenario == "completed") store.Current = Context with { Unfinished = false };
            if (scenario == "suppressed") store.State = Initial with { SuppressedAt = ReadyAt };
            if (scenario == "changed-after-claim") store.AfterClaim = () => store.Current = Context with { Email = "other@example.test" };
            var sender = new Sender();
            var permission = new Permission { Value = scenario == "optout" ? RecoveryMarketingPermission.Denied : RecoveryMarketingPermission.Unavailable };
            var config = new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            var service = new FunnelRecoverySequence(store, sender, permission, config, NullLogger<FunnelRecoverySequence>.Instance);
            await service.RunAsync(ReadyAt.AddDays(1));
            await service.RunAsync(ReadyAt.AddDays(1));
            Assert.AreEqual(expected, sender.Attempts);
            if (expected == 1)
            {
                Assert.AreEqual("canonical@example.test", sender.Recipient);
                Assert.AreEqual("Your DesignMode preview is waiting", sender.Messages.Single().Subject);
                Assert.IsNotNull(store.State!.Reminder1SentAt);
            }
        }
        finally { foreach (var key in keys) Environment.SetEnvironmentVariable(key, original[key]); }
    }
    private static FunnelRecoverySequence Service(Store store, Sender sender, Permission? permission = null, bool enabled = true) =>
        new(store, sender, permission ?? new Permission(), Config(enabled), NullLogger<FunnelRecoverySequence>.Instance);

    private sealed class Permission : IFunnelRecoveryMarketingPermission
    {
        public RecoveryMarketingPermission Value = RecoveryMarketingPermission.Allowed;
        public string? UserId;
        public Task<RecoveryMarketingPermission> CheckAsync(string canonicalUserId) { UserId = canonicalUserId; return Task.FromResult(Value); }
    }

    private sealed class Sender : IFunnelRecoveryEmailSender
    {
        public List<FunnelRecoveryEmail> Messages = [];
        public string? Recipient;
        public Exception? Failure;
        public int Attempts;
        public bool Yield;
        public async Task<string> SendAsync(string email, FunnelRecoveryEmail message)
        {
            Interlocked.Increment(ref Attempts);
            if (Yield) await Task.Yield();
            if (Failure is not null) throw Failure;
            Recipient = email;
            lock (Messages) Messages.Add(message);
            return "message";
        }
    }

    private sealed class Store : IFunnelRecoverySequenceStore
    {
        private readonly object _gate = new();
        public FunnelRecoveryState? State = Initial;
        public FunnelRecoveryContext? Current = Context;
        public int Resolutions, Claims, Scans;
        public Action? AfterClaim;
        public bool FailMarkSent;
        public bool FailSuppression;
        public int SubscriptionCallbacks;
        public async IAsyncEnumerable<string> FindCandidatesAsync(DateTimeOffset readyBefore)
        {
            Scans++;
            await Task.CompletedTask;
            if (State is not null) yield return State.Id;
        }
        public Task<FunnelRecoveryState?> ReadAsync(string id) { lock (_gate) return Task.FromResult(State); }
        public Task<FunnelRecoveryContext?> ResolveRecoveryAsync(FunnelRecoveryState state) { Resolutions++; return Task.FromResult(Current); }
        public Task SuppressAsync(string id, string reason)
        {
            lock (_gate) State = State! with { SuppressedAt = ReadyAt, SuppressionReason = reason };
            return Task.CompletedTask;
        }
        public Task<bool> ClaimReminderAsync(FunnelRecoveryState state, FunnelRecoveryStep step, DateTimeOffset now, DateTimeOffset readyBefore)
        {
            lock (_gate)
            {
                if (FunnelRecoverySequence.Due(State!, now, Timing) != step) return Task.FromResult(false);
                State = State! with { ReminderClaim = state.ReminderClaim, ReminderAttempts = State!.ReminderAttempts + 1 };
                Claims++;
                AfterClaim?.Invoke();
                return Task.FromResult(true);
            }
        }
        public Task MarkReminderSentAsync(string id, FunnelRecoveryStep step, string claim, string messageId, DateTimeOffset now)
        {
            if (FailMarkSent) throw new IOException();
            Assert.AreEqual(claim, State!.ReminderClaim);
            State = State with
            {
                Reminder1SentAt = step == FunnelRecoveryStep.Reminder1 ? now : State.Reminder1SentAt,
                Reminder2SentAt = step == FunnelRecoveryStep.Reminder2 ? now : State.Reminder2SentAt,
                FinalReminderSentAt = step == FunnelRecoveryStep.FinalReminder ? now : State.FinalReminderSentAt,
                ReminderClaim = null, ReminderAttempts = 0, ReminderRetryAt = null
            };
            return Task.CompletedTask;
        }
        public Task RejectReminderAsync(string id, string claim, DateTimeOffset now)
        {
            Assert.AreEqual(claim, State!.ReminderClaim);
            State = State with { ReminderClaim = null, ReminderRetryAt = now.AddHours(6) };
            return Task.CompletedTask;
        }
        public Task SuppressSubscribedAsync(string userId, string subscriptionRecordId)
        {
            SubscriptionCallbacks++;
            if (FailSuppression) throw new IOException();
            return Task.CompletedTask;
        }
    }
}
