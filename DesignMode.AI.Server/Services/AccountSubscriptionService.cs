using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models;
using Stripe;

namespace DesignMode.AI.Server.Services;

public sealed class AccountSubscriptionService
{
    private readonly Func<string, Task<CanonicalUserStore.CanonicalUser?>> _findUser;
    private readonly Func<IAmazonDynamoDB> _createDatabase;
    private readonly Func<SubscriptionService> _createStripe;

    public AccountSubscriptionService(CanonicalUserStore users)
        : this(username => users.FindByEmailAsync(username), () => DynamoDbClientFactory.CreateClient(),
            () => new SubscriptionService(new StripeClient(Environment.GetEnvironmentVariable("StripeSecretKey")))) { }

    public AccountSubscriptionService(Func<string, Task<CanonicalUserStore.CanonicalUser?>> findUser,
        Func<IAmazonDynamoDB> createDatabase, Func<SubscriptionService> createStripe)
    {
        _findUser = findUser;
        _createDatabase = createDatabase;
        _createStripe = createStripe;
    }

    public async Task<AccountSummary?> GetAsync(string username, bool cancel = false)
    {
        var user = await _findUser(username);
        if (user is null) return null;
        using var database = _createDatabase();
        var query = new ExecuteStatementRequest
        {
            Statement = "SELECT * FROM Subscriptions WHERE UserId = ?",
            ConsistentRead = true,
            Parameters = [new() { S = user.Id }]
        };
        var rows = new List<Dictionary<string, AttributeValue>>();
        do
        {
            var page = await database.ExecuteStatementAsync(query);
            rows.AddRange(page.Items ?? []);
            query.NextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(query.NextToken));
        var signupCount = CountSignups(rows);

        // Stripe is authoritative for current billing state, including cancellations
        // scheduled in another session. Never trust a subscription ID from a request.
        var subscriptions = new List<(string Plan, Subscription Subscription)>();
        SubscriptionService? stripe = null;
        foreach (var group in rows.Where(row => row.GetValueOrDefault("SubscriptionId")?.S?.StartsWith("sub_", StringComparison.Ordinal) == true)
            .GroupBy(row => row["SubscriptionId"].S))
        {
            stripe ??= _createStripe();
            var subscription = await stripe.GetAsync(group.Key);
            if (subscription.Status is "canceled" or "incomplete_expired") continue;
            var plan = group.First().GetValueOrDefault("Name")?.S;
            subscriptions.Add((string.IsNullOrWhiteSpace(plan) ? "Subscription" : plan, subscription));
        }
        // Do not arbitrarily cancel one of several subscriptions on a legacy account.
        if (subscriptions.Count > 1) throw new MultipleAccountSubscriptionsException();
        if (subscriptions.Count == 0)
        {
            if (cancel) throw new NoCancellableSubscriptionException();
            return new(user.Username, "None", "None", false, false, signupCount);
        }
        var current = subscriptions.Single();
        var live = current.Subscription;
        var scheduled = live.CancelAtPeriodEnd || live.CancelAt.HasValue;
        var cancellable = live.Status is "active" or "trialing" or "past_due" or "unpaid";
        if (cancel && !scheduled)
        {
            if (!cancellable) throw new NoCancellableSubscriptionException();
            live = await stripe!.UpdateAsync(live.Id, new SubscriptionUpdateOptions { CancelAtPeriodEnd = true });
            scheduled = live.CancelAtPeriodEnd || live.CancelAt.HasValue;
            if (!scheduled) throw new InvalidOperationException("Cancellation was not confirmed.");
        }
        // No Subscriptions writes: scheduling cancellation must not revoke paid access.
        var currentPlan = StripeVerifiedPaymentResolver.ConfiguredPricePlan(live.Items?.Data.FirstOrDefault()?.Price?.Id, live.Livemode);
        return new(user.Username, currentPlan is null ? current.Plan : currentPlan == "extreme" ? "Extreme" : "Plus", live.Status, cancellable && !scheduled, scheduled, signupCount);
    }

    // Subscription events append historical rows. Count distinct canonical identities
    // with evidence of activation; retries/renewals and later cancellation add nothing.
    public static int CountSignups(IEnumerable<Dictionary<string, AttributeValue>> rows) => rows
        .Where(row => row.GetValueOrDefault("SubscriptionId")?.S is { } id &&
            ((id.StartsWith("sub_", StringComparison.Ordinal) && row.GetValueOrDefault("SubscriptionStatus")?.S is "active" or "trialing") ||
             (id.StartsWith("pi_", StringComparison.Ordinal) && row.GetValueOrDefault("Name")?.S == "Starter" && row.GetValueOrDefault("SubscriptionStatus")?.S == "Paid")))
        .Select(row => row["SubscriptionId"].S).Distinct(StringComparer.Ordinal).Count();
}

public sealed class MultipleAccountSubscriptionsException : Exception { }
public sealed class NoCancellableSubscriptionException : Exception { }
