using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class AIGenerationUsageLocalIntegrationTests
{
    [TestMethod]
    [TestCategory("LocalIntegration")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OpenAiUsage_PersistsParsedCostAndRuntimeModel(bool responsesApi)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();
        var clients = new AttributionStore(NullLogger<AttributionStore>.Instance);
        var store = new AIGenerationUsageStore(clients, NullLogger<AIGenerationUsageStore>.Instance);
        await EnsureTableAsync(clients);
        using var json = System.Text.Json.JsonDocument.Parse(responsesApi
            ? """{"model":"gpt-4.1-mini-2025-04-14","usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":200},"output_tokens":500}}"""
            : """{"model":"gpt-4.1-mini-2025-04-14","usage":{"prompt_tokens":1000,"prompt_tokens_details":{"cached_tokens":200},"completion_tokens":500}}""");
        var usage = responsesApi ? OpenAIUsageParser.ParseResponses(json.RootElement, "gpt-4.1-mini")
            : OpenAIUsageParser.ParseChatCompletions(json.RootElement, "gpt-4.1-mini");
        var id = await store.CreateStartedAsync(new AIGenerationUsage
        {
            GenerationJobId = Guid.NewGuid().ToString(), Provider = "OpenAI", Model = "gpt-4.1-mini",
            Operation = "TextGeneration", PricingVersion = AIProviderPricingService.CurrentVersion
        });
        using var client = clients.CreateClient();
        var key = new Dictionary<string, AttributeValue> { ["Id"] = new() { S = id } };
        try
        {
            await store.CompleteOpenAiSuccessAsync(id, usage, new AIProviderPricingService(), "TextGeneration", "test-request-id");
            var item = (await client.GetItemAsync(new GetItemRequest { TableName = "AIGenerationUsage", Key = key, ConsistentRead = true })).Item;
            Assert.AreEqual(0.00114m, decimal.Parse(item["EstimatedCost"].N, System.Globalization.CultureInfo.InvariantCulture));
            Assert.AreEqual("1000", item["InputTokens"].N);
            Assert.AreEqual("200", item["CachedInputTokens"].N);
            Assert.AreEqual("500", item["OutputTokens"].N);
            Assert.AreEqual(usage.Model, item["Model"].S);
            Assert.AreEqual(AIProviderPricingService.CurrentVersion, item["PricingVersion"].S);
            Assert.AreEqual("Succeeded", item["Status"].S);
            Assert.AreEqual("USD", item["Currency"].S);
            Assert.AreEqual("test-request-id", item["ProviderRequestId"].S);
            Assert.IsFalse(item.ContainsKey("ActualCost"));
            await store.CompleteOpenAiSuccessAsync(id, usage with { Model = "unknown-model" }, new AIProviderPricingService(), "TextGeneration", null);
            item = (await client.GetItemAsync(new GetItemRequest { TableName = "AIGenerationUsage", Key = key, ConsistentRead = true })).Item;
            Assert.IsFalse(item.ContainsKey("EstimatedCost"));
            Assert.AreEqual("1000", item["InputTokens"].N);
        }
        finally { await client.DeleteItemAsync("AIGenerationUsage", key); }
    }

    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task UsageLedger_TracksLifecycleCostsAggregatesAndIdempotency()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();

        var clients = new AttributionStore(NullLogger<AttributionStore>.Instance);
        var store = new AIGenerationUsageStore(clients, NullLogger<AIGenerationUsageStore>.Instance);
        await EnsureTableAsync(clients);
        var user = Guid.NewGuid().ToString(); var project = Guid.NewGuid().ToString(); var funnel = Guid.NewGuid().ToString(); var job = Guid.NewGuid().ToString();
        var ids = new List<string>();
        try
        {
            var image = new AIGenerationUsage { UserId = user, ProjectId = project, FunnelId = funnel, GenerationJobId = job, Provider = "Runware", Model = "runware:101@1", Operation = "ImageGeneration", ProviderTaskId = job, ImageCount = 1, ImageWidth = 1344, ImageHeight = 768 };
            ids.Add(await store.CreateStartedAsync(image));
            Assert.AreEqual(ids[0], await store.CreateStartedAsync(image));
            await store.CompleteSuccessAsync(ids[0], actualCost: 0.0123m);

            var text = new AIGenerationUsage { UserId = user, ProjectId = project, FunnelId = funnel, GenerationJobId = job, Provider = "OpenAI", Model = "gpt-4o-mini", Operation = "TextGeneration", ProviderTaskId = Guid.NewGuid().ToString(), PricingVersion = AIProviderPricingService.CurrentVersion };
            ids.Add(await store.CreateStartedAsync(text));
            var estimated = new AIProviderPricingService().EstimateOpenAiText(text.Model, 1000, 200, 500);
            Assert.IsNotNull(estimated);
            await store.CompleteSuccessAsync(ids[1], estimatedCost: estimated, inputTokens: 1000, cachedInputTokens: 200, outputTokens: 500);

            var failed = new AIGenerationUsage { UserId = user, ProjectId = project, FunnelId = funnel, GenerationJobId = job, Provider = "Runware", Model = "bytedance:2@2", Operation = "VideoGeneration", ProviderTaskId = Guid.NewGuid().ToString(), VideoSeconds = 5 };
            ids.Add(await store.CreateStartedAsync(failed));
            await store.CompleteFailureAsync(ids[2], "ProviderError", actualCost: 0.001m);

            foreach (var aggregate in new[] { await store.AggregateByProjectAsync(project), await store.AggregateByFunnelAsync(funnel), await store.AggregateByUserAsync(user), await store.AggregateByGenerationJobAsync(job) })
            { Assert.AreEqual(0.0133m, aggregate.TotalActualCost); Assert.AreEqual(estimated, aggregate.TotalEstimatedCost); Assert.AreEqual(2, aggregate.SucceededCount); Assert.AreEqual(1, aggregate.FailedCount); }

            using var client = clients.CreateClient();
            var rows = await client.ScanAsync(new ScanRequest { TableName = "AIGenerationUsage", FilterExpression = "GenerationJobId = :job", ExpressionAttributeValues = new() { [":job"] = new() { S = job } } });
            Assert.AreEqual(3, rows.Count);
            Assert.IsFalse(rows.Items.Any(x => x.ContainsKey("Email") || x.ContainsKey("Prompt") || x.ContainsKey("ImageUrl") || x.ContainsKey("Jwt")));
        }
        finally { using var client = clients.CreateClient(); foreach (var id in ids.Distinct()) await client.DeleteItemAsync("AIGenerationUsage", new() { ["Id"] = new() { S = id } }); }
    }

    [TestMethod]
    [TestCategory("LocalIntegration")]
    public async Task FractionalGenerationUnits_PersistAsDecimalNumber()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("Set RUN_LOCAL_DYNAMODB_INTEGRATION_TESTS=true to run against DynamoDB Local.");
        LocalDynamoDbTestSafety.RequireLoopback();

        var clients = new AttributionStore(NullLogger<AttributionStore>.Instance);
        var store = new AIGenerationUsageStore(clients, NullLogger<AIGenerationUsageStore>.Instance);
        await EnsureTableAsync(clients);
        var id = await store.CreateStartedAsync(new AIGenerationUsage
        {
            GenerationJobId = Guid.NewGuid().ToString(),
            Provider = "Runware",
            Model = "runware:101@1",
            Operation = "ImageGeneration",
            ProviderTaskId = Guid.NewGuid().ToString(),
            GenerationUnits = 0.1m
        });

        using var client = clients.CreateClient();
        try
        {
            var item = (await client.GetItemAsync(new GetItemRequest { TableName = "AIGenerationUsage", Key = new Dictionary<string, AttributeValue> { ["Id"] = new() { S = id } }, ConsistentRead = true })).Item;
            Assert.AreEqual("0.1", item["GenerationUnits"].N);
        }
        finally
        {
            await client.DeleteItemAsync("AIGenerationUsage", new Dictionary<string, AttributeValue> { ["Id"] = new() { S = id } });
        }
    }

    private static async Task EnsureTableAsync(AttributionStore clients)
    {
        using var client = clients.CreateClient();
        try { await client.DescribeTableAsync("AIGenerationUsage"); }
        catch (ResourceNotFoundException) { await client.CreateTableAsync(new CreateTableRequest { TableName = "AIGenerationUsage", BillingMode = BillingMode.PAY_PER_REQUEST, KeySchema = [new("Id", KeyType.HASH)], AttributeDefinitions = [new("Id", ScalarAttributeType.S)] }); }
    }
}
