using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AttributionStoreLocalIntegrationTests
{
    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task CaptureAndCreateProject_WriteExpectedLocalRecordsQuickly()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();

        var visitorId = Guid.NewGuid().ToString();
        var sessionId = Guid.NewGuid().ToString();
        var funnelId = Guid.NewGuid().ToString();
        string? acquisitionId = null;
        string? projectId = null;
        string? freshFunnelId = null;
        string? freshAcquisitionId = null;
        string? freshProjectId = null;
        string? checkoutAttemptId = null;
        string? snapshotId = null;
        var userId = Guid.NewGuid().ToString();
        var store = new AttributionStore(NullLogger<AttributionStore>.Instance);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var capture = await store.CaptureAsync(new AcquisitionCaptureRequest
            {
                VisitorId = visitorId,
                SessionId = sessionId,
                FunnelId = funnelId,
                LandingPage = "/social-landing.html",
                LandingVariant = "integration-test"
            });
            acquisitionId = capture.AcquisitionId;
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Capture took {stopwatch.Elapsed}.");

            // A returning anonymous browser can have rotated local attribution IDs.
            // Capture should refresh an unowned funnel so project creation can resume.
            visitorId = Guid.NewGuid().ToString();
            sessionId = Guid.NewGuid().ToString();
            await store.CaptureAsync(new AcquisitionCaptureRequest
            {
                VisitorId = visitorId,
                SessionId = sessionId,
                FunnelId = funnelId,
                LandingPage = "/social-landing.html",
                LandingVariant = "integration-test"
            });

            var project = await store.CreateProjectAsync(new CreateSocialProjectRequest
            {
                VisitorId = visitorId,
                SessionId = sessionId,
                FunnelId = funnelId
            });
            Assert.IsNotNull(project);
            projectId = project.ProjectId;

            using var client = store.CreateClient();
            await client.PutItemAsync("Users", new Dictionary<string, AttributeValue>
            {
                ["Id"] = new() { S = userId }, ["Username"] = new() { S = $"attribution-test-{userId}@example.invalid" }
            });
            Assert.IsTrue(await store.BindAsync(new BindIdentityRequest
            {
                VisitorId = visitorId, SessionId = sessionId, FunnelId = funnelId, ProjectId = projectId
            }, userId));

            var freshCapture = await store.CaptureAsync(new AcquisitionCaptureRequest
            {
                VisitorId = visitorId,
                SessionId = sessionId,
                FunnelId = funnelId,
                LandingPage = "/social-landing.html",
                LandingVariant = "integration-test"
            });
            freshFunnelId = freshCapture.FunnelId;
            freshAcquisitionId = freshCapture.AcquisitionId;
            Assert.AreNotEqual(funnelId, freshFunnelId, "The public landing must not reuse an already account-bound funnel.");
            var freshProject = await store.CreateProjectAsync(new CreateSocialProjectRequest
            {
                VisitorId = visitorId, SessionId = sessionId, FunnelId = freshFunnelId
            });
            Assert.IsNotNull(freshProject);
            freshProjectId = freshProject.ProjectId;

            var checkout = await store.CreateCheckoutAttemptAsync(userId, projectId, funnelId, "integration-test", 1.23m, "usd");
            Assert.IsNotNull(checkout);
            checkoutAttemptId = checkout.CheckoutAttemptId;
            snapshotId = checkout.AttributionSnapshotId;

            var funnel = await client.GetItemAsync("Funnels", Key(funnelId));
            var touch = await client.GetItemAsync("AttributionTouches", Key(acquisitionId));
            var savedProject = await client.GetItemAsync("Projects", Key(projectId));
            var savedCheckout = await client.GetItemAsync("CheckoutAttempts", Key(checkoutAttemptId));
            var savedUser = await client.GetItemAsync("Users", Key(userId));
            Assert.IsTrue(funnel.Item is { Count: > 0 });
            Assert.IsTrue(touch.Item is { Count: > 0 });
            Assert.IsTrue(touch.Item["AdvertisingMeasurementEnabled"].BOOL);
            Assert.AreEqual(AdvertisingMeasurementPolicy.Version, touch.Item["AdvertisingMeasurementPolicyVersion"].S);
            var storedSnapshot = await client.GetItemAsync("AttributionSnapshots", Key(snapshotId));
            var measurement = System.Text.Json.JsonSerializer.Deserialize<AttributionSnapshot>(storedSnapshot.Item["Data"].S)!.Measurement;
            Assert.IsTrue(measurement.AdvertisingConsent, "Authenticated checkout with no consent record still measures internally.");
            Assert.IsTrue(measurement.GoogleAdvertisingConsent);
            Assert.AreEqual(AdvertisingMeasurementPolicy.Version, measurement.AdvertisingMeasurementPolicyVersion);
            Assert.IsTrue(savedProject.Item is { Count: > 0 });
            Assert.IsTrue(savedCheckout.Item is { Count: > 0 });
            Assert.AreEqual(projectId, funnel.Item["ProjectId"].S);
            Assert.AreEqual(userId, funnel.Item["UserId"].S);
            Assert.AreEqual(userId, savedProject.Item["UserId"].S);
            Assert.AreEqual(acquisitionId, savedUser.Item["OriginalAcquisitionId"].S);
        }
        finally
        {
            using var client = store.CreateClient();
            if (!string.IsNullOrWhiteSpace(checkoutAttemptId)) await client.DeleteItemAsync("CheckoutAttempts", Key(checkoutAttemptId));
            if (!string.IsNullOrWhiteSpace(snapshotId)) await client.DeleteItemAsync("AttributionSnapshots", Key(snapshotId));
            if (!string.IsNullOrWhiteSpace(projectId)) await client.DeleteItemAsync("Projects", Key(projectId));
            if (!string.IsNullOrWhiteSpace(freshProjectId)) await client.DeleteItemAsync("Projects", Key(freshProjectId));
            if (!string.IsNullOrWhiteSpace(freshAcquisitionId)) await client.DeleteItemAsync("AttributionTouches", Key(freshAcquisitionId));
            Dictionary<string, AttributeValue>? cursor = null;
            do
            {
                var touches = await client.ScanAsync(new Amazon.DynamoDBv2.Model.ScanRequest
                {
                    TableName = "AttributionTouches",
                    FilterExpression = "FunnelId = :funnel",
                    ExpressionAttributeValues = new() { [":funnel"] = new() { S = funnelId } },
                    ExclusiveStartKey = cursor
                });
                foreach (var touchItem in touches.Items ?? [])
                    if (touchItem.TryGetValue("Id", out var touchId))
                        await client.DeleteItemAsync("AttributionTouches", Key(touchId.S));
                cursor = touches.LastEvaluatedKey;
            } while (cursor is { Count: > 0 });
            if (!string.IsNullOrWhiteSpace(freshFunnelId)) await client.DeleteItemAsync("Funnels", Key(freshFunnelId));
            await client.DeleteItemAsync("Funnels", Key(funnelId));
            await client.DeleteItemAsync("Users", Key(userId));
        }
    }

    private static Dictionary<string, AttributeValue> Key(string id) => new() { ["Id"] = new() { S = id } };
}
