using System.Globalization;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

public static class RunwareUsageParser
{
    // Read money from the provider JSON as decimal. Missing costs are unknown,
    // not zero; do not report a partial batch total as the full operation cost.
    public static decimal? ImageCost(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        decimal total = 0;
        var count = 0;
        foreach (var image in data.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("cost", out var cost)) return null;
            decimal value;
            if (cost.ValueKind == JsonValueKind.Number)
            {
                if (!cost.TryGetDecimal(out value)) return null;
            }
            else if (cost.ValueKind != JsonValueKind.String ||
                !decimal.TryParse(cost.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return null;
            if (value < 0) return null;
            total += value;
            count++;
        }
        return count > 0 ? total : null;
    }
}
