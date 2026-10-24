using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server;

public sealed class AccountFunction(JwtUserResolver jwt, AccountSubscriptionService accounts, ILogger<AccountFunction> logger)
{
    [Function("AccountSummary")]
    public Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "account")] HttpRequest request)
        => ExecuteAsync(request, false);

    [Function("CancelAccountSubscription")]
    public Task<IActionResult> Cancel([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "account/cancel")] HttpRequest request)
        => ExecuteAsync(request, true);

    private async Task<IActionResult> ExecuteAsync(HttpRequest request, bool cancel)
    {
        var username = jwt.ResolveUsername(request);
        if (string.IsNullOrWhiteSpace(username)) return new UnauthorizedResult();
        try
        {
            var account = await accounts.GetAsync(username, cancel);
            return account is null ? new UnauthorizedResult() : new OkObjectResult(account);
        }
        catch (MultipleAccountSubscriptionsException)
        {
            return new ConflictObjectResult("Your account has multiple subscriptions. Please contact contact@designmode.ai for help managing them.");
        }
        catch (NoCancellableSubscriptionException)
        {
            return new ConflictObjectResult("There is no current subscription available to cancel. Please refresh your account.");
        }
        catch
        {
            logger.LogWarning("[ACCOUNT] subscription request failed");
            return new ObjectResult("We couldn't update or load your subscription. Please retry.")
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }
}
