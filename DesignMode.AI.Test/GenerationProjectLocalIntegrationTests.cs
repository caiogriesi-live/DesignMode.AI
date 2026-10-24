using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class GenerationProjectLocalIntegrationTests
{
    [TestMethod, TestCategory("LocalIntegration")]
    public async Task ExistingSubscriptionLinkedWorkspace_ResolvesOriginalAttribution_ReadOnly()
    {
        var projectId = Environment.GetEnvironmentVariable("VERIFY_GENERATION_PROJECT_ID");
        if (Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS") != "true" ||
            string.IsNullOrWhiteSpace(projectId))
            Assert.Inconclusive("Set local integration opt-in and VERIFY_GENERATION_PROJECT_ID for a read-only check.");
        LocalDynamoDbTestSafety.RequireLoopback();
        var store = new AttributionStore(NullLogger<AttributionStore>.Instance);
        using var db = store.CreateClient();
        var project = (await db.GetItemAsync(new GetItemRequest
        { TableName = "Projects", Key = new() { ["Id"] = new() { S = projectId } }, ConsistentRead = true })).Item;
        Assert.IsTrue(project is { Count: > 0 });
        var userId = project["UserId"].S;
        var subscriptionRequest = new ExecuteStatementRequest
        {
            Statement = "SELECT InviteId FROM Subscriptions WHERE ProjectId = ? AND UserId = ?",
            Parameters = [new() { S = projectId }, new() { S = userId }], ConsistentRead = true
        };
        var invites = new HashSet<string>();
        do
        {
            var page = await db.ExecuteStatementAsync(subscriptionRequest);
            foreach (var row in page.Items)
                if (row.GetValueOrDefault("InviteId")?.S is { Length: > 0 } invite) invites.Add(invite);
            subscriptionRequest.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(subscriptionRequest.NextToken));
        var verified = 0;
        foreach (var invite in invites)
        {
            var promptRequest = new ExecuteStatementRequest
            {
                Statement = "SELECT CompanyIdeaId FROM Prompt WHERE InviteId = ?",
                Parameters = [new() { S = invite }], ConsistentRead = true
            };
            do
            {
                var page = await db.ExecuteStatementAsync(promptRequest);
                foreach (var prompt in page.Items)
                {
                    var cid = prompt.GetValueOrDefault("CompanyIdeaId")?.S;
                    if (string.IsNullOrWhiteSpace(cid)) continue;
                    var context = await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(db, userId, cid);
                    Assert.IsNotNull(context);
                    Assert.AreEqual(projectId, context.ProjectId);
                    Assert.AreEqual(project.GetValueOrDefault("OriginatingFunnelId")?.S ?? "", context.FunnelId);
                    Assert.AreEqual(project.GetValueOrDefault("AcquisitionId")?.S, context.AcquisitionId);
                    verified++;
                }
                promptRequest.NextToken = page.NextToken;
            } while (!string.IsNullOrEmpty(promptRequest.NextToken));
        }
        Assert.IsTrue(verified > 0, "No persisted subscription/prompt link was tested.");
    }
}
