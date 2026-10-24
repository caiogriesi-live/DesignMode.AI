using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryUnsubscribeTests
{
    private static readonly string Id = new('A', 64);
    private static FunnelRecoveryUnsubscribe Create(Store store, DynamoStub db, string secret = "test-unsubscribe-signing-secret") =>
        new(new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["JwtSecretKey"] = secret }).Build()), store, () => db);
    private static string Token(FunnelRecoveryUnsubscribe service) =>
        new Uri(service.CreateUrl("https://designmode.ai", Id)).Query["?token=".Length..];

    [TestMethod]
    public async Task SignedToken_RejectsTamperingAndWrongPurposeWithoutReadingState()
    {
        var store = new Store();
        var service = Create(store, new());
        var token = Token(service);
        Assert.IsTrue(service.TryValidate(token, out var id));
        Assert.AreEqual(Id, id);
        Assert.IsFalse(await service.UnsubscribeAsync("B" + token[1..]));
        Assert.IsFalse(Create(store, new(), "different-secret").TryValidate(token, out _));
        foreach (var invalid in new string?[] { null, "", token + "x", token[..64], new string('Z', 129) })
            Assert.IsFalse(await service.UnsubscribeAsync(invalid));
        Assert.AreEqual(0, store.Reads);
        Assert.IsFalse(token.Contains("canonical-user"));
    }

    [TestMethod]
    public async Task Unsubscribe_UsesCanonicalUserAndIdempotentWrite_ThenDeniesReminders()
    {
        var db = new DynamoStub();
        var service = Create(new Store(), db);
        Assert.AreEqual(RecoveryMarketingPermission.Allowed, await service.CheckAsync("canonical-user"));
        Assert.IsTrue(await service.UnsubscribeAsync(Token(service)));
        Assert.IsTrue(await service.UnsubscribeAsync(Token(service)));
        Assert.AreEqual("Users", db.Update!.TableName);
        Assert.AreEqual("canonical-user", db.Update.Key["Id"].S);
        Assert.AreEqual("attribute_exists(Id)", db.Update.ConditionExpression);
        StringAssert.Contains(db.Update.UpdateExpression, "if_not_exists(RecoveryEmailsUnsubscribedAt, :now)");
        Assert.AreEqual(RecoveryMarketingPermission.Denied, await service.CheckAsync("canonical-user"));
        Assert.IsTrue(db.Read!.ConsistentRead);
    }

    [TestMethod]
    public async Task UnknownRecoveryOrDeletedUser_DoesNotCreatePreferenceRecord()
    {
        var store = new Store { Missing = true };
        var db = new DynamoStub();
        var service = Create(store, db);
        Assert.IsFalse(await service.UnsubscribeAsync(Token(service)));
        Assert.IsNull(db.Update);
        store.Missing = false;
        db.MissingUser = true;
        Assert.IsFalse(await service.UnsubscribeAsync(Token(service)));
    }

    [TestMethod]
    public async Task Endpoint_ValidatesBodyAndOnlyAcceptsPost()
    {
        var store = new Store();
        var service = Create(store, new());
        var endpoint = new FunnelRecoveryUnsubscribeFunction(service, NullLogger<FunnelRecoveryUnsubscribeFunction>.Instance);
        async Task<IActionResult> Send(string method, string body)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return await endpoint.Run(context.Request);
        }
        Assert.IsInstanceOfType(await Send("GET", Token(service)), typeof(StatusCodeResult));
        Assert.AreEqual(0, store.Reads);
        Assert.IsInstanceOfType(await Send("POST", "invalid"), typeof(BadRequestObjectResult));
        Assert.IsInstanceOfType(await Send("POST", Token(service) + "x"), typeof(BadRequestObjectResult));
        Assert.IsInstanceOfType(await Send("POST", Token(service)), typeof(OkObjectResult));
        store.Fail = true;
        Assert.AreEqual(503, ((StatusCodeResult)await Send("POST", Token(service))).StatusCode);
    }

    [TestMethod]
    public void AllRecoveryTemplates_IncludeWorkingUnsubscribeAndCompanyCopyright()
    {
        var url = Create(new(), new()).CreateUrl("https://designmode.ai", Id);
        var emails = new[] { FunnelRecoveryEmail.Create("https://designmode.ai", "/init/funnelend?id=123&e=abc", unsubscribeUrl: url) }
            .Concat(Enum.GetValues<FunnelRecoveryStep>().Select(step =>
                FunnelRecoveryEmail.CreateReminder(step, "https://designmode.ai", "/init/funnelend?id=123&e=abc", unsubscribeUrl: url)));
        foreach (var email in emails)
        {
            StringAssert.Contains(email.Html, System.Net.WebUtility.HtmlEncode(url));
            StringAssert.Contains(email.Html, ">Unsubscribe</a>");
            StringAssert.Contains(email.Text, url);
            StringAssert.Contains(email.Html, "DesignMode Inc. All rights reserved.");
            StringAssert.Contains(email.Text, "DesignMode Inc. All rights reserved.");
            StringAssert.Contains(email.ResumeUrl, "/login?returnUrl=");
            StringAssert.Contains(Uri.UnescapeDataString(email.ResumeUrl), "/init/funnelend?id=123&e=abc");
        }
    }

    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("mismatch")]
    [DataRow("empty-id")]
    public async Task Permission_RequiresExistingCanonicalUser(string scenario)
    {
        var db = new DynamoStub { MissingUser = scenario == "missing", UserId = scenario == "mismatch" ? "other-user" : "canonical-user" };
        Assert.AreEqual(RecoveryMarketingPermission.Unavailable,
            await Create(new Store(), db).CheckAsync(scenario == "empty-id" ? "" : "canonical-user"));
        if (scenario == "empty-id") Assert.IsNull(db.Read);
    }

    internal sealed class DynamoStub() : AmazonDynamoDBClient(new AnonymousAWSCredentials(),
        new AmazonDynamoDBConfig { ServiceURL = "http://localhost:8000" })
    {
        public UpdateItemRequest? Update;
        public GetItemRequest? Read;
        public bool MissingUser;
        public bool FailRead;
        public bool Unsubscribed;
        public string UserId = "canonical-user";
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
        {
            if (MissingUser) throw new ConditionalCheckFailedException("missing");
            Update = request;
            return Task.FromResult(new UpdateItemResponse());
        }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Read = request;
            if (FailRead) throw new IOException("test-only read failure");
            var item = new Dictionary<string, AttributeValue>();
            if (!MissingUser)
            {
                item["Id"] = new() { S = UserId };
                if (Update is not null || Unsubscribed) item["RecoveryEmailsUnsubscribedAt"] = new() { S = "saved" };
            }
            return Task.FromResult(new GetItemResponse { Item = item });
        }
    }

    internal sealed class Store : IFunnelRecoveryStore
    {
        public bool Missing, Fail;
        public int Reads;
        public Task<FunnelRecoveryState?> ReadAsync(string id)
        {
            Reads++;
            if (Fail) throw new InvalidOperationException();
            Assert.AreEqual(Id, id);
            return Task.FromResult<FunnelRecoveryState?>(Missing ? null :
                new(Id, "canonical-user", "funnel", "project", DateTimeOffset.UtcNow));
        }
        public Task<FunnelRecoveryContext?> ResolveAsync(string id) => throw new NotImplementedException();
        public Task<FunnelRecoveryState> RecordReadyAsync(FunnelRecoveryContext context, DateTimeOffset at) => throw new NotImplementedException();
        public Task<bool> ClaimAsync(string id) => throw new NotImplementedException();
        public Task SuppressAsync(string id) => throw new NotImplementedException();
        public Task MarkSentAsync(string id, string message) => throw new NotImplementedException();
        public Task RejectAsync(string id) => throw new NotImplementedException();
    }
}
