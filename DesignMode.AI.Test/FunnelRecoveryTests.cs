using DesignMode.AI.Server;
using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryTests
{
    private static readonly FunnelVideoReadyEvent Ready = new("job", DateTimeOffset.UtcNow);
    private const string Host = "https://app.example.test";

    [DataTestMethod]
    [DataRow("Production", "https://www.designmode.ai")]
    [DataRow("Development", "https://localhost:7178")]
    public async Task Completion_SamePublisherAndConsumerReachFakeSender(string environment, string host)
    {
        var configuration = new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZURE_FUNCTIONS_ENVIRONMENT"] = environment,
                ["FUNNEL_RECOVERY_EMAIL_ENABLED"] = "true", ["HostUrl"] = host }).Build());
        var store = new Store();
        var sender = new Sender();
        var consumer = new FunnelRecoveryFunction(Service(store, sender), NullLogger<FunnelRecoveryFunction>.Instance, configuration);
        var publisher = new FunnelRecoveryPublisher(NullLogger<FunnelRecoveryPublisher>.Instance,
            () => configuration.Enabled, notification => consumer.RunAsync(VideoQueuePipeline.Serialize(notification)));
        await publisher.PublishSafelyAsync("completed-job");
        Assert.AreEqual(1, sender.Calls);
        Assert.IsNotNull(store.State!.VideoReadyEmailSentAt);
        StringAssert.StartsWith(sender.Message!.ResumeUrl, host);
    }

    [TestMethod]
    public async Task Send_IncludesSignedUnsubscribeLink()
    {
        var store = new Store();
        var sender = new Sender();
        var configuration = new FunnelRecoveryConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSecretKey"] = "test-signing-secret" }).Build());
        var unsubscribe = new FunnelRecoveryUnsubscribe(configuration, store);
        await new FunnelRecoveryService(store, sender, NullLogger<FunnelRecoveryService>.Instance, unsubscribe)
            .ProcessAsync(Ready, true, Host);
        var expected = unsubscribe.CreateUrl(Host, store.State!.Id);
        Assert.AreEqual(1, sender.Calls);
        StringAssert.Contains(sender.Message!.Html, expected);
        StringAssert.Contains(sender.Message.Text, expected);
    }

    [TestMethod]
    public async Task Send_UsesSelectionRefreshedAfterClaim()
    {
        var store = new Store();
        store.Context = store.Context! with { SelectedImageUrl = "https://cdn.designmode.ai/old.png" };
        store.OnClaim = () => store.Context = store.Context! with { SelectedImageUrl = "https://cdn.designmode.ai/selected.png" };
        var sender = new Sender();
        var service = new FunnelRecoveryService(store, sender, NullLogger<FunnelRecoveryService>.Instance);
        await service.ProcessAsync(Ready, true, Host);
        StringAssert.Contains(sender.Message!.Html, "https://cdn.designmode.ai/selected.png");
        StringAssert.Contains(sender.Message.Html, "email-play-overlay");
        Assert.IsFalse(sender.Message.Html.Contains("old.png"));
    }

    [TestMethod]
    public async Task CompletedPersistedVideo_ReplayedConcurrently_SendsOnce()
    {
        var store = new Store();
        var sender = new Sender();
        var service = Service(store, sender);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.ProcessAsync(Ready, true, Host)));
        Assert.AreEqual(1, sender.Calls);
        Assert.IsNotNull(store.State!.VideoReadyEmailSentAt);
        Assert.AreEqual(Ready.ObservedAt, store.State.VideoReadyAt);
    }

    [DataTestMethod]
    [DataRow("pending")]
    [DataRow("missing-user")]
    [DataRow("subscribed")]
    [DataRow("completed")]
    [DataRow("bad-email")]
    [DataRow("no-email")]
    [DataRow("missing-link")]
    [DataRow("sent")]
    [DataRow("suppressed")]
    public async Task Ineligible_DoesNotSend(string scenario)
    {
        var store = new Store();
        store.Context = scenario switch
        {
            "pending" => store.Context! with { VideoReady = false },
            "missing-user" => null,
            "subscribed" => store.Context! with { Paid = true },
            "completed" => store.Context! with { Unfinished = false },
            "bad-email" => store.Context! with { Email = "not an email" },
            "no-email" => store.Context! with { Email = "" },
            "missing-link" => store.Context! with { FunnelId = "" },
            _ => store.Context
        };
        if (scenario is "sent" or "suppressed")
        {
            await store.RecordReadyAsync(store.Context!, Ready.ObservedAt);
            store.State = scenario == "sent"
                ? store.State! with { VideoReadyEmailSentAt = DateTimeOffset.UtcNow }
                : store.State! with { SuppressedAt = DateTimeOffset.UtcNow };
        }
        var sender = new Sender();
        await Service(store, sender).ProcessAsync(Ready, true, Host);
        Assert.AreEqual(0, sender.Calls);
        if (scenario is "pending" or "missing-user") Assert.IsNull(store.State);
    }

    [DataTestMethod]
    [DataRow("subscription")]
    [DataRow("completion")]
    [DataRow("ownership")]
    [DataRow("suppression")]
    public async Task StateChangesWhileQueued_RecheckedAfterClaim(string change)
    {
        var store = new Store();
        store.OnClaim = () =>
        {
            if (change == "suppression") store.State = store.State! with { SuppressedAt = DateTimeOffset.UtcNow };
            else store.Context = change switch
            {
                "subscription" => store.Context! with { Paid = true },
                "completion" => store.Context! with { Unfinished = false },
                _ => store.Context! with { UserId = "different-owner" }
            };
        };
        var sender = new Sender();
        await Service(store, sender).ProcessAsync(Ready, true, Host);
        Assert.AreEqual(0, sender.Calls);
        Assert.IsNotNull(store.State!.SuppressedAt);
    }

    [TestMethod]
    public async Task Disabled_NoReadsWritesOrExternalSend()
    {
        var store = new Store();
        var sender = new Sender();
        await Service(store, sender).ProcessAsync(Ready, false, null);
        Assert.AreEqual(0, store.Reads);
        Assert.IsNull(store.State);
        Assert.AreEqual(0, sender.Calls);
    }

    [TestMethod]
    public async Task DefiniteProviderRejection_UnsentAndBoundedAtThreeAttempts()
    {
        var store = new Store();
        var sender = new Sender { Failure = new FunnelRecoveryEmailRejectedException() };
        var service = Service(store, sender);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { await service.ProcessAsync(Ready, true, Host); }
            catch (FunnelRecoveryEmailRejectedException) { }
        }
        Assert.AreEqual(3, sender.Calls);
        Assert.IsNull(store.State!.VideoReadyEmailSentAt);
    }

    [TestMethod]
    public async Task DefiniteRejection_CanRetrySuccessfully()
    {
        var store = new Store();
        var sender = new Sender { Failure = new FunnelRecoveryEmailRejectedException() };
        var service = Service(store, sender);
        await Assert.ThrowsExceptionAsync<FunnelRecoveryEmailRejectedException>(() => service.ProcessAsync(Ready, true, Host));
        sender.Failure = null;
        await service.ProcessAsync(Ready, true, Host);
        Assert.IsNotNull(store.State!.VideoReadyEmailSentAt);
        Assert.AreEqual(2, sender.Calls);
    }

    [TestMethod]
    public async Task AmbiguousProviderFailure_DurableClaimPreventsResend()
    {
        var store = new Store();
        var sender = new Sender { Failure = new TimeoutException() };
        var service = Service(store, sender);
        await service.ProcessAsync(Ready, true, Host);
        await service.ProcessAsync(Ready, true, Host);
        Assert.AreEqual(1, sender.Calls);
        Assert.IsNull(store.State!.VideoReadyEmailSentAt);
        Assert.AreEqual("Sending", store.State.DeliveryStatus);
    }

    [TestMethod]
    public async Task AcceptedEmail_SentWriteFailureDoesNotResend()
    {
        var store = new Store { FailSentWrite = true };
        var sender = new Sender();
        var service = Service(store, sender);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ProcessAsync(Ready, true, Host));
        await service.ProcessAsync(Ready, true, Host);
        Assert.AreEqual(1, sender.Calls);
        Assert.IsNull(store.State!.VideoReadyEmailSentAt);
    }

    [TestMethod]
    public async Task PublisherFailure_DoesNotFailVideoCompletion()
    {
        var publisher = new FunnelRecoveryPublisher(NullLogger<FunnelRecoveryPublisher>.Instance,
            () => true, _ => throw new InvalidOperationException("private failure details"));
        await publisher.PublishSafelyAsync("completed-job");
    }

    [TestMethod]
    public async Task DisabledPublisher_DoesNotEnqueueHistoricalWork()
    {
        var calls = 0;
        var publisher = new FunnelRecoveryPublisher(NullLogger<FunnelRecoveryPublisher>.Instance,
            () => false, _ => { calls++; return Task.CompletedTask; });
        await publisher.PublishSafelyAsync("historical-job");
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void Email_UsesCanonicalLoginReturnUrlAndConciseServiceCopy()
    {
        const string page = "/init/mainconcept?id=owned&view=preview";
        var email = FunnelRecoveryEmail.Create(Host, page);
        Assert.AreEqual($"{Host}/login?returnUrl={Uri.EscapeDataString(page)}", email.ResumeUrl);
        Assert.AreEqual("Your DesignMode preview is ready", email.Subject);
        StringAssert.Contains(email.Html, "View your preview");
        StringAssert.Contains(email.Html, "DesignMode");
        StringAssert.Contains(email.Text, "Your design preview is ready.");
        StringAssert.Contains(email.Text, email.ResumeUrl);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("https://evil.example/path")]
    [DataRow("//evil.example/path")]
    [DataRow("/%2f%2fevil.example")]
    [DataRow("/login")]
    [DataRow("/init/../login")]
    [DataRow("/init\\evil")]
    public void InvalidCurrentPage_FallsBackToDesignerIntro(string? page) =>
        Assert.AreEqual($"{Host}/login?returnUrl=%2Fdesignerintro", FunnelRecoveryEmail.Create(Host, page).ResumeUrl);

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("http://app.example.test")]
    [DataRow("https://localhost")]
    [DataRow("https://127.0.0.1")]
    [DataRow("https://app.example.test:7032")]
    [DataRow("https://user@app.example.test")]
    [DataRow("https://app.example.test/api")]
    public void InvalidHost_RejectsEmailLink(string? host) =>
        Assert.ThrowsException<InvalidOperationException>(() => FunnelRecoveryEmail.Create(host, null));

    [DataTestMethod]
    [DataRow("/designerintro", true)]
    [DataRow("/init/mainconcept?id=owned", true)]
    [DataRow("/init/funnelend", true)]
    [DataRow("/init/upgrade", true)]
    [DataRow("/init/success", false)]
    [DataRow("/init/geninit", false)]
    [DataRow("/init/gen?cid=owned", false)]
    [DataRow("/init/dashboard", false)]
    [DataRow("/init/website-success", false)]
    public void ExistingProgress_ExcludesCompletedDestinations(string page, bool unfinished) =>
        Assert.AreEqual(unfinished, FunnelRecoveryService.IsUnfinished(page));

    [TestMethod]
    public void RecoveryState_ContainsNoEmailContentOrBrowserIdentity()
    {
        var stateFields = typeof(FunnelRecoveryState).GetProperties().Select(p => p.Name).ToArray();
        CollectionAssert.DoesNotContain(stateFields, "Email");
        CollectionAssert.DoesNotContain(stateFields, "CurrentPage");
        CollectionAssert.DoesNotContain(stateFields, "Html");
        CollectionAssert.AreEquivalent(new[] { "JobId", "ObservedAt" },
            typeof(FunnelVideoReadyEvent).GetProperties().Select(p => p.Name).ToArray());
    }

    private static FunnelRecoveryService Service(Store store, Sender sender) =>
        new(store, sender, NullLogger<FunnelRecoveryService>.Instance);

    [TestMethod]
    public async Task LocalDevelopment_EligibleVideoSendsWithLocalResumeLink()
    {
        var store = new Store();
        var sender = new Sender();
        await Service(store, sender).ProcessAsync(Ready, true, "https://localhost:7178", true);
        Assert.AreEqual(1, sender.Calls);
        Assert.IsNotNull(store.State!.VideoReadyEmailSentAt);
        StringAssert.StartsWith(sender.Message!.ResumeUrl, "https://localhost:7178/login?returnUrl=");
    }

    private sealed class Sender : IFunnelRecoveryEmailSender
    {
        public int Calls;
        public Exception? Failure;
        public FunnelRecoveryEmail? Message;
        public async Task<string> SendAsync(string email, FunnelRecoveryEmail message)
        {
            Interlocked.Increment(ref Calls);
            Message = message;
            await Task.Yield();
            if (Failure is not null) throw Failure;
            Assert.AreEqual("canonical@example.test", email);
            return "ses-message";
        }
    }

    private sealed class Store : IFunnelRecoveryStore
    {
        private readonly object _gate = new();
        public FunnelRecoveryContext? Context = new("user", "funnel", "project", "canonical@example.test",
            "/init/mainconcept?id=owned", true, false, true);
        public FunnelRecoveryState? State;
        public Action? OnClaim;
        public int Reads;
        public bool FailSentWrite;
        public Task<FunnelRecoveryContext?> ResolveAsync(string jobId) { Reads++; return Task.FromResult(Context); }
        public Task<FunnelRecoveryState> RecordReadyAsync(FunnelRecoveryContext context, DateTimeOffset readyAt)
        {
            lock (_gate)
            {
                State ??= new(FunnelRecoveryService.RecoveryId(context), context.UserId, context.FunnelId, context.ProjectId, readyAt);
                return Task.FromResult(State);
            }
        }
        public Task<FunnelRecoveryState?> ReadAsync(string id) => Task.FromResult(State);
        public Task<bool> ClaimAsync(string id)
        {
            lock (_gate)
            {
                if (State!.DeliveryStatus != "Ready" || State.Attempts >= 3 ||
                    State.VideoReadyEmailSentAt.HasValue || State.SuppressedAt.HasValue) return Task.FromResult(false);
                State = State with { DeliveryStatus = "Sending", Attempts = State.Attempts + 1 };
                OnClaim?.Invoke();
                return Task.FromResult(true);
            }
        }
        public Task SuppressAsync(string id) { State = State! with { SuppressedAt = DateTimeOffset.UtcNow }; return Task.CompletedTask; }
        public Task RejectAsync(string id) { State = State! with { DeliveryStatus = "Ready" }; return Task.CompletedTask; }
        public Task MarkSentAsync(string id, string messageId)
        {
            if (FailSentWrite) throw new InvalidOperationException();
            State = State! with { VideoReadyEmailSentAt = DateTimeOffset.UtcNow, DeliveryStatus = "Sent" };
            return Task.CompletedTask;
        }
    }
}
