using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace DesignMode.AI.Server;

// Compatibility endpoint only. Historical AdvertisingConsents rows remain untouched and read-only.
public sealed class AdvertisingConsentFunction
{
    [Function("AdvertisingConsent")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        return new StatusCodeResult(StatusCodes.Status410Gone);
    }
}
