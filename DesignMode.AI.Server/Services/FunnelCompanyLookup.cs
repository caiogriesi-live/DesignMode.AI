using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace DesignMode.AI.Server.Services;

public static class FunnelCompanyLookup
{
    public static async Task<List<Dictionary<string, AttributeValue>>> FindAsync(
        IAmazonDynamoDB client, string local, string? companyId, string? name, string? industry)
    {
        if (string.IsNullOrWhiteSpace(local) && string.IsNullOrWhiteSpace(companyId)) return [];
        var request = new ExecuteStatementRequest
        {
            Statement = "SELECT * FROM \"CompanyIndustry\" WHERE \"Local\" = ?",
            Parameters = [new() { S = local }],
            ConsistentRead = true
        };
        if (!string.IsNullOrWhiteSpace(companyId))
        {
            request.Statement = "SELECT * FROM \"CompanyIndustry\" WHERE \"Id\" = ?";
            request.Parameters = [new() { S = companyId }];
        }
        else if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(industry))
        {
            // Compatibility for an open funnel created before its URL carried an Id.
            request.Statement += " AND \"Name\" = ? AND \"Industry\" = ?";
            request.Parameters.Add(new() { S = name });
            request.Parameters.Add(new() { S = industry });
        }

        var matches = new List<Dictionary<string, AttributeValue>>();
        do
        {
            var response = await client.ExecuteStatementAsync(request);
            matches.AddRange(response.Items ?? []);
            request.NextToken = response.NextToken;
        } while (matches.Count < 2 && !string.IsNullOrEmpty(request.NextToken));
        // The caller rejects ambiguity; never choose the first/latest project.
        return matches;
    }

    public static bool CanClaim(Dictionary<string, AttributeValue> company, string local, string userId, string username)
    {
        var owner = company.GetValueOrDefault("UserId")?.S;
        if (owner == userId || (!string.IsNullOrWhiteSpace(owner) &&
            CanonicalUserStore.NormalizeEmail(owner) == CanonicalUserStore.NormalizeEmail(username))) return true;
        // Only the original browser may claim an anonymous project. A project ID alone is not ownership.
        return !string.IsNullOrWhiteSpace(local) && company.GetValueOrDefault("Local")?.S == local && owner == local;
    }
}