using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Navigation;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryStoreTests
{
    [TestMethod]
    public async Task CanonicalRecovery_LoadsFunnelEndSelectionWithoutCompanyOrPromptLink()
    {
        var client = new DynamoStub();
        client.Project["LegacyInviteId"] = new() { S = "owned-invite" };
        var store = CreateStore(client, new RoutingStub());
        var ready = await store.ResolveAsync("job");
        Assert.AreEqual("https://cdn.designmode.ai/funnel-selection.png", ready!.SelectedImageUrl);
        var state = new FunnelRecoveryState("record", "canonical-user", "funnel", "project", DateTimeOffset.UtcNow);
        var reminder = await store.ResolveRecoveryAsync(state);
        Assert.AreEqual(ready.SelectedImageUrl, reminder!.SelectedImageUrl);
        var email = FunnelRecoveryEmail.Create("https://designmode.ai", reminder.CurrentPage,
            selectedImageUrl: reminder.SelectedImageUrl);
        StringAssert.Contains(email.Html, "src=\"https://cdn.designmode.ai/funnel-selection.png\"");
        Assert.IsTrue(email.Html.IndexOf("funnel-selection.png", StringComparison.Ordinal) >
            email.Html.IndexOf("Your design preview is ready.", StringComparison.Ordinal));
        client.Project["UserId"].S = "another-user";
        Assert.IsNull(await store.ResolveRecoveryAsync(state));
    }

    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task LocalDynamoDb_SubscriptionCompositeKey_SuppressesPendingRecovery()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();
        using var client = DynamoDbClientFactory.CreateClient();
        // Use the application's local schema; never change existing tables.
        var schema = (await client.DescribeTableAsync("Subscriptions")).Table.KeySchema;
        Assert.IsTrue(schema.Any(key => key.AttributeName == "Id" && key.KeyType == KeyType.HASH));
        Assert.IsTrue(schema.Any(key => key.AttributeName == "UserId" && key.KeyType == KeyType.RANGE));
        var userId = Guid.NewGuid().ToString();
        var subscriptionId = Guid.NewGuid().ToString();
        var email = $"recovery-test-{userId}@example.invalid";
        var context = new FunnelRecoveryContext(userId, "test-funnel", "test-project", email, "/init/mainconcept", true, false, true);
        var id = FunnelRecoveryService.RecoveryId(context);
        var store = new FunnelRecoveryStore(() => DynamoDbClientFactory.CreateClient(),
            value => Task.FromResult<CanonicalUserStore.CanonicalUser?>(value == email ? new(userId, email) : null), new RoutingStub());
        Dictionary<string, AttributeValue> userKey = new() { ["Id"] = new() { S = userId } };
        Dictionary<string, AttributeValue> subscriptionKey = new()
        { ["Id"] = new() { S = subscriptionId }, ["UserId"] = new() { S = userId } };
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = "Users", ConditionExpression = "attribute_not_exists(Id)",
                Item = new(userKey) { ["Username"] = new() { S = email } }
            });
            await store.RecordReadyAsync(context, DateTimeOffset.UtcNow);
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = "Subscriptions", ConditionExpression = "attribute_not_exists(Id)",
                Item = new(subscriptionKey) { ["SubscriptionStatus"] = new() { S = "active" } }
            });
            await store.SuppressSubscribedAsync(userId, subscriptionId);
            var state = (await store.ReadAsync(id))!;
            Assert.IsNotNull(state.SuppressedAt);
            Assert.AreEqual("SUBSCRIBED", state.SuppressionReason);
            Assert.IsFalse(await store.ClaimAsync(id));
            await store.SuppressSubscribedAsync(userId, subscriptionId);
            Assert.AreEqual(state.SuppressedAt, (await store.ReadAsync(id))!.SuppressedAt);
        }
        finally
        {
            await client.DeleteItemAsync(FunnelRecoveryStore.TableName, new() { ["Id"] = new() { S = id } });
            await client.DeleteItemAsync("Subscriptions", subscriptionKey);
            await client.DeleteItemAsync("Users", userKey);
        }
    }

    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task LocalDynamoDb_ReminderClaimsRetriesAndFinalizationAreDurable()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();
        using var client = DynamoDbClientFactory.CreateClient();
        try { await client.DescribeTableAsync(FunnelRecoveryStore.TableName); }
        catch (ResourceNotFoundException)
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = FunnelRecoveryStore.TableName, BillingMode = BillingMode.PAY_PER_REQUEST,
                KeySchema = [new("Id", KeyType.HASH)], AttributeDefinitions = [new("Id", ScalarAttributeType.S)]
            });
        }
        var store = new FunnelRecoveryStore(() => DynamoDbClientFactory.CreateClient(),
            _ => throw new AssertFailedException("Persistence test must not read accounts."), new RoutingStub());
        var context = new FunnelRecoveryContext(Guid.NewGuid().ToString(), "test-funnel", "test-project", "unused@example.invalid", null, true, false, true);
        var id = FunnelRecoveryService.RecoveryId(context);
        var readyAt = DateTimeOffset.UtcNow.AddDays(-10);
        var now = DateTimeOffset.UtcNow;
        try
        {
            await store.RecordReadyAsync(context, readyAt);
            Assert.IsTrue(await store.ClaimAsync(id));
            await store.MarkSentAsync(id, "first-message");
            foreach (var step in Enum.GetValues<FunnelRecoveryStep>())
            {
                var state = (await store.ReadAsync(id))!;
                var claim = Guid.NewGuid().ToString("N");
                var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                    store.ClaimReminderAsync(state with { ReminderClaim = claim }, step, now, readyAt.AddDays(1))));
                Assert.AreEqual(1, claims.Count(won => won));
                // Wrong owners cannot release a worker's claim.
                await Assert.ThrowsExceptionAsync<ConditionalCheckFailedException>(() => store.RejectReminderAsync(id, "other-worker", now));
                await store.RejectReminderAsync(id, claim, now);
                Assert.IsFalse(await store.ClaimReminderAsync(state with { ReminderClaim = claim }, step, now, readyAt.AddDays(1)));
                now += FunnelRecoveryTiming.RetryDelay;
                Assert.IsTrue(await store.ClaimReminderAsync(state with { ReminderClaim = claim }, step, now, readyAt.AddDays(1)));
                await store.MarkReminderSentAsync(id, step, claim, "reminder-message", now);
                Assert.IsFalse(await store.ClaimReminderAsync(state with { ReminderClaim = claim }, step, now, readyAt.AddDays(1)));
                Assert.AreEqual(0, (await store.ReadAsync(id))!.ReminderAttempts);
                now += FunnelRecoveryTiming.MinimumSpacing;
            }
            var complete = (await store.ReadAsync(id))!;
            Assert.IsNotNull(complete.Reminder1SentAt);
            Assert.IsNotNull(complete.Reminder2SentAt);
            Assert.IsNotNull(complete.FinalReminderSentAt);
            await store.SuppressAsync(id, "SUBSCRIBED");
            await store.SuppressAsync(id, "FUNNEL_COMPLETED");
            Assert.AreEqual("SUBSCRIBED", (await store.ReadAsync(id))!.SuppressionReason);
        }
        finally { await client.DeleteItemAsync(FunnelRecoveryStore.TableName, new() { ["Id"] = new() { S = id } }); }
    }

    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task LocalDynamoDb_ConditionalClaimsAndSuppressionAreDurable()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();
        using var client = DynamoDbClientFactory.CreateClient();
        try { await client.DescribeTableAsync(FunnelRecoveryStore.TableName); }
        catch (ResourceNotFoundException)
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = FunnelRecoveryStore.TableName, BillingMode = BillingMode.PAY_PER_REQUEST,
                KeySchema = [new("Id", KeyType.HASH)],
                AttributeDefinitions = [new("Id", ScalarAttributeType.S)]
            });
        }
        var store = new FunnelRecoveryStore(() => DynamoDbClientFactory.CreateClient(),
            _ => throw new AssertFailedException("No account lookup is needed for the persistence test."), new RoutingStub());
        var context = new FunnelRecoveryContext(Guid.NewGuid().ToString(), "test-funnel", "test-project",
            "unused@example.invalid", null, true, false, true);
        var id = FunnelRecoveryService.RecoveryId(context);
        try
        {
            var readyAt = DateTimeOffset.UtcNow;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.RecordReadyAsync(context, readyAt)));
            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ClaimAsync(id)));
            Assert.AreEqual(1, claims.Count(claimed => claimed));
            await store.RejectAsync(id);
            Assert.IsTrue(await store.ClaimAsync(id));
            await store.MarkSentAsync(id, "test-message");
            Assert.IsFalse(await store.ClaimAsync(id));
            var replay = await store.RecordReadyAsync(context, readyAt.AddDays(1));
            Assert.AreEqual(readyAt, replay.VideoReadyAt);
            Assert.IsNotNull(replay.VideoReadyEmailSentAt);
            Assert.AreEqual(2, replay.Attempts);
            await store.SuppressAsync(id);
            Assert.IsNotNull((await store.ReadAsync(id))!.SuppressedAt);
            var row = (await client.GetItemAsync(new GetItemRequest
            { TableName = FunnelRecoveryStore.TableName, Key = new() { ["Id"] = new() { S = id } }, ConsistentRead = true })).Item;
            Assert.IsFalse(row.ContainsKey("Email"));
            Assert.IsFalse(row.ContainsKey("Html"));
            Assert.AreEqual("5", row["Revision"].N);
        }
        finally
        {
            await client.DeleteItemAsync(FunnelRecoveryStore.TableName, new() { ["Id"] = new() { S = id } });
        }
    }

    [TestMethod]
    public async Task PersistedOwnership_ResolvesCanonicalAccountAndSubscription()
    {
        var client = new DynamoStub();
        var routing = new RoutingStub();
        var result = await CreateStore(client, routing).ResolveAsync("job");
        Assert.IsNotNull(result);
        Assert.AreEqual("canonical-user", result.UserId);
        Assert.AreEqual("canonical@example.test", result.Email);
        Assert.AreEqual("canonical-user", routing.SubscriptionUser);
        Assert.IsTrue(result.VideoReady);
        Assert.IsTrue(result.Unfinished);
        Assert.IsTrue(client.Reads.All(r => r.ConsistentRead == true));
    }

    [DataTestMethod]
    [DataRow("job-user")]
    [DataRow("funnel-user")]
    [DataRow("project-funnel")]
    [DataRow("funnel-project")]
    [DataRow("additional-project")]
    [DataRow("missing-video")]
    [DataRow("pending")]
    [DataRow("canonical-user-mismatch")]
    [DataRow("historical-job")]
    [DataRow("website-preview")]
    public async Task UntrustedOrIncompleteLinkage_FailsClosed(string scenario)
    {
        var client = new DynamoStub();
        switch (scenario)
        {
            case "job-user": client.Job["UserId"].S = "browser-selected-user"; break;
            case "funnel-user": client.Funnel["UserId"].S = "someone-else"; break;
            case "project-funnel": client.Project["OriginatingFunnelId"].S = "other-funnel"; break;
            case "funnel-project": client.Funnel["ProjectId"].S = "other-project"; break;
            case "additional-project": client.Project["IsOriginalAcquisitionProject"].BOOL = false; break;
            case "missing-video": client.Job.Remove("VideoUrl"); break;
            case "pending": client.Job["Status"].S = "Processing"; break;
            case "historical-job": client.Job.Remove("PreviewKind"); break;
            case "website-preview": client.Job["PreviewKind"].S = "Website"; break;
        }
        var store = CreateStore(client, new RoutingStub(), scenario == "canonical-user-mismatch");
        Assert.IsNull(await store.ResolveAsync("job"));
    }

    [TestMethod]
    public async Task BrowserEmailInJob_IsIgnored()
    {
        var client = new DynamoStub();
        client.Job["Email"] = new() { S = "attacker@example.test" };
        client.Job["CurrentPage"] = new() { S = "https://evil.example" };
        var result = await CreateStore(client, new RoutingStub()).ResolveAsync("job");
        Assert.AreEqual("canonical@example.test", result!.Email);
        Assert.AreEqual("/init/mainconcept", result.CurrentPage);
    }

    [TestMethod]
    public async Task AdvancedFunnelState_IsNotEligible()
    {
        var client = new DynamoStub();
        client.Funnel["Status"].S = "Completed";
        Assert.IsFalse((await CreateStore(client, new RoutingStub()).ResolveAsync("job"))!.Unfinished);
    }

    [TestMethod]
    public async Task PaidSubscription_IsReadFromCanonicalRoutingStore()
    {
        var result = await CreateStore(new DynamoStub(), new RoutingStub { Paid = true }).ResolveAsync("job");
        Assert.IsTrue(result!.Paid);
        Assert.IsFalse(FunnelRecoveryService.Eligible(result));
    }

    [TestMethod]
    public async Task ScheduledRecovery_ResolvesCanonicalOwnershipWithoutJobLookup()
    {
        var client = new DynamoStub();
        var routing = new RoutingStub();
        var state = new FunnelRecoveryState("record", "canonical-user", "funnel", "project", DateTimeOffset.UtcNow);
        var context = await CreateStore(client, routing).ResolveRecoveryAsync(state);
        Assert.AreEqual("canonical-user", context!.UserId);
        Assert.AreEqual("canonical-user", routing.SubscriptionUser);
        Assert.IsFalse(client.Reads.Any(request => request.TableName == "DesignModeJobs"));
        Assert.IsTrue(client.Reads.All(request => request.ConsistentRead == true));
        Assert.IsNull(await CreateStore(client, routing).ResolveRecoveryAsync(state with { UserId = "untrusted" }));
    }

    [TestMethod]
    public async Task SubscriptionHook_RechecksCanonicalAccessAndSuppressesAllPages()
    {
        var client = new DynamoStub();
        var routing = new RoutingStub { Paid = true };
        await CreateStore(client, routing).SuppressSubscribedAsync("canonical-user", "subscription");
        Assert.IsTrue(client.Reads.Any(request => request.TableName == "Subscriptions" && request.ConsistentRead == true &&
            request.Key["Id"].S == "subscription" && request.Key["UserId"].S == "canonical-user" && request.Key.Count == 2));
        Assert.AreEqual(2, client.Scans.Count);
        Assert.AreEqual(2, client.Updates.Count);
        Assert.IsTrue(client.Updates.All(request => request.ExpressionAttributeValues[":reason"].S == "SUBSCRIBED"));
        Assert.IsTrue(client.Scans.All(request => request.ExpressionAttributeValues[":user"].S == "canonical-user"));
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task SubscriptionHook_UnpaidOrNoncanonicalAccountDoesNotSuppress(bool paid, bool mismatch)
    {
        var client = new DynamoStub();
        client.Subscription["SubscriptionStatus"].S = paid ? "active" : "canceled";
        await CreateStore(client, new RoutingStub { Paid = paid }, mismatch).SuppressSubscribedAsync("canonical-user", "subscription");
        Assert.AreEqual(0, client.Scans.Count);
        Assert.AreEqual(0, client.Updates.Count);
    }

    [TestMethod]
    public async Task SubscriptionHook_RejectsAnotherUsersSubscription()
    {
        var client = new DynamoStub();
        client.Subscription["UserId"].S = "other-user";
        await CreateStore(client, new RoutingStub()).SuppressSubscribedAsync("canonical-user", "subscription");
        Assert.AreEqual(0, client.Updates.Count);
    }

    [TestMethod]
    public async Task DueScan_PaginatesOnlyExistingRecoveryRecords()
    {
        var client = new DynamoStub();
        var results = new List<string>();
        await foreach (var id in CreateStore(client, new RoutingStub()).FindCandidatesAsync(DateTimeOffset.UtcNow)) results.Add(id);
        CollectionAssert.AreEqual(new[] { "first", "second" }, results);
        Assert.IsTrue(client.Scans.All(request => request.TableName == "FunnelRecovery" && request.Limit == 100));
        StringAssert.Contains(client.Scans[0].FilterExpression, "attribute_exists(VideoReadyEmailSentAt)");
        StringAssert.Contains(client.Scans[0].FilterExpression, "attribute_not_exists(SuppressedAt)");
    }

    [DataTestMethod]
    [DataRow(FunnelRecoveryStep.Reminder1)]
    [DataRow(FunnelRecoveryStep.Reminder2)]
    [DataRow(FunnelRecoveryStep.FinalReminder)]
    public async Task ReminderClaim_UsesDurableConditionsAndUniqueClaim(FunnelRecoveryStep step)
    {
        var client = new DynamoStub { RejectClaim = true };
        var state = new FunnelRecoveryState("record", "user", "funnel", "project", DateTimeOffset.UtcNow, ReminderClaim: "unique-claim");
        Assert.IsFalse(await CreateStore(client, new RoutingStub()).ClaimReminderAsync(state, step, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1)));
        var request = client.Update!;
        StringAssert.Contains(request.ConditionExpression, $"attribute_not_exists({step}SentAt)");
        StringAssert.Contains(request.ConditionExpression, "attribute_not_exists(ReminderClaim)");
        StringAssert.Contains(request.ConditionExpression, "attribute_not_exists(SuppressedAt)");
        StringAssert.Contains(request.ConditionExpression, "ReminderAttempts < :max");
        Assert.AreEqual("3", request.ExpressionAttributeValues[":max"].N);
        Assert.AreEqual("unique-claim", request.ExpressionAttributeValues[":claim"].S);
    }

    [TestMethod]
    public async Task ReminderFinalizationAndRetry_AreBoundToClaim()
    {
        var client = new DynamoStub();
        var store = CreateStore(client, new RoutingStub());
        var now = DateTimeOffset.UtcNow;
        await store.MarkReminderSentAsync("record", FunnelRecoveryStep.Reminder1, "owner", "message", now);
        StringAssert.Contains(client.Update!.ConditionExpression, "ReminderClaim = :claim");
        StringAssert.Contains(client.Update.UpdateExpression, "REMOVE ReminderClaim, ReminderAttempts, ReminderRetryAt");
        await store.RejectReminderAsync("record", "owner", now);
        Assert.AreEqual("ReminderClaim = :claim", client.Update.ConditionExpression);
        Assert.AreEqual(now.AddHours(6), DateTimeOffset.Parse(client.Update.ExpressionAttributeValues[":retry"].S));
        Assert.IsFalse(client.Update.UpdateExpression.Contains("SentAt"));
    }

    [TestMethod]
    public async Task LostConditionalClaim_ReturnsFalse()
    {
        var client = new DynamoStub { RejectClaim = true };
        Assert.IsFalse(await CreateStore(client, new RoutingStub()).ClaimAsync("record"));
        Assert.AreEqual("FunnelRecovery", client.Update!.TableName);
        StringAssert.Contains(client.Update.ConditionExpression, "attribute_not_exists(VideoReadyEmailSentAt)");
        StringAssert.Contains(client.Update.ConditionExpression, "attribute_not_exists(SuppressedAt)");
        StringAssert.Contains(client.Update.ConditionExpression, "DeliveryStatus = :ready");
        Assert.AreEqual("3", client.Update.ExpressionAttributeValues[":max"].N);
    }

    private static FunnelRecoveryStore CreateStore(DynamoStub client, RoutingStub routing, bool mismatch = false) =>
        new(() => client, email => Task.FromResult<CanonicalUserStore.CanonicalUser?>(
            new(mismatch ? "other-user" : "canonical-user", email, CurrentPage: "/init/mainconcept")), routing);

    private sealed class RoutingStub : IPostAuthRoutingStore
    {
        public bool Paid;
        public string? SubscriptionUser;
        public Task<PostAuthUser?> FindUserAsync(string username) => Task.FromResult<PostAuthUser?>(new("canonical-user", "/init/mainconcept"));
        public Task<bool> HasPaidSubscriptionAsync(string userId) { SubscriptionUser = userId; return Task.FromResult(Paid); }
        public Task SaveCurrentPageAsync(string username, string currentPage) => throw new AssertFailedException("Recovery must not write progress.");
    }

    private sealed class DynamoStub() : AmazonDynamoDBClient(new AnonymousAWSCredentials(),
        new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:8000" })
    {
        public Dictionary<string, AttributeValue> Job = Row(("PreviewKind", "Social"), ("Status", "Completed"), ("VideoUrl", "https://video.example.test/ready.mp4"),
            ("UserId", "canonical-user"), ("ProjectId", "project"), ("FunnelId", "funnel"));
        public Dictionary<string, AttributeValue> Project = new(Row(("UserId", "canonical-user"), ("OriginatingFunnelId", "funnel")))
        { ["IsOriginalAcquisitionProject"] = new() { BOOL = true } };
        public Dictionary<string, AttributeValue> Funnel = Row(("UserId", "canonical-user"), ("ProjectId", "project"), ("Status", "Authenticated"));
        public Dictionary<string, AttributeValue> Subscription = Row(("UserId", "canonical-user"), ("SubscriptionStatus", "active"));
        public List<GetItemRequest> Reads = [];
        public UpdateItemRequest? Update;
        public List<UpdateItemRequest> Updates = [];
        public List<ScanRequest> Scans = [];
        public bool RejectClaim;
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.IsTrue(request.ConsistentRead == true);
            Assert.AreEqual("owned-invite", request.Parameters[0].S);
            if (request.Statement.Contains("FROM InviteClientImages"))
            {
                Assert.AreEqual("selected-second", request.Parameters[1].S);
                return Task.FromResult(new ExecuteStatementResponse { Items =
                    [Row(("InviteId", "owned-invite"), ("ImageId", "selected-second"),
                        ("ImageUrl", "https://cdn.designmode.ai/funnel-selection.png"))] });
            }
            Assert.IsTrue(request.Statement.Contains("FROM InviteClient WHERE"));
            return Task.FromResult(new ExecuteStatementResponse { Items =
                [Row(("Id", "owned-invite"), ("ImageId", "selected-second"))] });
        }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Reads.Add(request);
            if (request.TableName == "Subscriptions" &&
                (request.Key.Count != 2 || !request.Key.ContainsKey("Id") || !request.Key.ContainsKey("UserId")))
                throw new AmazonDynamoDBException("Subscription reads require the composite key.");
            return Task.FromResult(new GetItemResponse
            {
                Item = request.TableName switch
                {
                    "Projects" => Project, "Funnels" => Funnel, "Subscriptions" => Subscription,
                    "Users" => Row(("Username", "canonical@example.test")), _ => Job
                }
            });
        }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
        {
            Update = request;
            Updates.Add(request);
            if (RejectClaim) throw new ConditionalCheckFailedException("Already claimed");
            return Task.FromResult(new UpdateItemResponse());
        }
        public override Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default)
        {
            Scans.Add(request);
            return Task.FromResult(new ScanResponse
            {
                Items = [Row(("Id", Scans.Count == 1 ? "first" : "second"))],
                LastEvaluatedKey = Scans.Count == 1 ? Row(("Id", "first")) : new()
            });
        }
        private static Dictionary<string, AttributeValue> Row(params (string Key, string Value)[] fields) =>
            fields.ToDictionary(f => f.Key, f => new AttributeValue { S = f.Value });
    }
}
