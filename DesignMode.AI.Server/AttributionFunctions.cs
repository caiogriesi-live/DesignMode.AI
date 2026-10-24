using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models.Attribution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DesignMode.AI.Server;

public sealed class AttributionFunctions
{
    private readonly AttributionStore _store;
    private readonly JwtUserResolver _identity;
    private readonly ILogger<AttributionFunctions> _logger;

    public AttributionFunctions(AttributionStore store, JwtUserResolver identity, ILogger<AttributionFunctions> logger)
    {
        _store = store;
        _identity = identity;
        _logger = logger;
    }

    [Function("CaptureSocialAcquisition")]
    public async Task<IActionResult> CaptureSocialAcquisition([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        var elapsed = Stopwatch.StartNew();
        _logger.LogInformation("[ATTRIBUTION SERVER] CaptureSocialAcquisition entered");
        try
        {
            if (!OriginAllowed(request)) return new UnauthorizedResult();
            AcquisitionCaptureRequest? value;
            try { value = await request.ReadFromJsonAsync<AcquisitionCaptureRequest>(); }
            catch (Exception) { return new BadRequestObjectResult("The request body must be valid JSON."); }
            if (value is null || !Guid.TryParse(value.VisitorId, out _) || !Guid.TryParse(value.SessionId, out _) || !Guid.TryParse(value.FunnelId, out _))
                return new BadRequestObjectResult("Valid VisitorId, SessionId, and FunnelId are required.");
            _logger.LogInformation("[ATTRIBUTION SERVER] request validated");
            value.Referrer = SanitizeReferrer(value.Referrer);
            value.LandingPage = Limit(value.LandingPage, 500);
            var result = await _store.CaptureAsync(value);
            _logger.LogInformation("[ATTRIBUTION SERVER] CaptureSocialAcquisition HTTP COMPLETE {ElapsedMs} ms", elapsed.ElapsedMilliseconds);
            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ATTRIBUTION SERVER] CaptureSocialAcquisition persistence failed after {ElapsedMs} ms", elapsed.ElapsedMilliseconds);
            return new ObjectResult("Attribution persistence is temporarily unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }

    [Function("CreateSocialProject")]
    public async Task<IActionResult> CreateSocialProject([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        var elapsed = Stopwatch.StartNew();
        _logger.LogInformation("[ATTRIBUTION SERVER] CreateSocialProject entered");
        try
        {
            if (!OriginAllowed(request)) return new UnauthorizedResult();
            CreateSocialProjectRequest? value;
            try { value = await request.ReadFromJsonAsync<CreateSocialProjectRequest>(); }
            catch (Exception) { return new BadRequestObjectResult("The request body must be valid JSON."); }
            if (value is null || !Guid.TryParse(value.VisitorId, out _) || !Guid.TryParse(value.SessionId, out _) || !Guid.TryParse(value.FunnelId, out _))
                return new BadRequestObjectResult("Valid VisitorId, SessionId, and FunnelId are required.");
            var result = await _store.CreateProjectAsync(value);
            _logger.LogInformation("[ATTRIBUTION SERVER] CreateSocialProject HTTP COMPLETE {ElapsedMs} ms", elapsed.ElapsedMilliseconds);
            return result is null ? new NotFoundObjectResult("Funnel was not found or is incomplete.") : new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ATTRIBUTION SERVER] CreateSocialProject persistence failed after {ElapsedMs} ms", elapsed.ElapsedMilliseconds);
            return new ObjectResult("Project persistence is temporarily unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }

    [Function("BindSocialIdentity")]
    public async Task<IActionResult> BindSocialIdentity([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        try
        {
            _logger.LogInformation("[ATTRIBUTION SERVER] BindSocialIdentity entered");
            if (!OriginAllowed(request)) return new UnauthorizedObjectResult("origin_not_allowed");
            _logger.LogInformation("[AUTH] JWT validation started");
            var principal = _identity.ResolvePrincipal(request);
            if (principal is null) return new UnauthorizedObjectResult("jwt_validation_failed");
            _logger.LogInformation("[AUTH] JWT validated");
            var claimedUserId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            _logger.LogInformation("[AUTH] NameIdentifier present: {Present}", !string.IsNullOrWhiteSpace(claimedUserId));
            var username = principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
            var userId = !string.IsNullOrWhiteSpace(claimedUserId)
                ? await _store.ResolveClaimedUserIdAsync(claimedUserId)
                : string.IsNullOrWhiteSpace(username) ? null : await _store.ResolveUserIdAsync(username);
            if (string.IsNullOrWhiteSpace(userId)) return new UnauthorizedObjectResult("canonical_user_not_found");
            _logger.LogInformation("[AUTH] canonical user resolved");
            BindIdentityRequest? value;
            try { value = await request.ReadFromJsonAsync<BindIdentityRequest>(); }
            catch { return new BadRequestObjectResult("invalid_json"); }
            if (value is null || !Guid.TryParse(value.VisitorId, out _) || !Guid.TryParse(value.SessionId, out _) || !Guid.TryParse(value.ProjectId, out _) || !Guid.TryParse(value.FunnelId, out _))
                return new BadRequestObjectResult("invalid_browser_context");
            var result = await _store.BindWithResultAsync(value, userId);
            if (!result.Success)
            {
                _logger.LogWarning("[ATTRIBUTION SERVER] Identity binding rejected: {ErrorCode}", result.ErrorCode);
                return new ConflictObjectResult(result.ErrorCode);
            }
            _logger.LogInformation("[ATTRIBUTION SERVER] BindSocialIdentity complete");
            return new OkObjectResult(new { userId, value.ProjectId, value.FunnelId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ATTRIBUTION SERVER] BindSocialIdentity persistence failed");
            return new ObjectResult("dynamodb_failure") { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }

    [Function("CreateAdditionalProject")]
    public async Task<IActionResult> CreateAdditionalProject([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        if (!OriginAllowed(request)) return new UnauthorizedResult();
        var userId = await ResolveAuthenticatedUserIdAsync(request);
        if (userId is null) return new UnauthorizedResult();
        var value = await request.ReadFromJsonAsync<AdditionalProjectRequest>() ?? new AdditionalProjectRequest();
        return new OkObjectResult(await _store.CreateAdditionalProjectAsync(value, userId));
    }

    [Function("ListProjects")]
    public async Task<IActionResult> ListProjects([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects")] HttpRequest request)
    {
        if (!OriginAllowed(request)) return new UnauthorizedResult();
        var userId = await ResolveAuthenticatedUserIdAsync(request);
        return userId is null ? new UnauthorizedResult() : new OkObjectResult(await _store.ListProjectsAsync(userId));
    }

    [Function("OpenProject")]
    public async Task<IActionResult> OpenProject([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "projects/{projectId}")] HttpRequest request, string projectId)
    {
        if (!OriginAllowed(request)) return new UnauthorizedResult();
        var userId = await ResolveAuthenticatedUserIdAsync(request);
        if (userId is null) return new UnauthorizedResult();
        var context = await _store.GetOwnedProjectContextAsync(userId, projectId);
        return context is null ? new NotFoundResult() : new OkObjectResult(context);
    }

    [Function("ResolveLegacyProject")]
    public async Task<IActionResult> ResolveLegacyProject([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest request)
    {
        if (!OriginAllowed(request)) return new UnauthorizedResult();
        var userId = await ResolveAuthenticatedUserIdAsync(request);
        if (userId is null) return new UnauthorizedResult();
        var value = await request.ReadFromJsonAsync<LegacyProjectResolutionRequest>();
        if (value is null) return new BadRequestObjectResult("A legacy identifier is required.");
        return new OkObjectResult(await _store.ResolveLegacyProjectAsync(value, userId));
    }

    private async Task<string?> ResolveAuthenticatedUserIdAsync(HttpRequest request)
    {
        var claimedUserId = _identity.ResolveUserId(request);
        if (!string.IsNullOrWhiteSpace(claimedUserId))
            return await _store.ResolveClaimedUserIdAsync(claimedUserId);
        var username = _identity.ResolveUsername(request);
        return string.IsNullOrWhiteSpace(username) ? null : await _store.ResolveUserIdAsync(username);
    }

    private static bool OriginAllowed(HttpRequest request)
    {
        var configured = Environment.GetEnvironmentVariable("HostUrl")?.TrimEnd('/');
        var origin = request.Headers.Origin.FirstOrDefault()?.TrimEnd('/');
        return !string.IsNullOrWhiteSpace(configured) && string.Equals(configured, origin, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SanitizeReferrer(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return Limit(uri.GetLeftPart(UriPartial.Path), 500);
    }

    private static string? Limit(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
