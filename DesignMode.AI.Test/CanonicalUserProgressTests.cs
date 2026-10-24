using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class CanonicalUserProgressTests
{
    [DataTestMethod]
    [DataRow("/designerintro?name=A%26B&funnelId=f", true)]
    [DataRow("https://app.example/designerintro?funnelId=f", true)]
    [DataRow("https://evil.example/designerintro", false)]
    [DataRow("//evil.example/designerintro", false)]
    [DataRow("javascript:alert(1)", false)]
    public void LegacyPage_IsNormalizedOnlyForTrustedOrigin(string page, bool valid)
    {
        Assert.AreEqual(valid, CanonicalUserProgress.TryNormalizePage(page, "https://app.example", out var route));
        if (valid)
        {
            Assert.IsTrue(route.StartsWith("/designerintro"));
            Assert.IsFalse(route.Contains("https:"));
        }
    }

    [TestMethod]
    public async Task Progress_UpdatesCanonicalAccountWithoutReplacingCredentialsOrCreatingAnotherUser()
    {
        var client = new ProgressClient();
        string? lookup = null;
        var store = new PostAuthRoutingStore(username =>
        {
            lookup = username;
            return Task.FromResult<CanonicalUserStore.CanonicalUser?>(new("canonical-id", "StoredUsername", "hash", "salt"));
        }, () => client);
        await store.SaveCurrentPageAsync("authenticated-name", "/designerintro?funnelId=f&name=A%26B");
        Assert.AreEqual("authenticated-name", lookup);
        var update = client.Update!;
        Assert.AreEqual("Users", update.TableName);
        Assert.AreEqual("canonical-id", update.Key["Id"].S);
        Assert.AreEqual("StoredUsername", update.ExpressionAttributeValues[":username"].S);
        Assert.AreEqual("/designerintro?funnelId=f&name=A%26B", update.ExpressionAttributeValues[":page"].S);
        Assert.AreEqual("SET #page = :page, #updated = :updated", update.UpdateExpression);
        Assert.AreEqual("attribute_exists(Id) AND #username = :username", update.ConditionExpression);
    }

    [TestMethod]
    public async Task MissingAccount_DoesNotCreateIncompleteUsersRow()
    {
        var store = new PostAuthRoutingStore(_ => Task.FromResult<CanonicalUserStore.CanonicalUser?>(null),
            () => throw new AssertFailedException("No write should occur."));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SaveCurrentPageAsync("name", "/designerintro"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task Endpoint_SavesSignedAccountBeforeRoutingOrReportsFailure(bool fails)
    {
        var previous = Environment.GetEnvironmentVariable("JwtSecretKey");
        try
        {
            Environment.SetEnvironmentVariable("JwtSecretKey", "progress-unit-test-signing-secret-123456789");
            var token = DesignMode.AI.Shared.Util.Utilities.GenerateJwt("signed-name", "claim-id");
            var store = new PostAuthDestinationResolverTests.RoutingStore { ThrowSave = fails };
            var function = new PostAuthDestinationFunction(new JwtUserResolver(), store, NullLogger<PostAuthDestinationFunction>.Instance);
            var request = new DefaultHttpContext().Request;
            request.Headers.Authorization = $"Bearer {token}";
            request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                """{"CurrentFunnelRoute":"/designerintro?funnelId=f","Username":"forged-name","UserId":"forged-id"}"""));
            var result = (ObjectResult)await function.Resolve(request);
            if (fails)
            {
                Assert.AreEqual(503, result.StatusCode);
                Assert.AreEqual(0, store.UserCalls);
                Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(result.Value).Contains("private"));
            }
            else
            {
                Assert.IsInstanceOfType(result, typeof(OkObjectResult));
                Assert.AreEqual("signed-name", store.SavedUsername);
                Assert.AreEqual("/designerintro?funnelId=f", store.SavedCurrentPage);
                Assert.AreEqual(0, store.SubscriptionCalls);
            }
        }
        finally { Environment.SetEnvironmentVariable("JwtSecretKey", previous); }
    }

    private sealed class ProgressClient : AmazonDynamoDBClient
    {
        public UpdateItemRequest? Update;
        public ProgressClient() : base(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast1 }) { }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
        {
            Update = request;
            return Task.FromResult(new UpdateItemResponse());
        }
    }
}
