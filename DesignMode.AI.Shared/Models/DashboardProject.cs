using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace DesignMode.AI.Shared.Models;

public sealed class DashboardProject
{
    public string CompanyIdeaId { get; set; } = string.Empty;
    public string Name { get; set; } = "Project";
    public string? Industry { get; set; }
    public string? ThumbnailUrl { get; set; }

    [JsonIgnore]
    public string Link => $"/init/gen?s=1&cid={Uri.EscapeDataString(CompanyIdeaId)}";

    // Preserve the stored key: DynamoDB string keys and legacy base64 CIDs are case-sensitive.
    public static DashboardProject? FromCompanyIdeaId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty)
            return new() { CompanyIdeaId = value };

        var parts = value.Split("|~~|@@|^^|", StringSplitOptions.None);
        if (parts.Length != 3) return null;
        // Existing projects use either a UTC timestamp or a GUID as the unique suffix.
        // Keep the entire stored CID intact: it is the key used to reopen the project.
        var timestampSuffix = DateTime.TryParseExact(parts[2], "yyyyMMddTHHmmssZ",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        var guidSuffix = (Guid.TryParseExact(parts[2], "N", out var suffixId)
            || Guid.TryParseExact(parts[2], "D", out suffixId)) && suffixId != Guid.Empty;
        if (!timestampSuffix && !guidSuffix) return null;
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            var name = utf8.GetString(Convert.FromBase64String(parts[0]));
            var industry = utf8.GetString(Convert.FromBase64String(parts[1]));
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(industry) ||
                name.Any(char.IsControl) || industry.Any(char.IsControl)) return null;
            return new() { CompanyIdeaId = value, Name = name, Industry = industry };
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }
}
