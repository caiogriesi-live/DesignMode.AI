using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class ProviderCostPersistenceTests
{
    [DataTestMethod]
    [DataRow("{\"data\":[{\"cost\":0.00123456789},{\"cost\":\"0.00000000001\"}]}", "0.00123456790")]
    [DataRow("{\"data\":[{\"cost\":0}]}", "0")]
    [DataRow("{\"data\":[{}]}", null)]
    [DataRow("{\"data\":[{\"cost\":0.01},{}]}", null)]
    [DataRow("{\"data\":[]}", null)]
    [DataRow("{\"data\":[null]}", null)]
    [DataRow("{\"data\":[{\"cost\":-1}]}", null)]
    public void RunwareCost_PreservesDecimalAndDistinguishesUnknownFromZero(string response, string? expected)
    {
        Assert.AreEqual(expected is null ? (decimal?)null : decimal.Parse(expected, CultureInfo.InvariantCulture), RunwareUsageParser.ImageCost(response));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OpenAi_CostAndTokensPersistAcrossProjects_WithTransientWriteRetry(bool chat)
    {
        using var db = new GenerationProjectAuthorizationTests.Database();
        var clients = new AttributionStore(NullLogger<AttributionStore>.Instance, clients: () => db);
        var store = new AIGenerationUsageStore(clients, NullLogger<AIGenerationUsageStore>.Instance);
        var recorder = new AIUsageRecorder(store, new AIProviderPricingService(), NullLogger<AIUsageRecorder>.Instance);
        var response = chat
            ? """{"model":"gpt-4.1-mini","usage":{"prompt_tokens":1000,"prompt_tokens_details":{"cached_tokens":200},"completion_tokens":500}}"""
            : """{"model":"gpt-4.1-mini","usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":200},"output_tokens":500}}""";
        foreach (var project in new[] { "project-a", "project-b", "project-c" })
        {
            var id = await recorder.StartAsync(new AIGenerationUsage
            {
                UserId = GenerationProjectAuthorizationTests.User, ProjectId = project,
                GenerationJobId = project, ProviderTaskId = project, Provider = "OpenAI",
                Operation = "TextGeneration", UsageScope = "Paid", GenerationUnits = 0
            });
            db.TransientUsageUpdateFailures = 2;
            await recorder.CompleteOpenAiAsync(id, response, "gpt-4.1-mini", "TextGeneration", "request-" + project);
            var row = db.UsageRows.Single(item => item["Id"].S == id);
            Assert.AreEqual(project, row["ProjectId"].S);
            Assert.AreEqual("Succeeded", row["Status"].S);
            Assert.AreEqual(0.00114m, decimal.Parse(row["EstimatedCost"].N, CultureInfo.InvariantCulture));
            Assert.AreEqual("1000", row["InputTokens"].N);
            Assert.AreEqual("200", row["CachedInputTokens"].N);
            Assert.AreEqual("500", row["OutputTokens"].N);
            Assert.AreEqual("USD", row["Currency"].S);
            Assert.AreEqual("request-" + project, row["ProviderRequestId"].S);
            Assert.AreEqual(AIProviderPricingService.CurrentVersion, row["PricingVersion"].S);
            Assert.IsFalse(row.ContainsKey("ActualCost"));
        }
        Assert.AreEqual(3, db.UsageRows.Count);
        Assert.AreEqual(3, db.UsageCompletions);
    }
}
