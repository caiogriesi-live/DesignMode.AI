using System.Net;
using System.Text;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AccountSubscriptionTests
{
    [TestMethod]
    public async Task Cancel_UsesCanonicalUserAndStoredSubscription_PaginatesAndSchedulesWithoutDatabaseWrites()
    {
        using var db = new Database();
        db.Pages.Enqueue(new() { Items = [], NextToken = "next" });
        db.Pages.Enqueue(new() { Items = [Row()] });
        using var http = new StripeHttp();
        var result = await Service(db, http).GetAsync("signed-name", true);
        Assert.AreEqual("canonical@example.test", result!.Username);
        Assert.AreEqual("Plus", result.Plan);
        Assert.IsTrue(result.CancellationScheduled);
        Assert.IsFalse(result.CanCancel);
        Assert.AreEqual("active", result.Status);
        Assert.AreEqual(2, db.Queries.Count);
        Assert.IsTrue(db.Queries.All(q => q.Statement == "SELECT * FROM Subscriptions WHERE UserId = ?" && q.User == "canonical-user"));
        Assert.AreEqual("next", db.Queries[1].Next);
        Assert.AreEqual(2, http.Requests.Count);
        Assert.AreEqual("POST", http.Requests[1].Method);
        StringAssert.EndsWith(http.Requests[1].Path, "/v1/subscriptions/sub_owned");
        StringAssert.Contains(http.Requests[1].Body, "cancel_at_period_end=true");
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.IsFalse(json.Contains("sub_owned") || json.Contains("canonical-user"));
    }

    [DataTestMethod]
    [DataRow("active", false, true)]
    [DataRow("trialing", false, true)]
    [DataRow("past_due", false, true)]
    [DataRow("active", true, false)]
    [DataRow("incomplete", false, false)]
    [DataRow("canceled", false, false)]
    [DataRow("incomplete_expired", false, false)]
    public async Task Read_UsesLiveStripeState(string status, bool scheduled, bool canCancel)
    {
        using var db = Database.WithRow();
        using var http = new StripeHttp { Status = status, Scheduled = scheduled };
        var result = await Service(db, http).GetAsync("signed-name");
        Assert.AreEqual(canCancel, result!.CanCancel);
        Assert.AreEqual(scheduled, result.CancellationScheduled);
        Assert.AreEqual(1, http.Requests.Count);
    }

    [TestMethod]
    public async Task AlreadyScheduled_CancelDoesNotSendAnotherUpdate()
    {
        using var db = Database.WithRow();
        using var http = new StripeHttp { Scheduled = true };
        Assert.IsTrue((await Service(db, http).GetAsync("signed-name", true))!.CancellationScheduled);
        Assert.AreEqual(1, http.Requests.Count);
    }

    [TestMethod]
    public async Task NoSubscription_HasNoCancelActionOrStripeCall()
    {
        using var db = new Database();
        db.Pages.Enqueue(new() { Items = [] });
        using var http = new StripeHttp();
        var result = await Service(db, http).GetAsync("signed-name");
        Assert.AreEqual("None", result!.Plan);
        Assert.IsFalse(result.CanCancel);
        Assert.AreEqual(0, http.Requests.Count);
    }

    [TestMethod]
    public async Task MultipleLiveSubscriptions_NeverChooseAnArbitraryCancellation()
    {
        using var db = new Database();
        db.Pages.Enqueue(new() { Items = [Row(), Row("sub_second")] });
        using var http = new StripeHttp();
        await Assert.ThrowsExceptionAsync<MultipleAccountSubscriptionsException>(() => Service(db, http).GetAsync("signed-name", true));
        Assert.IsTrue(http.Requests.All(r => r.Method == "GET"));
    }

    [TestMethod]
    public async Task Anonymous_CannotReadOrCancel()
    {
        var service = new AccountSubscriptionService(_ => throw new AssertFailedException("No user lookup expected"),
            () => throw new AssertFailedException(), () => throw new AssertFailedException());
        var endpoint = new AccountFunction(new JwtUserResolver(), service, NullLogger<AccountFunction>.Instance);
        var request = new DefaultHttpContext().Request;
        Assert.IsInstanceOfType(await endpoint.Get(request), typeof(UnauthorizedResult));
        Assert.IsInstanceOfType(await endpoint.Cancel(request), typeof(UnauthorizedResult));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task Endpoint_IgnoresForgedIdsAndReturnsSafeProviderFailure(bool fail)
    {
        var previous = Environment.GetEnvironmentVariable("JwtSecretKey");
        try
        {
            Environment.SetEnvironmentVariable("JwtSecretKey", "account-unit-test-signing-secret-1234567890");
            using var db = Database.WithRow();
            using var http = new StripeHttp { Fail = fail };
            var endpoint = new AccountFunction(new JwtUserResolver(), Service(db, http), NullLogger<AccountFunction>.Instance);
            var request = new DefaultHttpContext().Request;
            request.Headers.Authorization = "Bearer " + DesignMode.AI.Shared.Util.Utilities.GenerateJwt("signed-name", "victim-user");
            request.QueryString = new QueryString("?UserId=victim&SubscriptionId=sub_victim");
            request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"SubscriptionId":"sub_victim","Username":"victim"}"""));
            var result = (ObjectResult)await endpoint.Cancel(request);
            Assert.AreEqual(fail ? 503 : 200, result.StatusCode);
            Assert.IsTrue(http.Requests.All(r => r.Path.EndsWith("/sub_owned")));
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(result.Value).Contains("provider-secret"));
            if (!fail) Assert.IsTrue(((AccountSummary)result.Value!).CancellationScheduled);
        }
        finally { Environment.SetEnvironmentVariable("JwtSecretKey", previous); }
    }

    private static AccountSubscriptionService Service(Database db, StripeHttp http) => new(
        username =>
        {
            Assert.AreEqual("signed-name", username);
            return Task.FromResult<CanonicalUserStore.CanonicalUser?>(new("canonical-user", "canonical@example.test"));
        }, () => db, () => new SubscriptionService(new StripeClient("sk_test_offline",
            httpClient: new SystemNetHttpClient(new HttpClient(http)))));

    private static Dictionary<string, AttributeValue> Row(string id = "sub_owned") => new()
    {
        ["SubscriptionId"] = new() { S = id }, ["Name"] = new() { S = "Plus" },
        ["SubscriptionStatus"] = new() { S = "active" }
    };

    private sealed class Database : AmazonDynamoDBClient
    {
        public Queue<ExecuteStatementResponse> Pages { get; } = new();
        public List<(string Statement, string User, string? Next)> Queries { get; } = [];
        public Database() : base(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast1 }) { }
        public static Database WithRow()
        {
            var db = new Database();
            db.Pages.Enqueue(new() { Items = [Row()] });
            return db;
        }
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
        {
            Queries.Add((request.Statement, request.Parameters.Single().S, request.NextToken));
            return Task.FromResult(Pages.Dequeue());
        }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Scheduling cancellation must not change stored access.");
    }

    private sealed class StripeHttp : HttpMessageHandler
    {
        public string Status { get; init; } = "active";
        public bool Scheduled { get; init; }
        public bool Fail { get; init; }
        public List<(string Method, string Path, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Fail) return new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"provider-secret","type":"invalid_request_error"}}""") };
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = request.RequestUri.Segments.Last(), @object = "subscription", status = Status,
                cancel_at_period_end = Scheduled || request.Method == HttpMethod.Post
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
