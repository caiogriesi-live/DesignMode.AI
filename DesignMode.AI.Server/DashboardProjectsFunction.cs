using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server;

public sealed class DashboardProjectsFunction(
    JwtUserResolver jwt, AttributionStore attribution, ILogger<DashboardProjectsFunction> logger)
{
    [Function("DashboardProjects")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/projects")] HttpRequest request)
    {
        try
        {
            var claimed = jwt.ResolveUserId(request);
            var username = jwt.ResolveUsername(request);
            var userId = !string.IsNullOrWhiteSpace(claimed)
                ? await attribution.ResolveClaimedUserIdAsync(claimed)
                : string.IsNullOrWhiteSpace(username) ? null : await attribution.ResolveUserIdAsync(username);
            if (string.IsNullOrWhiteSpace(userId)) return new UnauthorizedResult();
            using var client = attribution.CreateClient();
            var projects = await DashboardPromptQuery.ListAsync(client, userId);
            logger.LogInformation("[DASHBOARD] project count={Count}", projects.Count);
            return new OkObjectResult(projects);
        }
        catch
        {
            logger.LogWarning("[DASHBOARD] project lookup failed");
            return new ObjectResult("Projects are temporarily unavailable. Please retry.")
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }
}
