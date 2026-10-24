using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Navigation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PostAuthRoutingStoreTests
{
    [DataTestMethod]
    [DataRow("active", true)]
    [DataRow("Active", true)]
    [DataRow("Paid", true)]
    [DataRow("trialing", false)]
    [DataRow("past_due", false)]
    [DataRow("canceled", false)]
    [DataRow("cancelled", false)]
    [DataRow("expired", false)]
    [DataRow("inactive", false)]
    [DataRow("unpaid", false)]
    [DataRow("incomplete", false)]
    [DataRow(null, false)]
    [DataRow("", false)]
    public void Subscription_OnlyPersistedPaidStatesQualify(string? status, bool expected)
    {
        var row = new Dictionary<string, AttributeValue>();
        if (status is not null) row["SubscriptionStatus"] = new() { S = status };
        Assert.AreEqual(expected, PostAuthRoutingStore.HasPaidAccess(row));
    }

    [TestMethod]
    public void ScheduledCancellation_StillActiveRetainsAccess()
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["SubscriptionStatus"] = new() { S = "active" },
            ["cancel_at_period_end"] = new() { BOOL = true }
        };
        Assert.IsTrue(PostAuthRoutingStore.HasPaidAccess(row));
        row["SubscriptionStatus"].S = "canceled";
        Assert.IsFalse(PostAuthRoutingStore.HasPaidAccess(row));
    }

    [TestMethod]
    public void ClientStyleStatusFlag_CannotGrantAccess() =>
        Assert.IsFalse(PostAuthRoutingStore.HasPaidAccess(new()
        {
            ["Status"] = new() { S = "active" }, ["HasSubscription"] = new() { BOOL = true }
        }));

    [TestMethod]
    public async Task CanonicalUsernameLookup_UsesResolvedIdAndPaginatesSubscriptions()
    {
        var client = new DynamoStub();
        client.Results.Enqueue(new() { Items = [], NextToken = "next-page" });
        client.Results.Enqueue(new() { Items = [new() { ["SubscriptionStatus"] = new() { S = "active" } }] });
        string? queriedUsername = null;
        var store = new PostAuthRoutingStore(username =>
        {
            queriedUsername = username;
            return Task.FromResult<CanonicalUserStore.CanonicalUser?>(new("users-id", username));
        }, () => client);
        Assert.AreEqual("/init/dashboard", await PostAuthDestinationResolver.ResolveAsync("username", null, store));
        Assert.AreEqual("username", queriedUsername);
        Assert.AreEqual(2, client.Calls.Count);
        Assert.IsTrue(client.Calls.All(call => call.Statement == "SELECT * FROM Subscriptions WHERE UserId = ?" && call.Values.Single() == "users-id"));
        Assert.AreEqual("next-page", client.Calls[1].NextToken);
    }

    [DataTestMethod]
    [DataRow(true, "/init/gen?s=1&cid=ABC")]
    [DataRow(false, "/social-landing.html")]
    public async Task ResumeProject_PreservesOwnershipCheck(bool owned, string expected)
    {
        var client = new DynamoStub();
        client.Results.Enqueue(new() { Items = owned ? [new() { ["Id"] = new() { S = "ABC" } }] : [] });
        if (!owned) client.Results.Enqueue(new() { Items = [] });
        var store = new PostAuthRoutingStore(
            username => Task.FromResult<CanonicalUserStore.CanonicalUser?>(new("users-id", username, CurrentPage: "/init/gen?s=1&cid=ABC")),
            () => client);
        Assert.AreEqual(expected, await PostAuthDestinationResolver.ResolveAsync("name", null, store));
        CollectionAssert.AreEqual(new[] { "ABC", "users-id" }, client.Calls[0].Values);
        Assert.AreEqual(owned ? 1 : 2, client.Calls.Count);
    }

    [TestMethod]
    public async Task ResumeWithoutResourceId_PreservesRouteWithoutOwnershipLookup()
    {
        var store = new PostAuthRoutingStore(
            username => Task.FromResult<CanonicalUserStore.CanonicalUser?>(new("users-id", username, CurrentPage: "/init/gen?s=1")),
            () => throw new AssertFailedException("No data lookup should be needed."));
        Assert.AreEqual("/init/gen?s=1", await PostAuthDestinationResolver.ResolveAsync("name", null, store));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Endpoint_SignedUsernameWinsOverForgedBodyAndUserIdClaim()
    {
        var previous = Environment.GetEnvironmentVariable("JwtSecretKey");
        try
        {
            // Test-only signing configuration, restored immediately; no deployed settings change.
            Environment.SetEnvironmentVariable("JwtSecretKey", "auth-routing-unit-test-signing-secret-123456789");
            var token = DesignMode.AI.Shared.Util.Utilities.GenerateJwt("signed-username", "untrusted-for-routing-id");
            var store = new PostAuthDestinationResolverTests.RoutingStore { User = new("canonical-users-id", null), Paid = false };
            var function = new PostAuthDestinationFunction(new JwtUserResolver(), store, NullLogger<PostAuthDestinationFunction>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = $"Bearer {token}";
            context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                """{"Username":"victim","Email":"victim","UserId":"victim-id","HasSubscription":true,"CurrentPage":"/init/dashboard"}"""));
            var result = (OkObjectResult)await function.Resolve(context.Request);
            Assert.AreEqual("signed-username", store.Username);
            Assert.AreEqual("canonical-users-id", store.SubscriptionUserId);
            StringAssert.Contains(System.Text.Json.JsonSerializer.Serialize(result.Value), "/social-landing.html");
        }
        finally
        {
            Environment.SetEnvironmentVariable("JwtSecretKey", previous);
        }
    }

    [TestMethod]
    public async Task Endpoint_UnsignedIdentityAndFakeSubscriptionAreRejected()
    {
        var store = new PostAuthDestinationResolverTests.RoutingStore { Paid = true };
        var function = new PostAuthDestinationFunction(new JwtUserResolver(), store, NullLogger<PostAuthDestinationFunction>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"Username":"victim","UserId":"victim-id","HasSubscription":true,"ReturnUrl":"/init/dashboard"}"""));
        Assert.IsInstanceOfType(await function.Resolve(context.Request), typeof(UnauthorizedResult));
        Assert.AreEqual(0, store.UserCalls);
        Assert.AreEqual(0, store.SubscriptionCalls);
    }

    private sealed class DynamoStub : AmazonDynamoDBClient
    {
        public Queue<ExecuteStatementResponse> Results { get; } = new();
        public List<(string Statement, string[] Values, string? NextToken)> Calls { get; } = new();

        public DynamoStub() : base(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast1 }) { }

        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add((request.Statement, request.Parameters.Select(value => value.S).ToArray(), request.NextToken));
            return Task.FromResult(Results.Dequeue());
        }
    }
}
