using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Navigation;

namespace DesignMode.AI.Server.Services;

public static class CanonicalUserProgress
{
    public static Update CreateUpdate(CanonicalUserStore.CanonicalUser user, string currentPage)
    {
        if (!PostAuthDestinationResolver.TryNormalizeLocalRoute(currentPage, out var route))
            throw new ArgumentException("A safe local current page is required.", nameof(currentPage));
        return new Update
        {
            TableName = "Users",
            Key = new() { ["Id"] = new() { S = user.Id } },
            UpdateExpression = "SET #page = :page, #updated = :updated",
            ConditionExpression = "attribute_exists(Id) AND #username = :username",
            ExpressionAttributeNames = new()
            {
                ["#page"] = "CurrentPage", ["#updated"] = "UpdatedAt", ["#username"] = "Username"
            },
            ExpressionAttributeValues = new()
            {
                [":page"] = new() { S = route },
                [":updated"] = new() { S = DateTimeOffset.UtcNow.ToString("O") },
                [":username"] = new() { S = user.Username }
            }
        };
    }

    public static bool TryNormalizePage(string value, string trustedOrigin, out string route)
    {
        // Compatibility for existing funnel callers sending NavigationManager.Uri.
        // Never strip the host from an arbitrary external URL.
        if (!value.StartsWith('/') &&
            Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            Uri.TryCreate(trustedOrigin, UriKind.Absolute, out var origin) &&
            absolute.Scheme == origin.Scheme && absolute.Authority == origin.Authority &&
            string.IsNullOrEmpty(absolute.UserInfo))
            value = absolute.PathAndQuery + absolute.Fragment;
        return PostAuthDestinationResolver.TryNormalizeLocalRoute(value, out route);
    }
}
