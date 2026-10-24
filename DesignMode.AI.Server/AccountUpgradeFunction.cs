using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Stripe;

namespace DesignMode.AI.Server;

public sealed class AccountUpgradeFunction(JwtUserResolver jwt, CanonicalUserStore users,
    AttributionStore database, IConfiguration configuration, AICreditPurchaseOptions options)
{
    [Function("UpgradeAccountSubscription")]
    public async Task<IActionResult> Start([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "account/upgrade")] HttpRequest request)
    {
        var username = jwt.ResolveUsername(request);
        if (string.IsNullOrWhiteSpace(username)) return new UnauthorizedResult();
        var user = await users.FindByEmailAsync(username);
        if (user is null) return new UnauthorizedResult();
        AICreditCheckoutRequest? input;
        try { input = await request.ReadFromJsonAsync<AICreditCheckoutRequest>(); }
        catch (System.Text.Json.JsonException) { return new BadRequestResult(); }
        var plan = input?.PackageId;
        if (plan is not ("plus" or "extreme")) return new BadRequestResult();
        string? Read(string name) => configuration[name] ?? configuration["Values:" + name];
        var prefix = $"AI_UPGRADES_{(options.IsTest ? "TEST" : "LIVE")}";
        var priceId = Read(prefix + "_" + plan.ToUpperInvariant() + "_PRICE_ID");
        var portalConfig = Read(prefix + "_PORTAL_CONFIGURATION_ID");
        if (!options.Enabled || !string.Equals(Read("MEASUREMENT_LEDGER_ENABLED"), "true", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(priceId) || string.IsNullOrWhiteSpace(portalConfig))
            return new ObjectResult("Plan changes are not configured for this environment.") { StatusCode = 503 };
        try
        {
            var dashboard = BillingReturnUrl.Dashboard(request.Headers.Origin.FirstOrDefault(), options.IsTest, configuration);
            var stripe = new StripeClient(Read("StripeSecretKey"));
            var price = await new PriceService(stripe).GetAsync(priceId);
            if (!price.Active || price.Livemode == options.IsTest || price.Currency != "usd" ||
                price.UnitAmount != (plan == "plus" ? 7900 : 12900) || price.Recurring?.Interval != "month" || price.Recurring.IntervalCount != 1)
                throw new InvalidOperationException("Upgrade price configuration requires review.");
            using var db = database.CreateClient();
            var query = new ExecuteStatementRequest
            {
                Statement = "SELECT * FROM Subscriptions WHERE UserId = ?", ConsistentRead = true,
                Parameters = [new() { S = user.Id }]
            };
            var ids = new HashSet<string>();
            do
            {
                var page = await db.ExecuteStatementAsync(query);
                foreach (var row in page.Items)
                    if (row.GetValueOrDefault("SubscriptionId")?.S is { } id && id.StartsWith("sub_")) ids.Add(id);
                query.NextToken = page.NextToken;
            } while (!string.IsNullOrEmpty(query.NextToken));
            var subscriptions = new List<Subscription>();
            foreach (var id in ids)
            {
                var subscription = await new SubscriptionService(stripe).GetAsync(id);
                if (subscription.Status is not ("canceled" or "incomplete_expired")) subscriptions.Add(subscription);
            }
            // Existing subscribers must confirm an update to their current subscription.
            // Never route an account without a uniquely verified subscription to a new recurring purchase.
            if (subscriptions.Count == 0)
                return new ConflictObjectResult("No current recurring subscription was found for your account. Please contact contact@designmode.ai to set up your plan.");
            if (subscriptions.Count > 1)
                return new ConflictObjectResult("Your account has multiple current subscriptions. Please contact contact@designmode.ai to choose which subscription to upgrade.");
            var current = subscriptions.Single();
            if (string.IsNullOrWhiteSpace(current.Metadata.GetValueOrDefault("checkoutAttemptId")))
                return new ConflictObjectResult("This older subscription needs help from contact@designmode.ai before changing plans.");
            if (!IsEligible(current, price, user.Id, options.IsTest))
                return new ConflictObjectResult("Please resolve your current subscription status before changing plans.");
            var item = current.Items.Data.Single();
            var prior = new List<PurchaseRecord>();
            Dictionary<string, AttributeValue>? cursor = null;
            do
            {
                var page = await db.ScanAsync(new ScanRequest
                {
                    TableName = "Purchases", ConsistentRead = true, ExclusiveStartKey = cursor,
                    FilterExpression = "UserId = :user", ExpressionAttributeValues = new() { [":user"] = new() { S = user.Id } }
                });
                prior.AddRange(page.Items.Where(x => x.ContainsKey("Data")).Select(x => System.Text.Json.JsonSerializer.Deserialize<PurchaseRecord>(x["Data"].S)!)
                    .Where(x => x.StripeSubscriptionId == current.Id));
                cursor = page.LastEvaluatedKey;
            } while (cursor is { Count: > 0 });
            // Confirm a paid billing period can be preserved before exposing a chargeable upgrade.
            _ = PurchaseMeasurementStore.UpgradePeriodStart(prior, DateTimeOffset.UtcNow);
            var session = await new Stripe.BillingPortal.SessionService(stripe).CreateAsync(new()
            {
                Customer = current.CustomerId, Configuration = portalConfig, ReturnUrl = dashboard,
                FlowData = new()
                {
                    Type = "subscription_update_confirm",
                    SubscriptionUpdateConfirm = new()
                    {
                        Subscription = current.Id, Items = [new() { Id = item.Id, Price = price.Id, Quantity = 1 }]
                    },
                    AfterCompletion = new() { Type = "redirect", Redirect = new() { ReturnUrl = dashboard } }
                }
            });
            return new OkObjectResult(new AICreditCheckoutResponse("", session.Url));
        }
        catch (Exception ex) when (ex is StripeException or InvalidOperationException)
        {
            return new ObjectResult("We couldn't open the plan change confirmation. Please try again.") { StatusCode = 503 };
        }
    }
    public static bool IsEligible(Subscription current, Price price, string userId, bool test)
    {
        var item = current.Items?.Data is { Count: 1 } items ? items[0] : null;
        return current.Metadata?.GetValueOrDefault("userId") == userId && current.Livemode != test &&
            current.Status == "active" && !current.CancelAtPeriodEnd && !current.CancelAt.HasValue &&
            item?.Quantity == 1 && item.Price.Recurring?.Interval == "month" && item.Price.Recurring.IntervalCount == 1 &&
            item.Price.Currency == "usd" && item.Price.UnitAmount.HasValue && item.Price.UnitAmount < price.UnitAmount;
    }
}
