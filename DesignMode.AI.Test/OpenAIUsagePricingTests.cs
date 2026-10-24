using DesignMode.AI.Server.Services;
using System.Text.Json;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class OpenAIUsagePricingTests
{
    private readonly AIProviderPricingService _pricing = new();

    [TestMethod]
    public void Gpt56Pricing_CalculatesEstimatedCost()
    {
        var estimate = _pricing.EstimateOpenAiText("gpt-5.6", 1000, 200, 500);

        Assert.AreEqual(0.01328m, estimate);
    }

    [TestMethod]
    public void ChatCompletions_UsesPromptFieldsAndReturnedSnapshot()
    {
        using var json = JsonDocument.Parse("""{"model":"gpt-4.1-mini-2025-04-14","usage":{"prompt_tokens":1000,"prompt_tokens_details":{"cached_tokens":200},"completion_tokens":500}}""");
        var usage = OpenAIUsageParser.ParseChatCompletions(json.RootElement, "gpt-3.5-turbo");
        Assert.AreEqual("gpt-4.1-mini-2025-04-14", usage.Model);
        Assert.AreEqual(1000L, usage.InputTokens);
        Assert.AreEqual(200L, usage.CachedInputTokens);
        Assert.AreEqual(500L, usage.OutputTokens);
        Assert.AreEqual(0.00114m, _pricing.EstimateOpenAiText(usage.Model, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens));
    }

    [TestMethod]
    public void Responses_UsesInputFieldsAndNestedCache()
    {
        using var json = JsonDocument.Parse("""{"model":"gpt-4.1-mini","usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":200},"output_tokens":500}}""");
        var usage = OpenAIUsageParser.ParseResponses(json.RootElement, "gpt-4.1-mini");
        Assert.AreEqual(1000L, usage.InputTokens);
        Assert.AreEqual(200L, usage.CachedInputTokens);
        Assert.AreEqual(500L, usage.OutputTokens);
        Assert.AreEqual(0.00114m, _pricing.EstimateOpenAiText(usage.Model, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens));
        Assert.IsNull(OpenAIUsageParser.ParseChatCompletions(json.RootElement, usage.Model).InputTokens);
    }

    [TestMethod]
    public void MissingCache_DoesNotInventDiscount()
    {
        using var json = JsonDocument.Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":500}}""");
        var usage = OpenAIUsageParser.ParseChatCompletions(json.RootElement, "gpt-4.1-mini");
        Assert.IsNull(usage.CachedInputTokens);
        Assert.AreEqual(0.0012m, _pricing.EstimateOpenAiText(usage.Model, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens));
    }

    [TestMethod]
    public void ModelWithoutCacheRate_ChargesAllInputAtRegularRate()
    {
        Assert.AreEqual(0.00125m, _pricing.EstimateOpenAiText("gpt-3.5-turbo-0125", 1000, 200, 500));
    }

    [TestMethod]
    [DataRow("gpt-4.1-mini-2025-04-14", "gpt-4.1-mini")]
    [DataRow(" GPT-4.1-2025-04-14 ", "gpt-4.1")]
    [DataRow("gpt-3.5-turbo-0125", "gpt-3.5-turbo")]
    public void KnownSnapshot_NormalizesExplicitly(string model, string expected)
    {
        Assert.AreEqual(expected, AIProviderPricingService.NormalizeModel(model));
        Assert.IsNotNull(_pricing.EstimateOpenAiText(model, 1000, 0, 500));
    }

    [TestMethod]
    [DataRow("gpt-5")]
    [DataRow("gpt-4.1-mini-unknown")]
    [DataRow("gpt-4o-2024-05-13")]
    [DataRow("gpt-3.5-turbo-0613")]
    public void UnknownOrDifferentlyPricedSnapshot_LeavesCostNull(string model)
    {
        Assert.IsNull(_pricing.EstimateOpenAiText(model, 1000, 200, 500));
    }

    [TestMethod]
    public void MissingUsage_IsNotZeroCost()
    {
        using var json = JsonDocument.Parse("""{"usage":null}""");
        var usage = OpenAIUsageParser.ParseResponses(json.RootElement, "gpt-4.1-mini");
        Assert.IsNull(usage.InputTokens);
        Assert.IsNull(usage.OutputTokens);
        Assert.IsNull(_pricing.EstimateOpenAiText(usage.Model, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens));
        Assert.AreEqual(0m, _pricing.EstimateOpenAiText(usage.Model, 0, 0, 0));
        Assert.IsNull(_pricing.EstimateOpenAiText(usage.Model, 100, 200, 500));
    }
}
