using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using DesignMode.AI.Shared.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GoogleCanonicalUserLocalIntegrationTests
{
    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task GoogleProvisioning_ReusesCanonicalUserAcrossRepeatCaseAndConcurrency()
    {
        RequireLocalIntegration();
        var store = new AttributionStore(NullLogger<AttributionStore>.Instance);
        var users = new CanonicalUserStore(store, NullLogger<CanonicalUserStore>.Instance);
        var unique = Guid.NewGuid().ToString("N");
        var newEmail = $"google-{unique}@example.invalid";
        var legacyEmail = $"Legacy-{unique}@Example.Invalid";
        var newUserId = CanonicalUserStore.UserIdForEmail(CanonicalUserStore.NormalizeEmail(newEmail)!);
        var legacyUserId = Guid.NewGuid().ToString();

        try
        {
            var concurrent = await Task.WhenAll(
                users.FindOrCreateGoogleUserAsync(newEmail),
                users.FindOrCreateGoogleUserAsync($" {newEmail.ToUpperInvariant()} "));
            Assert.AreEqual(concurrent[0].Id, concurrent[1].Id);
            Assert.AreEqual(newUserId, concurrent[0].Id);

            using var client = store.CreateClient();
            await client.PutItemAsync("Users", new()
            {
                ["Id"] = new() { S = legacyUserId }, ["Username"] = new() { S = legacyEmail },
                ["PasswordHash"] = new() { S = "legacy-hash" }, ["Salt"] = new() { S = "legacy-salt" }, ["Onboarded"] = new() { S = "0" }
            });

            var reused = await users.FindOrCreateGoogleUserAsync(legacyEmail.ToLowerInvariant());
            Assert.AreEqual(legacyUserId, reused.Id);

            var allUsers = await client.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = "SELECT Id, Username FROM Users" });
            Assert.AreEqual(1, allUsers.Items.Count(item => CanonicalUserStore.NormalizeEmail(item.GetValueOrDefault("Username")?.S) == CanonicalUserStore.NormalizeEmail(newEmail)));
            Assert.AreEqual(1, allUsers.Items.Count(item => CanonicalUserStore.NormalizeEmail(item.GetValueOrDefault("Username")?.S) == CanonicalUserStore.NormalizeEmail(legacyEmail)));
        }
        finally
        {
            using var client = store.CreateClient();
            await client.DeleteItemAsync("Users", Key(newUserId));
            await client.DeleteItemAsync("Users", Key(legacyUserId));
        }
    }

    [TestMethod]
    public async Task JwtResolver_PrefersSignedCanonicalUserIdClaim()
    {
        var previousSecret = Environment.GetEnvironmentVariable("JwtSecretKey");
        try
        {
            Environment.SetEnvironmentVariable("JwtSecretKey", "integration-test-secret-that-is-long-enough-123456");
            var userId = Guid.NewGuid().ToString();
            var token = Utilities.GenerateJwt("person@example.invalid", userId);
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = $"Bearer {token}";
            var resolver = new JwtUserResolver();
            Assert.AreEqual(userId, resolver.ResolveUserId(context.Request));
            Assert.AreEqual("person@example.invalid", resolver.ResolveUsername(context.Request));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JwtSecretKey", previousSecret);
        }

        await Task.CompletedTask;
    }

    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task BindingReturningGoogleUser_UpdatesCurrentContextWithoutReplacingOriginalAcquisition()
    {
        RequireLocalIntegration();
        var store = new AttributionStore(NullLogger<AttributionStore>.Instance);
        var visitorId = Guid.NewGuid().ToString();
        var sessionId = Guid.NewGuid().ToString();
        var funnelId = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid().ToString();
        var originalAcquisitionId = Guid.NewGuid().ToString();
        var originalProjectId = Guid.NewGuid().ToString();
        string? acquisitionId = null;
        string? projectId = null;

        try
        {
            using var client = store.CreateClient();
            await client.PutItemAsync("Users", new()
            {
                ["Id"] = new() { S = userId }, ["Username"] = new() { S = $"returning-{userId}@example.invalid" },
                ["OriginalAcquisitionId"] = new() { S = originalAcquisitionId },
                ["OriginalAcquisitionProjectId"] = new() { S = originalProjectId }
            });
            var capture = await store.CaptureAsync(new()
            {
                VisitorId = visitorId, SessionId = sessionId, FunnelId = funnelId, LandingPage = "/social-landing.html"
            });
            acquisitionId = capture.AcquisitionId;
            var project = await store.CreateProjectAsync(new()
            {
                VisitorId = visitorId, SessionId = sessionId, FunnelId = funnelId
            });
            Assert.IsNotNull(project);
            projectId = project.ProjectId;
            Assert.IsTrue(await store.BindAsync(new BindIdentityRequest
            {
                VisitorId = visitorId, SessionId = sessionId, FunnelId = funnelId, ProjectId = projectId
            }, userId));

            var funnel = await client.GetItemAsync("Funnels", Key(funnelId));
            var savedProject = await client.GetItemAsync("Projects", Key(projectId));
            var savedUser = await client.GetItemAsync("Users", Key(userId));
            Assert.AreEqual(userId, funnel.Item["UserId"].S);
            Assert.AreEqual("Authenticated", funnel.Item["Status"].S);
            Assert.AreEqual(userId, savedProject.Item["UserId"].S);
            Assert.AreEqual(originalAcquisitionId, savedUser.Item["OriginalAcquisitionId"].S);
            Assert.AreEqual(originalProjectId, savedUser.Item["OriginalAcquisitionProjectId"].S);
        }
        finally
        {
            using var client = store.CreateClient();
            if (projectId is not null) await client.DeleteItemAsync("Projects", Key(projectId));
            if (acquisitionId is not null) await client.DeleteItemAsync("AttributionTouches", Key(acquisitionId));
            await client.DeleteItemAsync("Funnels", Key(funnelId));
            await client.DeleteItemAsync("Users", Key(userId));
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [TestCategory("LocalIntegration")]
    public async Task CompleteIdentityChain_BindsCanonicalUserAndReusesExistingRecords(bool googleStyleUser)
    {
        RequireLocalIntegration();
        var previousSecret = Environment.GetEnvironmentVariable("JwtSecretKey");
        Environment.SetEnvironmentVariable("JwtSecretKey", "integration-test-secret-that-is-long-enough-123456");
        var store = new AttributionStore(NullLogger<AttributionStore>.Instance);
        var users = new CanonicalUserStore(store, NullLogger<CanonicalUserStore>.Instance);
        var email = $"identity-chain-{Guid.NewGuid():N}@example.invalid";
        var visitorId = Guid.NewGuid().ToString();
        var originalSessionId = Guid.NewGuid().ToString();
        var resumedSessionId = Guid.NewGuid().ToString();
        var funnelId = Guid.NewGuid().ToString();
        string? userId = null;
        string? projectId = null;
        string? acquisitionId = null;

        try
        {
            using var client = store.CreateClient();
            if (googleStyleUser)
            {
                userId = (await users.FindOrCreateGoogleUserAsync(email)).Id;
            }
            else
            {
                userId = Guid.NewGuid().ToString();
                await client.PutItemAsync("Users", new()
                {
                    ["Id"] = new() { S = userId }, ["Username"] = new() { S = email },
                    ["PasswordHash"] = new() { S = "test-hash" }, ["Salt"] = new() { S = "test-salt" }
                });
            }

            var capture = await store.CaptureAsync(new()
            {
                VisitorId = visitorId, SessionId = originalSessionId, FunnelId = funnelId,
                LandingPage = "/social-landing.html"
            });
            acquisitionId = capture.AcquisitionId;
            var project = await store.CreateProjectAsync(new()
            {
                VisitorId = visitorId, SessionId = originalSessionId, FunnelId = funnelId
            });
            Assert.IsNotNull(project);
            projectId = project.ProjectId;

            var token = Utilities.GenerateJwt(email, userId);
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = $"Bearer {token}";
            var resolver = new JwtUserResolver();
            var resolvedUserId = resolver.ResolveUserId(context.Request);
            Assert.AreEqual(userId, resolvedUserId);
            Assert.AreEqual(userId, await store.ResolveClaimedUserIdAsync(resolvedUserId!));

            var request = new BindIdentityRequest
            {
                VisitorId = visitorId, SessionId = resumedSessionId,
                FunnelId = funnelId, ProjectId = projectId
            };
            Assert.IsTrue((await store.BindWithResultAsync(request, resolvedUserId!)).Success);
            Assert.IsTrue((await store.BindWithResultAsync(request, resolvedUserId!)).Success);

            var savedUser = await client.GetItemAsync("Users", Key(userId));
            var savedFunnel = await client.GetItemAsync("Funnels", Key(funnelId));
            var savedProject = await client.GetItemAsync("Projects", Key(projectId));
            Assert.AreEqual(userId, savedFunnel.Item["UserId"].S);
            Assert.AreEqual(userId, savedProject.Item["UserId"].S);
            Assert.AreEqual(userId, savedUser.Item["Id"].S);
            Assert.AreEqual("Authenticated", savedFunnel.Item["Status"].S);
            Assert.AreEqual(resumedSessionId, savedFunnel.Item["SessionId"].S);
            Assert.AreEqual(projectId, savedFunnel.Item["ProjectId"].S);
            Assert.AreEqual(funnelId, savedProject.Item["OriginatingFunnelId"].S);

            var matchingUsers = await client.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = "SELECT Id, Username FROM Users" });
            Assert.AreEqual(1, matchingUsers.Items.Count(item => CanonicalUserStore.NormalizeEmail(item.GetValueOrDefault("Username")?.S) == email));
            Assert.AreEqual(1, (await client.ScanAsync(new ScanRequest { TableName = "Projects", FilterExpression = "Id = :id", ExpressionAttributeValues = new() { [":id"] = new() { S = projectId } } })).Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JwtSecretKey", previousSecret);
            using var client = store.CreateClient();
            if (projectId is not null) await client.DeleteItemAsync("Projects", Key(projectId));
            if (acquisitionId is not null) await client.DeleteItemAsync("AttributionTouches", Key(acquisitionId));
            await client.DeleteItemAsync("Funnels", Key(funnelId));
            if (userId is not null) await client.DeleteItemAsync("Users", Key(userId));
        }
    }

    private static void RequireLocalIntegration()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();
    }

    private static Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = new() { S = id } };
}
