using System.Text.Json;

namespace DesignMode.AI.Server.Services;

public sealed record OpenAIUsage(string Model, long? InputTokens, long? CachedInputTokens, long? OutputTokens);

public static class OpenAIUsageParser
{
    public static OpenAIUsage ParseResponse(JsonElement response, string requestedModel) =>
        response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty("prompt_tokens", out _)
            ? ParseChatCompletions(response, requestedModel)
            : ParseResponses(response, requestedModel);

    public static OpenAIUsage ParseChatCompletions(JsonElement response, string requestedModel) =>
        Parse(response, requestedModel, "prompt_tokens", "prompt_tokens_details", "completion_tokens");

    public static OpenAIUsage ParseResponses(JsonElement response, string requestedModel) =>
        Parse(response, requestedModel, "input_tokens", "input_tokens_details", "output_tokens");

    private static OpenAIUsage Parse(JsonElement response, string requestedModel, string input, string details, string output)
    {
        var model = response.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(m.GetString()) ? m.GetString()! : requestedModel;
        var usage = response.TryGetProperty("usage", out var u) ? u : default;
        var tokenDetails = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(details, out var d) ? d : default;
        return new(model, Read(usage, input), Read(tokenDetails, "cached_tokens"), Read(usage, output));
    }

    private static long? Read(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;
}
