using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AICreditPurchaseTests
{
    private static readonly CreditPackage Pack = new("credits-starter", 20, 2000,
        "https://buy.stripe.com/test_eVqdR98eFeXbgS1fo25Rm08", "plink_credit", "price_credit");

    [TestMethod]
    public async Task PaidRecorder_FailsClosedWhenLedgerIsEnabled()
    {
        var store = new AIGenerationUsageStore(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<AIGenerationUsageStore>.Instance,
            new AICreditLedger(() => new CreditDatabase()));
        var recorder = new AIUsageRecorder(store, new AIProviderPricingService(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AIUsageRecorder>.Instance);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => recorder.StartAsync(new AIGenerationUsage
        { UsageScope = "Paid", GenerationUnits = 1, GenerationJobId = "job" }));
    }

    [TestMethod]
    public async Task TestAndLiveWallets_AreIsolated_AndRepeatedCheckoutCanFundAgain()
    {
        var db = new CreditDatabase();
        var test = new AICreditLedger(() => db) { TableName = "AICreditLedgerTest" };
        var live = new AICreditLedger(() => db);
        var attempt = await test.CreateAttemptAsync("user", Pack, true);
        await test.GrantAsync(attempt, Pack, "pi_first", "cs_first", "evt_first", DateTimeOffset.UtcNow);
        await test.GrantAsync(attempt, Pack, "pi_second", "cs_second", "evt_second", DateTimeOffset.UtcNow);
        Assert.AreEqual(40m, (await test.GetWalletAsync("user")).Purchased);
        Assert.AreEqual(0m, (await live.GetWalletAsync("user")).Purchased);
        Assert.IsNull(await live.GetAttemptAsync(attempt.Id));
    }

    [TestMethod]
    public async Task Continuation_RequiresTheSameUsersFundedJob_AndRejectsReleasedHolds()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        await Fund(ledger);
        await Assert.ThrowsExceptionAsync<AICreditQuotaException>(() => ledger.RequireFundedJobAsync("user", "job", "test"));
        await ledger.ReserveAsync(Usage("job", 3), AIUsageSummaryService.Calculate("none", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0));
        await ledger.RequireFundedJobAsync("user", "job", "test");
        await Assert.ThrowsExceptionAsync<AICreditQuotaException>(() => ledger.RequireFundedJobAsync("other-user", "job", "test"));
        await ledger.CompleteAsync("job", false);
        await Assert.ThrowsExceptionAsync<AICreditQuotaException>(() => ledger.RequireFundedJobAsync("user", "job", "test"));
    }

    [TestMethod]
    public async Task Grant_RetriesAcrossInstances_AtomicallyGrantsOnceAndExcludesAcquisition()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        var attempt = await ledger.CreateAttemptAsync("user", Pack, true);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => new AICreditLedger(() => db)
            .GrantAsync(attempt, Pack, "pi_one", "cs_one", "evt_" + i, DateTimeOffset.UtcNow)));
        Assert.AreEqual(20m, (await ledger.GetWalletAsync("user")).Purchased);
        var purchase = await ledger.GetPurchaseAsync(PurchaseMeasurementStore.TransactionId("pi_one"));
        Assert.AreEqual("AICredits", purchase!.PurchaseType);
        Assert.AreEqual("user", purchase.UserId);
        Assert.IsFalse(purchase.IsFirstPayment || purchase.IsRenewal);
        Assert.AreEqual("", purchase.ProjectId);
        Assert.AreEqual("NotEligible", db.Rows[("Purchases", purchase.TransactionId)]["AdsStatus"].S);
        Assert.AreEqual(1, db.Rows.Keys.Count(x => x.Table == "Purchases"));
    }

    [TestMethod]
    public async Task FailedTransaction_DoesNotGrantOrRecordPurchase_AndCanRetry()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        var attempt = await ledger.CreateAttemptAsync("user", Pack, true);
        db.FailNextTransaction = true;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => ledger.GrantAsync(attempt, Pack, "pi_one", "cs_one", "evt", DateTimeOffset.UtcNow));
        Assert.AreEqual(0m, (await ledger.GetWalletAsync("user")).Purchased);
        Assert.IsNull(await ledger.GetPurchaseAsync(PurchaseMeasurementStore.TransactionId("pi_one")));
        await ledger.GrantAsync(attempt, Pack, "pi_one", "cs_one", "evt", DateTimeOffset.UtcNow);
        Assert.AreEqual(20m, (await ledger.GetWalletAsync("user")).Purchased);
    }

    [TestMethod]
    public async Task ConcurrentRequests_OneCreditRemaining_OnlyOneReserves()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        await Fund(ledger);
        var summary = AIUsageSummaryService.Calculate("plus", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(1), 35, 35);
        await ledger.ReserveAsync(Usage("initial", 19), summary);
        async Task<bool> Reserve(string id)
        {
            try { await new AICreditLedger(() => db).ReserveAsync(Usage(id, 1), summary); return true; }
            catch (AICreditQuotaException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Reserve("project-a")), Task.Run(() => Reserve("project-b")));
        Assert.AreEqual(1, results.Count(x => x));
        Assert.AreEqual(0m, (await ledger.GetWalletAsync("user")).Purchased);
    }

    [TestMethod]
    public async Task Consumption_BaseFirst_ThenPurchased_PreservesPurchasedAcrossRenewal()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        await Fund(ledger);
        var start = DateTimeOffset.UtcNow;
        var summary = AIUsageSummaryService.Calculate("plus", start, start.AddMonths(1), 35, 30);
        await ledger.ReserveAsync(Usage("first", 8), summary);
        Assert.AreEqual(17m, (await ledger.GetWalletAsync("user")).Purchased);
        Assert.AreEqual(35m, (await ledger.GetWalletAsync("user")).BaseUsed);
        var renewed = AIUsageSummaryService.Calculate("plus", start.AddMonths(1), start.AddMonths(2), 35, 0);
        await ledger.ReserveAsync(Usage("renewed", 3), renewed);
        Assert.AreEqual(17m, (await ledger.GetWalletAsync("user")).Purchased);
        await ledger.CompleteAsync("first", false);
        Assert.AreEqual(20m, (await ledger.GetWalletAsync("user")).Purchased);
        Assert.AreEqual(3m, (await ledger.GetWalletAsync("user")).BaseUsed);
        await ledger.CompleteAsync("first", false);
        Assert.AreEqual(20m, (await ledger.GetWalletAsync("user")).Purchased);
    }

    [TestMethod]
    public async Task ReusedJobCannotStartAgain_AndSuccessCannotBeReleased()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        await Fund(ledger);
        var summary = AIUsageSummaryService.Calculate("none", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0);
        await ledger.ReserveAsync(Usage("same", 3), summary);
        await ledger.CompleteAsync("same", true);
        await ledger.CompleteAsync("same", false);
        await Assert.ThrowsExceptionAsync<AICreditDuplicateGenerationException>(() => ledger.ReserveAsync(Usage("same", 3), summary));
        Assert.AreEqual(17m, (await ledger.GetWalletAsync("user")).Purchased);
    }

    [TestMethod]
    public async Task RefundAndDispute_RetriesDoNotDoubleRevoke_KeepDebtForSpentCredits()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        await Fund(ledger);
        await ledger.ReserveAsync(Usage("spent", 15), AIUsageSummaryService.Calculate("none", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0));
        await ledger.RevokeAsync("pi_one", 1000, false);
        await ledger.RevokeAsync("pi_one", 1000, false);
        Assert.AreEqual(-5m, (await ledger.GetWalletAsync("user")).Purchased);
        await ledger.RevokeAsync("pi_one", 1000, true);
        await ledger.RevokeAsync("pi_one", 1000, true);
        Assert.AreEqual(-15m, (await ledger.GetWalletAsync("user")).Purchased);
        await Assert.ThrowsExceptionAsync<AICreditQuotaException>(() => ledger.ReserveAsync(Usage("blocked", 1),
            AIUsageSummaryService.Calculate("plus", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(1), 35, 0)));
    }

    [DataTestMethod]
    [DataRow("Test", "sk_test_example", "Development", true)]
    [DataRow("Test", "sk_live_example", "Development", false)]
    [DataRow("Test", "sk_test_example", "Production", false)]
    [DataRow("Live", "sk_live_example", "Production", false)]
    [DataRow("", "sk_test_example", "Development", false)]
    public void Configuration_FailsClosed_WithoutCorrectModeAndLinks(string mode, string key, string environment, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_CREDITS_LEDGER_ENABLED"] = "true", ["AI_CREDITS_PURCHASES_ENABLED"] = "true", ["AI_CREDITS_MODE"] = mode,
            ["StripeSecretKey"] = key, ["AZURE_FUNCTIONS_ENVIRONMENT"] = environment,
            ["AI_CREDITS_TEST_20_PAYMENT_LINK_ID"] = "plink_credit", ["AI_CREDITS_TEST_20_PRICE_ID"] = "price_credit"
        }).Build();
        var options = new AICreditPurchaseOptions(config);
        Assert.AreEqual(expected, options.IsConfigured(options.Packages[0]));
        if (mode == "Live") Assert.IsTrue(options.Packages.All(x => x.PaymentLink == ""));
    }

    [DataTestMethod]
    [DataRow("true", "sk_test_fixture", true)]
    [DataRow("true", "rk_test_fixture", true)]
    [DataRow("false", "sk_test_fixture", false)]
    [DataRow(null, "sk_test_fixture", false)]
    [DataRow("invalid", "sk_test_fixture", false)]
    [DataRow("true", "sk_live_fixture", false)]
    public void HostedTestPayments_RequireExplicitOptInAndTestKey(string? optIn, string key, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_CREDITS_LEDGER_ENABLED"] = "true", ["AI_CREDITS_PURCHASES_ENABLED"] = "true",
            ["AI_CREDITS_MODE"] = "Test", ["StripeSecretKey"] = key,
            ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Production", ["AI_CREDITS_ALLOW_HOSTED_TEST_PAYMENTS"] = optIn,
            ["AI_CREDITS_TEST_20_PAYMENT_LINK_ID"] = "plink_fixture", ["AI_CREDITS_TEST_20_PRICE_ID"] = "price_fixture"
        }).Build();
        var options = new AICreditPurchaseOptions(config);
        Assert.AreEqual(expected, options.IsConfigured(options.Packages[0]));
        Assert.AreEqual(expected, options.IsFulfillmentConfigured(options.Packages[0]));
    }

    [TestMethod]
    public async Task ThreeProjects_ShareBaseAndPurchasedWallet_WithoutAffectingOtherUser()
    {
        var db = new CreditDatabase();
        var ledger = new AICreditLedger(() => db);
        await Fund(ledger);
        var start = DateTimeOffset.UtcNow;
        var summary = AIUsageSummaryService.Calculate("starter", start, start.AddMonths(1), 20, 0);
        foreach (var project in new[] { "project-a", "project-b", "project-c" })
        {
            var usage = Usage(project, 10);
            usage.ProjectId = project;
            await new AICreditLedger(() => db).ReserveAsync(usage, summary);
            await ledger.CompleteAsync(usage.Id, true);
        }
        var wallet = await ledger.GetWalletAsync("user");
        Assert.AreEqual(20m, wallet.BaseUsed);
        Assert.AreEqual(10m, wallet.Purchased);
        var other = Usage("other-user-job", 5);
        other.UserId = "other-user";
        other.ProjectId = "other-user-project";
        await ledger.ReserveAsync(other, summary);
        Assert.AreEqual(wallet, await ledger.GetWalletAsync("user"));
        Assert.AreEqual(5m, (await ledger.GetWalletAsync("other-user")).BaseUsed);
        await Assert.ThrowsExceptionAsync<AICreditQuotaException>(() => ledger.ReserveAsync(Usage("new-project", 11), summary));
        Assert.AreEqual(wallet, await ledger.GetWalletAsync("user"));
    }

    [TestMethod]
    public void Qualification_RejectsRecurringWrongPriceWrongAmountAndUnpaid()
    {
        var intent = new PaymentIntent { Id = "pi", Status = "succeeded", AmountReceived = 2000, Currency = "usd" };
        var session = new Session { Mode = "payment", PaymentStatus = "paid", PaymentIntentId = "pi", PaymentLinkId = "plink_credit", AmountTotal = 2000, Currency = "usd" };
        var price = new Price { Id = "price_credit", Type = "one_time", UnitAmount = 2000, Currency = "usd" };
        var lines = new List<LineItem> { new() { Quantity = 1, Price = price } };
        Assert.IsTrue(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
        session.PaymentLinkId = null;
        Assert.IsFalse(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
        session.ClientReferenceId = "credit_server_attempt";
        Assert.IsTrue(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
        session.PaymentLinkId = "plink_other";
        Assert.IsFalse(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
        session.PaymentLinkId = "plink_credit";
        price.Type = "recurring";
        Assert.IsFalse(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
        price.Type = "one_time"; price.Id = "price_other";
        Assert.IsFalse(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
        price.Id = "price_credit"; intent.AmountReceived = 6000;
        Assert.IsFalse(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
        intent.AmountReceived = 2000; session.PaymentStatus = "unpaid";
        Assert.IsFalse(StripeCreditPurchaseHandler.Qualifies(intent, session, lines, Pack));
    }

    [TestMethod]
    public void SignupCount_DistinctActivations_ExcludesAttemptsRenewalsAndCreditPurchases()
    {
        static Dictionary<string, AttributeValue> Row(string id, string status, string name = "Plus") => new()
        { ["SubscriptionId"] = new() { S = id }, ["SubscriptionStatus"] = new() { S = status }, ["Name"] = new() { S = name } };
        Assert.AreEqual(3, AccountSubscriptionService.CountSignups([
            Row("sub_first", "incomplete"), Row("sub_first", "active"), Row("sub_first", "active"), Row("sub_first", "canceled"),
            Row("sub_second", "active"), Row("sub_failed", "incomplete_expired"), Row("pi_credit", "Paid", "One Time Purchase"),
            Row("pi_starter", "Paid", "Starter"), Row("pi_starter", "Paid", "Starter")
        ]));
    }

    private static AIGenerationUsage Usage(string id, decimal units) => new()
    { Id = id, UserId = "user", GenerationJobId = id, Feature = "test", GenerationUnits = units };
    private static async Task Fund(AICreditLedger ledger)
    {
        var attempt = await ledger.CreateAttemptAsync("user", Pack, true);
        await ledger.GrantAsync(attempt, Pack, "pi_one", "cs_one", "evt", DateTimeOffset.UtcNow);
    }

    // Atomic DynamoDB test double: all conditions are evaluated before any writes.
    internal sealed class CreditDatabase : AmazonDynamoDBClient
    {
        private readonly object gate = new();
        public Dictionary<(string Table, string Id), Dictionary<string, AttributeValue>> Rows { get; } = new();
        public bool FailNextTransaction { get; set; }
        public CreditDatabase() : base(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast1 }) { }
        protected override void Dispose(bool disposing) { }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken token = default)
        {
            lock (gate) return Task.FromResult(new GetItemResponse { Item = Rows.GetValueOrDefault((request.TableName, request.Key["Id"].S)) is { } row ? new(row) : new() });
        }
        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken token = default)
        {
            lock (gate)
            {
                Rows.Add((request.TableName, request.Item["Id"].S), new(request.Item));
                return Task.FromResult(new PutItemResponse());
            }
        }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken token = default)
        {
            lock (gate)
            {
                if (FailNextTransaction) { FailNextTransaction = false; throw new InvalidOperationException("simulated database failure"); }
                foreach (var entry in request.TransactItems)
                {
                    var table = entry.Put?.TableName ?? entry.Update.TableName;
                    var id = entry.Put?.Item["Id"].S ?? entry.Update.Key["Id"].S;
                    var row = Rows.GetValueOrDefault((table, id));
                    var condition = entry.Put?.ConditionExpression ?? entry.Update.ConditionExpression;
                    var values = entry.Put is { } putValues ? putValues.ExpressionAttributeValues : entry.Update.ExpressionAttributeValues;
                    var valid = condition switch
                    {
                        "attribute_not_exists(Id)" => row is null,
                        "Revision = :revision" => row?.GetValueOrDefault("Revision")?.N == values[":revision"].N,
                        "#data = :previous" => row?.GetValueOrDefault("Data")?.S == values[":previous"].S,
                        _ => throw new AssertFailedException("Unexpected condition " + condition)
                    };
                    if (!valid) throw new TransactionCanceledException("conflict") { CancellationReasons = [new() { Code = "ConditionalCheckFailed" }] };
                }
                foreach (var entry in request.TransactItems)
                {
                    if (entry.Put is { } put) Rows[(put.TableName, put.Item["Id"].S)] = new(put.Item);
                    else Rows[(entry.Update.TableName, entry.Update.Key["Id"].S)]["Data"] = entry.Update.ExpressionAttributeValues[":next"];
                }
                return Task.FromResult(new TransactWriteItemsResponse());
            }
        }
    }
}
