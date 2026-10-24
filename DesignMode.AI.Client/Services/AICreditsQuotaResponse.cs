using System.Net;
using System.Text.Json;

namespace DesignMode.AI.Client.Services;

public static class AICreditsQuotaResponse
{
    public static async Task<bool> IsExhaustedAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return false;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == "ai_generation_limit_exhausted";
        }
        catch (JsonException) { return false; }
    }
}
