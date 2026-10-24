using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Navigation;
using Microsoft.AspNetCore.WebUtilities;

namespace DesignMode.AI.Server.Services;

public sealed class PostAuthRoutingStore : IPostAuthRoutingStore
{
    private readonly Func<string, Task<CanonicalUserStore.CanonicalUser?>> _findUser;
    private readonly Func<IAmazonDynamoDB> _createClient;

    public PostAuthRoutingStore(CanonicalUserStore users)
        : this(username => users.FindByEmailAsync(username), () => DynamoDbClientFactory.CreateClient()) { }

    public PostAuthRoutingStore(
        Func<string, Task<CanonicalUserStore.CanonicalUser?>> findUser, Func<IAmazonDynamoDB> createClient)
    {
        _findUser = findUser;
        _createClient = createClient;
    }

    public async Task<PostAuthUser?> FindUserAsync(string username)
    {
        var user = await _findUser(username);
        if (user is null) return null;
        var currentPage = user.CurrentPage;
        if (PostAuthDestinationResolver.TryNormalizeResumeRoute(currentPage, out var route) &&
            Uri.UnescapeDataString(route.Split('?', '#')[0]).Contains("/init/gen", StringComparison.OrdinalIgnoreCase))
        {
            var queryIndex = route.IndexOf('?');
            if (queryIndex >= 0)
            {
                var query = QueryHelpers.ParseQuery(route[(queryIndex + 1)..].Split('#')[0]);
                query.TryGetValue("cid", out var cid);
                query.TryGetValue("projectId", out var projectId);
                var resourceId = cid.FirstOrDefault() ?? projectId.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(resourceId))
                {
                    // Preserve the existing GetUserResumePage ownership check.
                    using var client = _createClient();
                    var request = new ExecuteStatementRequest
                    {
                        Statement = "SELECT Id FROM CompanyIndustry WHERE Id = ? AND UserId = ?",
                        Parameters = [new() { S = resourceId }, new() { S = user.Id }]
                    };
                    bool owned;
                    do
                    {
                        var result = await client.ExecuteStatementAsync(request);
                        owned = result.Items.Any();
                        request.NextToken = result.NextToken;
                    } while (!owned && !string.IsNullOrEmpty(request.NextToken));
                    if (!owned) currentPage = null;
                }
            }
        }
        return new PostAuthUser(user.Id, currentPage);
    }

    public async Task<bool> HasPaidSubscriptionAsync(string userId)
    {
        using var client = _createClient();
        // Same canonical Subscriptions lookup as CheckUserSubscription, keyed only
        // by the Users.Id obtained above. Do not query Purchases or Stripe.
        var request = new ExecuteStatementRequest
        {
            Statement = "SELECT * FROM Subscriptions WHERE UserId = ?",
            Parameters = [new() { S = userId }]
        };
        do
        {
            var result = await client.ExecuteStatementAsync(request);
            if (result.Items.Any(HasPaidAccess)) return true;
            request.NextToken = result.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken));
        return false;
    }

    public async Task SaveCurrentPageAsync(string username, string currentPage)
    {
        var user = await _findUser(username) ?? throw new InvalidOperationException("The authenticated account was not found.");
        var update = CanonicalUserProgress.CreateUpdate(user, currentPage);
        using var client = _createClient();
        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = update.TableName, Key = update.Key,
            UpdateExpression = update.UpdateExpression, ConditionExpression = update.ConditionExpression,
            ExpressionAttributeNames = update.ExpressionAttributeNames,
            ExpressionAttributeValues = update.ExpressionAttributeValues
        });
    }

    public static bool HasPaidAccess(Dictionary<string, AttributeValue> subscription)
    {
        // These are the paid states persisted by the existing subscription/payment
        // writers. A scheduled cancellation remains active until its status changes.
        // The stored schema has no period end; do not manufacture one from TimeStamp.
        var status = subscription.GetValueOrDefault("SubscriptionStatus")?.S;
        return string.Equals(status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Paid", StringComparison.OrdinalIgnoreCase);
    }
}
