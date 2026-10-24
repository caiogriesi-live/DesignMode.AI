using System.Text.Json;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Shared.Navigation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server;

public sealed class PostAuthDestinationFunction(
    JwtUserResolver identity, IPostAuthRoutingStore store, ILogger<PostAuthDestinationFunction> logger)
{
    [Function("ResolvePostAuthDestination")]
    public async Task<IActionResult> Resolve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        var username = identity.ResolveUsername(request);
        if (string.IsNullOrWhiteSpace(username)) return new UnauthorizedResult();

        PostAuthRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<PostAuthRequest>(
                request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new { Message = "Invalid request." });
        }

        if (PostAuthDestinationResolver.TryNormalizeFunnelRoute(body?.CurrentFunnelRoute, out var currentPage))
        {
            try
            {
                await store.SaveCurrentPageAsync(username, currentPage);
            }
            catch
            {
                logger.LogWarning("[AUTH ROUTING] current-page save failed");
                return new ObjectResult(new { Message = "Your account progress could not be saved. Please try again." }) { StatusCode = 503 };
            }
        }

        var destination = await PostAuthDestinationResolver.ResolveAsync(
            username, body?.ReturnUrl, store, message => logger.LogInformation("{RoutingMessage}", message),
            currentFunnelRoute: body?.CurrentFunnelRoute);
        return new OkObjectResult(new { Destination = destination });
    }

    // Identity, UserId and subscription state are never accepted from the browser.
    public sealed class PostAuthRequest
    {
        public string? ReturnUrl { get; set; }
        public string? CurrentFunnelRoute { get; set; }
    }
}
