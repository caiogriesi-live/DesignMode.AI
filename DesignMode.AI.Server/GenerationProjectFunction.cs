using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server;

public sealed class GenerationProjectFunction(AttributionStore store, JwtUserResolver jwt,
    ILogger<GenerationProjectFunction> logger)
{
    [Function("AuthorizeGenerationProject")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HostUrl")) ||
            request.Headers.Origin.ToString().TrimEnd('/') != Environment.GetEnvironmentVariable("HostUrl")?.TrimEnd('/'))
            return new UnauthorizedResult();
        try
        {
            logger.LogInformation("[GEN AUTH] canonical project verification starting");
            var claimedId = jwt.ResolveUserId(request);
            var username = jwt.ResolveUsername(request);
            var userId = !string.IsNullOrWhiteSpace(claimedId) ? await store.ResolveClaimedUserIdAsync(claimedId) :
                string.IsNullOrWhiteSpace(username) ? null : await store.ResolveUserIdAsync(username);
            logger.LogInformation("[GEN AUTH] authenticated user resolved={Resolved}", userId is not null);
            if (string.IsNullOrWhiteSpace(userId)) return new UnauthorizedResult();
            LegacyProjectResolutionRequest? input;
            try { input = await request.ReadFromJsonAsync<LegacyProjectResolutionRequest>(); }
            catch { return new BadRequestResult(); }
            if (string.IsNullOrWhiteSpace(input?.LegacyCompanyIdeaId)) return new BadRequestResult();
            using var client = store.CreateClient();
            var context = await GenerationProjectAuthorization.ResolveCompanyIdeaAsync(client, userId, input.LegacyCompanyIdeaId);
            logger.LogInformation("[GEN AUTH] ownership verified={Verified}", context is not null);
            return context is null ? new UnauthorizedResult() : new OkObjectResult(context);
        }
        catch (Exception)
        {
            logger.LogWarning("[GEN AUTH] project verification unavailable; generation blocked");
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
