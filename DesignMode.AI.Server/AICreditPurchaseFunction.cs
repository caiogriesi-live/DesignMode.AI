using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace DesignMode.AI.Server;

public sealed class AICreditPurchaseFunction(JwtUserResolver jwt, AttributionStore users, AICreditPurchaseOptions options,
    AICreditLedger ledger, AIUsageSummaryService summaries, StripeCreditPurchaseHandler stripe, Microsoft.Extensions.Configuration.IConfiguration configuration)
{
    private async Task<string?> UserAsync(HttpRequest request)
    {
        var claim = jwt.ResolveUserId(request);
        return string.IsNullOrWhiteSpace(claim) ? null : await users.ResolveClaimedUserIdAsync(claim);
    }

    [Function("StartAICreditPurchase")]
    public async Task<IActionResult> Start([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ai-credits/checkout")] HttpRequest request)
    {
        var user = await UserAsync(request);
        if (user is null) return new UnauthorizedResult();
        AICreditCheckoutRequest? input;
        try { input = await request.ReadFromJsonAsync<AICreditCheckoutRequest>(); }
        catch (System.Text.Json.JsonException) { return new BadRequestResult(); }
        var pack = options.Packages.SingleOrDefault(x => x.Id == input?.PackageId);
        if (pack is null) return new BadRequestResult();
        if (!options.IsConfigured(pack)) return new ObjectResult(new { code = "credit_purchases_unavailable" }) { StatusCode = 503 };
        string dashboard;
        try { dashboard = BillingReturnUrl.Dashboard(request.Headers.Origin.FirstOrDefault(), options.IsTest, configuration); }
        catch (InvalidOperationException) { return new BadRequestObjectResult(new { code = "invalid_return_origin" }); }
        try { await stripe.ValidateCheckoutAsync(pack); }
        catch (Exception ex) when (ex is Stripe.StripeException or InvalidOperationException)
        {
            return new ObjectResult(new { code = "credit_configuration_requires_review" }) { StatusCode = 503 };
        }
        var attempt = await ledger.CreateAttemptAsync(user, pack, options.IsTest);
        var url = await stripe.CreateCheckoutAsync(pack, attempt.Id, dashboard);
        return new OkObjectResult(new AICreditCheckoutResponse(attempt.Id, url));
    }

    [Function("GetAICreditPurchaseStatus")]
    public async Task<IActionResult> Status([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ai-credits/purchases/{id}")] HttpRequest request, string id)
    {
        var user = await UserAsync(request);
        if (user is null) return new UnauthorizedResult();
        var attempt = await ledger.GetAttemptAsync(id);
        if (attempt is null || attempt.UserId != user) return new NotFoundResult();
        var purchase = attempt.PurchaseId is null ? null : await ledger.GetPurchaseAsync(attempt.PurchaseId);
        var summary = await summaries.GetAsync(user);
        return new OkObjectResult(new AICreditPurchaseStatus(purchase is not null,
            purchase is null ? 0 : Math.Max(0, purchase.CreditsGranted - purchase.CreditsRevoked), summary?.GenerationsRemaining));
    }
}
