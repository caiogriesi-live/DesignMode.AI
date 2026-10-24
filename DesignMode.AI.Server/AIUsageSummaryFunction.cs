using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Server;

public sealed class AIUsageSummaryFunction(JwtUserResolver jwt, AttributionStore attribution, AIUsageSummaryService summaries,
    IConfiguration configuration, AIUsageQuotaOptions quotas)
{
    [Function("GetAIUsageSummary")]
    public async Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Function, "get", Route = "ai-usage-summary")] HttpRequest request)
    {
        var claimed = jwt.ResolveUserId(request);
        var userId = string.IsNullOrWhiteSpace(claimed) ? null : await attribution.ResolveClaimedUserIdAsync(claimed);
        if (string.IsNullOrWhiteSpace(userId)) return new UnauthorizedResult();
        var summary = await summaries.GetAsync(userId);
        if (summary is not null)
            summary.RecoveryOptions = AICreditsRecoveryConfiguration.ForSummary(summary, configuration, quotas);
        return summary is null ? new NotFoundObjectResult(new { code = "billing_period_unavailable" }) : new OkObjectResult(summary);
    }
}
