using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GenerationProjectAuthorizationTests
{
    internal const string User = "11111111-1111-1111-1111-111111111111";
    internal const string Cid = "saved-company-idea";

    [TestMethod]
    public async Task ReturningOwner_NoAcquisitionRecords_UsesExistingLegacyIdentityWithoutWrites()
    {
        using var db = new Database();
        var result = await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid);
        Assert.AreEqual(Cid, result?.ProjectId);
        Assert.AreEqual("", result?.FunnelId);
        Assert.AreEqual(0, db.Writes);
        Assert.AreEqual(0, db.FunnelReads);
    }

    [TestMethod]
    public async Task OwnedMapping_PreservesCanonicalAccountingIdentityWithoutFunnelLookup()
    {
        using var db = new Database { Mapping = Row(("Id", "project"), ("UserId", User), ("OriginatingFunnelId", "missing-funnel")) };
        var result = await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid);
        Assert.AreEqual("project", result?.ProjectId);
        Assert.AreEqual("missing-funnel", result?.FunnelId);
        Assert.AreEqual(0, db.FunnelReads);
    }

    [DataTestMethod]
    [DataRow("other-owner", Cid)]
    [DataRow(User, "unknown-cid")]
    [DataRow("", Cid)]
    public async Task OtherOwnerUnknownProjectOrMissingIdentity_IsDenied(string owner, string cid)
    {
        using var db = new Database { PromptOwner = owner };
        Assert.IsNull(await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, cid));
    }

    [TestMethod]
    public async Task MissingCanonicalUser_IsDeniedEvenWhenPromptMatches()
    {
        using var db = new Database { UserExists = false };
        Assert.IsNull(await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid));
    }

    [TestMethod]
    public async Task LegacyUsername_IsResolvedOnlyFromCanonicalUsersRow()
    {
        using var db = new Database { PromptOwner = "stored-username" };
        Assert.IsNotNull(await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid));
    }

    [TestMethod]
    public async Task EmptyFirstPage_DoesNotHideOwnedPrompt()
    {
        using var db = new Database { EmptyFirstPage = true };
        Assert.IsNotNull(await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid));
        Assert.AreEqual(2, db.PromptReads);
    }

    [TestMethod]
    public async Task ForeignCanonicalProject_DoesNotFallBackToSameNamedLegacyProject()
    {
        using var db = new Database { Project = Row(("Id", Cid), ("UserId", "other")) };
        Assert.IsNull(await GenerationProjectAuthorization.ResolveProjectAsync(db, User, Cid));
        Assert.AreEqual(0, db.PromptReads);
    }

    [TestMethod]
    public async Task ForeignMapping_IsDenied()
    {
        using var db = new Database { Mapping = Row(("Id", "project"), ("UserId", "other")) };
        Assert.IsNull(await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid));
    }

    [TestMethod]
    public async Task OwnedMappingToOtherUsersCid_IsDeniedAtGenerationEndpoint()
    {
        using var db = new Database
        {
            PromptOwner = "other",
            Project = Row(("Id", "project"), ("UserId", User), ("LegacyCompanyIdeaId", Cid))
        };
        Assert.IsNull(await GenerationProjectAuthorization.ResolveProjectAsync(db, User, "project"));
    }

    [TestMethod]
    public async Task PaidEndpointResolution_RechecksLegacyOwnership()
    {
        using var db = new Database();
        Assert.IsNotNull(await GenerationProjectAuthorization.ResolveProjectAsync(db, User, Cid));
        db.PromptOwner = "other";
        Assert.IsNull(await GenerationProjectAuthorization.ResolveProjectAsync(db, User, Cid));
    }

    internal static Dictionary<string, AttributeValue> Row(params (string Key, string Value)[] values) =>
        values.ToDictionary(x => x.Key, x => new AttributeValue { S = x.Value });

    internal static Database SubscriptionLinkedProject() => new()
    {
        PromptInvite = "owned-invite",
        Subscriptions = [Row(("UserId", User), ("InviteId", "owned-invite"), ("ProjectId", "original-project"), ("FunnelId", "original-funnel"))],
        Project = Row(("Id", "original-project"), ("UserId", User), ("LegacyCompanyIndustryId", "company"),
            ("OriginatingFunnelId", "original-funnel"), ("AcquisitionId", "original-acquisition"))
    };

    [TestMethod]
    public async Task MissingCidMapping_UsesExactOwnedInviteSubscription_PreservesAttributionWithoutWrites()
    {
        using var db = SubscriptionLinkedProject();
        db.EmptyFirstSubscriptionPage = true;
        var context = await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid);
        Assert.AreEqual("original-project", context?.ProjectId);
        Assert.AreEqual("original-funnel", context?.FunnelId);
        Assert.AreEqual("original-acquisition", context?.AcquisitionId);
        Assert.AreEqual(2, db.SubscriptionReads);
        Assert.AreEqual(0, db.FunnelReads);
        Assert.AreEqual(0, db.Writes);
    }

    [DataTestMethod]
    [DataRow("foreign-subscription")]
    [DataRow("different-invite")]
    [DataRow("foreign-project")]
    [DataRow("missing-project")]
    [DataRow("different-cid")]
    [DataRow("different-funnel")]
    [DataRow("ambiguous-projects")]
    public async Task InvalidSubscriptionLink_DoesNotFallBackToUnattributedCid(string failure)
    {
        using var db = SubscriptionLinkedProject();
        switch (failure)
        {
            case "foreign-subscription": db.Subscriptions[0]["UserId"].S = "other"; break;
            case "different-invite": db.Subscriptions[0]["InviteId"].S = "other"; break;
            case "foreign-project": db.Project!["UserId"].S = "other"; break;
            case "missing-project": db.Project = null; break;
            case "different-cid": db.Project!["LegacyCompanyIdeaId"] = new() { S = "other" }; break;
            case "different-funnel": db.Subscriptions[0]["FunnelId"].S = "other"; break;
            case "ambiguous-projects": db.Subscriptions.Add(Row(("UserId", User), ("InviteId", "owned-invite"), ("ProjectId", "other"))); break;
        }
        Assert.IsNull(await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid));
        Assert.AreEqual(0, db.Writes);
    }

    [TestMethod]
    public async Task ExplicitCidMapping_RemainsPrimaryWithoutSubscriptionLookup()
    {
        using var db = SubscriptionLinkedProject();
        db.Mapping = db.Project;
        Assert.AreEqual("original-project", (await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, User, Cid))?.ProjectId);
        Assert.AreEqual(0, db.SubscriptionReads);
    }

    internal sealed class Database : AmazonDynamoDBClient
    {
        public Database() : base(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1) { }
        public bool UserExists = true;
        public string PromptOwner = User;
        public HashSet<string> OwnedCompanyIdeas = [Cid];
        public DateTimeOffset PaidAt = DateTimeOffset.UtcNow.AddDays(-1);
        public bool EmptyFirstPage;
        public string? PromptInvite;
        public List<Dictionary<string, AttributeValue>> Subscriptions = [];
        public bool EmptyFirstSubscriptionPage;
        public int SubscriptionReads;
        public Dictionary<string, AttributeValue>? Mapping;
        public Dictionary<string, AttributeValue>? Project;
        public Dictionary<string, AttributeValue>? Funnel;
        public int Writes, FunnelReads, PromptReads;
        public List<Dictionary<string, AttributeValue>> UsageRows = [];
        public int UsageCompletions;
        public bool FailUsageWrites;
        public int TransientUsageUpdateFailures;
        public bool Exhausted;
        public override Task<GetItemResponse> GetItemAsync(string table, Dictionary<string, AttributeValue> key, CancellationToken token = default) =>
            GetItemAsync(new GetItemRequest { TableName = table, Key = key }, token);
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken token = default)
        {
            var row = request.TableName switch
            {
                "Users" => UserExists ? Row(("Id", User), ("Username", "stored-username")) : [],
                "Projects" => Project is not null && (!Project.ContainsKey("Id") || Project["Id"].S == request.Key["Id"].S) ? Project : [],
                "Funnels" => Funnel ?? [],
                _ => throw new AssertFailedException("Unexpected table " + request.TableName)
            };
            if (request.TableName == "Funnels") FunnelReads++;
            return Task.FromResult(new GetItemResponse { Item = row });
        }
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken token = default)
        {
            if (request.Statement.Contains("FROM Prompt"))
            {
                PromptReads++;
                if (EmptyFirstPage && request.NextToken is null)
                    return Task.FromResult(new ExecuteStatementResponse { Items = [], NextToken = "next" });
                // Return even foreign rows to prove the authorization layer validates results.
                var requestedCid = request.Parameters.Last().S;
                if (!OwnedCompanyIdeas.Contains(requestedCid))
                    return Task.FromResult(new ExecuteStatementResponse { Items = [] });
                var prompt = Row(("Id", "prompt"), ("UserId", PromptOwner), ("CompanyIdeaId", requestedCid));
                if (PromptInvite is not null) prompt["InviteId"] = new() { S = PromptInvite };
                return Task.FromResult(new ExecuteStatementResponse { Items = [prompt] });
            }
            if (request.Statement.Contains("FROM Projects"))
                return Task.FromResult(new ExecuteStatementResponse { Items = Mapping is null ? [] : [Mapping] });
            if (request.Statement.Contains("FROM Subscriptions"))
            {
                SubscriptionReads++;
                Assert.AreEqual(PromptInvite, request.Parameters[0].S);
                Assert.AreEqual(User, request.Parameters[1].S);
                if (EmptyFirstSubscriptionPage && request.NextToken is null)
                    return Task.FromResult(new ExecuteStatementResponse { Items = [], NextToken = "next" });
                return Task.FromResult(new ExecuteStatementResponse { Items = Subscriptions });
            }
            throw new AssertFailedException("Unexpected query");
        }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken token = default)
        {
            Writes++;
            if (request.TableName == "AIGenerationUsage")
            {
                if (TransientUsageUpdateFailures-- > 0) throw new ProvisionedThroughputExceededException("Simulated throttling.");
                UsageCompletions++;
                var usage = UsageRows.SingleOrDefault(row => row["Id"].S == request.Key["Id"].S);
                if (usage is not null && request.ExpressionAttributeValues.TryGetValue(":status", out var status))
                    usage["Status"] = status;
                if (usage is not null)
                    foreach (var (field, placeholder) in new[] { ("ActualCost", ":actual"), ("EstimatedCost", ":estimated"),
                        ("InputTokens", ":input"), ("CachedInputTokens", ":cached"), ("OutputTokens", ":output"),
                        ("Model", ":model"), ("ProviderRequestId", ":request"), ("PricingVersion", ":version") })
                        if (request.ExpressionAttributeValues.TryGetValue(placeholder, out var value)) usage[field] = value;
            }
            if (request.TableName is "Projects" or "Funnels")
                Assert.AreEqual("attribute_not_exists(UserId) OR UserId = :userId", request.ConditionExpression);
            return Task.FromResult(new UpdateItemResponse());
        }
        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken token = default)
        {
            Assert.AreEqual("AIGenerationUsage", request.TableName, "Authorization must not manufacture project or funnel rows.");
            if (FailUsageWrites) throw new InvalidOperationException("Simulated usage storage outage.");
            UsageRows.Add(request.Item);
            return Task.FromResult(new PutItemResponse());
        }
        public override Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken token = default)
        {
            if (request.TableName == "Purchases") return Task.FromResult(new ScanResponse { Items = [Row(("Data",
                System.Text.Json.JsonSerializer.Serialize(new PurchaseRecord
                { UserId = User, Plan = "starter", BillingInterval = "month", PaidAt = PaidAt })))] });
            if (request.TableName == "AIGenerationUsage") return Task.FromResult(new ScanResponse { Items = Exhausted ?
                [new Dictionary<string, AttributeValue> { ["Id"] = new() { S = "usage" }, ["GenerationUnits"] = new() { N = "20" } }] :
                UsageRows.Where(row => row["UserId"].S == request.ExpressionAttributeValues[":user"].S &&
                    AIUsageSummaryService.CountsTowardPaidAllowance(row["UsageScope"].S, row["Status"].S,
                        DateTimeOffset.Parse(row["StartedAt"].S), DateTimeOffset.Parse(request.ExpressionAttributeValues[":start"].S),
                        DateTimeOffset.Parse(request.ExpressionAttributeValues[":end"].S))).ToList() });
            throw new AssertFailedException("Unexpected scan");
        }
    }
}
