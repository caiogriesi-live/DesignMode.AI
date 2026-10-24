using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DesignMode.AI.Server.Security;

namespace DesignMode.AI.Server
{
    public class UpdateUserCurrentPageMain
    {
        private readonly ILogger<UpdateUserCurrentPageMain> _logger;
        private readonly JwtUserResolver _jwtUserResolver;
        private readonly DesignMode.AI.Shared.Navigation.IPostAuthRoutingStore _progressStore;

        public UpdateUserCurrentPageMain(
            ILogger<UpdateUserCurrentPageMain> logger,
            JwtUserResolver jwtUserResolver, DesignMode.AI.Shared.Navigation.IPostAuthRoutingStore progressStore)
        {
            _logger = logger;
            _jwtUserResolver = jwtUserResolver;
            _progressStore = progressStore;
        }

        [Function("UpdateUserCurrentPage")]
        public async Task<IActionResult> UpdateUserCurrentPage(
                    [HttpTrigger(AuthorizationLevel.Function, "post")]
            HttpRequest req)
        {
            var username = _jwtUserResolver.ResolveUsername(req);
            if (string.IsNullOrWhiteSpace(username)) return new UnauthorizedResult();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<UpdateUserCurrentPageRequest>(req.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (string.IsNullOrWhiteSpace(body?.Currentpage) ||
                    !Services.CanonicalUserProgress.TryNormalizePage(body.Currentpage,
                        Environment.GetEnvironmentVariable("HostUrl") ?? string.Empty, out var page))
                    return new BadRequestObjectResult(new { Message = "A safe local Currentpage is required." });
                await _progressStore.SaveCurrentPageAsync(username, page);
                return new OkObjectResult(new { Success = true, Currentpage = page });
            }
            catch (JsonException)
            {
                return new BadRequestObjectResult(new { Message = "Invalid request." });
            }
            catch (Exception)
            {
                _logger.LogWarning("Current-page save failed for the authenticated account.");
                return new ObjectResult(new { Message = "Your progress could not be saved. Please try again." }) { StatusCode = 503 };
            }
        }
        public sealed class UpdateUserCurrentPageRequest
        {
            public string Email { get; set; } = string.Empty;

            public string Currentpage { get; set; } = string.Empty;
        }

        private static AmazonDynamoDBClient CreateDynamoDbClient()
        {
            return global::DesignMode.AI.Server.Services.DynamoDbClientFactory.CreateClient();
        }

        [Function("GetUserResumePage")]
        public async Task<IActionResult> GetUserResumePage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
        {
            string? origin = req.Headers["Origin"].FirstOrDefault();
            string? allowedOrigin = Environment.GetEnvironmentVariable("HostUrl");
            if (!string.IsNullOrWhiteSpace(allowedOrigin) &&
                !string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
                return new UnauthorizedResult();

            GetUserResumePageRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<GetUserResumePageRequest>(
                    req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return CreateBadRequest("The request body contains invalid JSON.");
            }

            if (string.IsNullOrWhiteSpace(request?.Email))
                return CreateBadRequest("Email is required.");

            using var client = CreateDynamoDbClient();
            var email = request.Email.Trim().ToLowerInvariant();
            var authenticatedEmail = _jwtUserResolver.ResolveUsername(req)?.Trim().ToLowerInvariant();
            if (!string.Equals(email, authenticatedEmail, StringComparison.OrdinalIgnoreCase))
                return new UnauthorizedResult();
            var result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = "SELECT Id, CurrentPage FROM Users WHERE Username = ?",
                Parameters = [new AttributeValue { S = email }]
            });
            var item = result.Items.FirstOrDefault();
            if (item is null)
            {
                result = await client.ExecuteStatementAsync(new ExecuteStatementRequest
                {
                    Statement = "SELECT Id, CurrentPage FROM Users WHERE Email = ?",
                    Parameters = [new AttributeValue { S = email }]
                });
                item = result.Items.FirstOrDefault();
            }

            if (item is null) return new OkObjectResult(new { CurrentPage = (string?)null, Authorized = true });
            var currentPage = item.GetValueOrDefault("CurrentPage")?.S;
            var userId = item.GetValueOrDefault("Id")?.S;
            var authorized = await IsOwnedResumeDestination(client, currentPage, userId);
            return new OkObjectResult(new { CurrentPage = authorized ? currentPage : null, Authorized = authorized });
        }

        private static async Task<bool> IsOwnedResumeDestination(
            IAmazonDynamoDB client, string? currentPage, string? userId)
        {
            if (string.IsNullOrWhiteSpace(currentPage)) return true;
            var route = Uri.TryCreate(currentPage, UriKind.Absolute, out var absolute)
                ? absolute.PathAndQuery
                : currentPage;
            if (!route.Contains("/init/gen", StringComparison.OrdinalIgnoreCase)) return true;

            var queryIndex = route.IndexOf('?');
            if (queryIndex < 0) return true;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(route[(queryIndex + 1)..]);
            var resourceId = query["cid"].FirstOrDefault() ?? query["projectId"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(resourceId)) return true;
            if (string.IsNullOrWhiteSpace(userId)) return false;

            var ownership = await client.ExecuteStatementAsync(new ExecuteStatementRequest
            {
                Statement = "SELECT Id FROM CompanyIndustry WHERE Id = ? AND UserId = ?",
                Parameters = [new AttributeValue { S = resourceId }, new AttributeValue { S = userId }]
            });
            return ownership.Items.Any();
        }

        private sealed class GetUserResumePageRequest
        {
            public string Email { get; set; } = string.Empty;
        }

        private static BadRequestObjectResult CreateBadRequest(
            string message)
        {
            return new BadRequestObjectResult(
                new UpdateUserCurrentPageResponse
                {
                    Success = false,
                    Message = message
                });
        }
    }

    public sealed class UpdateUserCurrentPageRequest
    {
        public string Email { get; set; } = string.Empty;

        public string Currentpage { get; set; } = string.Empty;
    }

    public sealed class UpdateUserCurrentPageResponse
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public string? Email { get; set; }

        public string? Currentpage { get; set; }
    }
}
