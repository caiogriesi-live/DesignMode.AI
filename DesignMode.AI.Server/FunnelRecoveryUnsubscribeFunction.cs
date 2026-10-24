using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server;

public sealed class FunnelRecoveryUnsubscribeFunction(FunnelRecoveryUnsubscribe unsubscribe,
    ILogger<FunnelRecoveryUnsubscribeFunction> logger)
{
    [Function("UnsubscribeRecoveryEmails")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recovery/unsubscribe")] HttpRequest request)
    {
        // The signed token authorizes only opting out. No login or function key is required.
        // GET requests never mutate preferences, protecting against email link scanners.
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method)) return new StatusCodeResult(StatusCodes.Status405MethodNotAllowed);
        try
        {
            if (request.ContentLength is > 512) return new BadRequestResult();
            using var reader = new StreamReader(request.Body);
            var buffer = new char[130];
            var count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (count != 129 || !await unsubscribe.UnsubscribeAsync(new string(buffer, 0, count)))
                return new BadRequestObjectResult("This unsubscribe link is invalid.");
            return new OkObjectResult("You have been unsubscribed from DesignMode recovery reminders.");
        }
        catch
        {
            logger.LogWarning("[RECOVERY EMAIL] unsubscribe could not be saved");
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
