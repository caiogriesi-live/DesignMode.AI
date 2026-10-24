using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Shared.Services;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Stripe;

namespace DesignMode.AI.Server;

public partial class DesignModeFunctions
{
    [Function("SaveWebsiteCompany")]
    public async Task<IActionResult> SaveWebsiteCompany(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
    {
        if (req.Headers["Origin"].ToString() != Environment.GetEnvironmentVariable("HostUrl"))
            return new UnauthorizedResult();
        var input = await JsonSerializer.DeserializeAsync<CompanyIndustryLocalRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (input is null || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Industry) ||
            input.Name.Length > 200 || input.Industry.Length > 200 || !Guid.TryParse(input.Local, out _))
            return new BadRequestObjectResult("A company, industry and anonymous session are required.");
        input.UserId = input.Local;
        return await SaveCompanyIndustryLocal(WebsiteInternalRequest(input));
    }

    [Function("PrepareWebsiteFunnel")]
    public async Task<IActionResult> PrepareWebsiteFunnel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
    {
        var username = jwtUserResolver.ResolveUsername(req);
        var userId = string.IsNullOrWhiteSpace(username) ? null : await attributionStore.ResolveUserIdAsync(username);
        if (string.IsNullOrWhiteSpace(userId)) return new UnauthorizedResult();
        var input = await JsonSerializer.DeserializeAsync<WebsiteFunnelPreparation>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (input is null || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Industry) ||
            input.Name.Length > 200 || input.Industry.Length > 200)
            return new BadRequestObjectResult("Enter a company name and industry, each up to 200 characters.");
        if (!await attributionStore.ValidateGenerationContextAsync(userId, input.ProjectId, input.FunnelId))
            return new UnauthorizedResult();

        var brief = WebsiteFunnelPromptResolver.Brief(input.Name, input.Industry);
        // Use the existing invite and prompt persistence, including their established legacy formats.
        var inviteResult = await SaveInviteClient(WebsiteInternalRequest(new
        {
            input.Name, input.Industry, Prompt = brief, Email = username, ImageUrls = "", UseSameImageSeedForAll = false
        }));
        if (inviteResult is not OkObjectResult inviteOk) return inviteResult;
        var inviteId = JsonSerializer.SerializeToElement(inviteOk.Value).GetProperty("Id").GetString()!;
        var cid = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(input.Name))}|~~|@@|^^|{Convert.ToBase64String(Encoding.UTF8.GetBytes(input.Industry))}|~~|@@|^^|{Guid.NewGuid():N}";
        var promptResult = await SavePrompt(WebsiteInternalRequest(new
        {
            Prompt = brief, CompanyIdeaId = cid, UserId = username, TimeStamp = DateTime.UtcNow.ToString("O"), InviteId = inviteId
        }));
        if (promptResult is not OkObjectResult promptOk) return promptResult;
        var promptId = JsonSerializer.SerializeToElement(promptOk.Value).GetProperty("PromptId").GetString()!;
        return new OkObjectResult(new WebsiteFunnelPrepared { CompanyIdeaId = cid, InviteId = inviteId, PromptId = promptId });
    }

    private static HttpRequest WebsiteInternalRequest(object body)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["Origin"] = Environment.GetEnvironmentVariable("HostUrl");
        request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
        return request;
    }

    private async Task<bool> ValidateWebsiteInviteAsync(string userId, string projectId, string inviteId)
    {
        using var db = attributionStore.CreateClient();
        var project = await db.GetItemAsync("Projects", new() { ["Id"] = new() { S = projectId } });
        return project.Item?.GetValueOrDefault("UserId")?.S == userId &&
            project.Item?.GetValueOrDefault("LegacyInviteId")?.S == inviteId &&
            !string.IsNullOrWhiteSpace(project.Item?.GetValueOrDefault("LegacyCompanyIdeaId")?.S);
    }

    [Function("WebsiteFunnelReturn")]
    public async Task<IActionResult> GetWebsiteFunnelReturn(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req)
    {
        var username = jwtUserResolver.ResolveUsername(req);
        var userId = string.IsNullOrWhiteSpace(username) ? null : await attributionStore.ResolveUserIdAsync(username);
        if (string.IsNullOrWhiteSpace(userId)) return new UnauthorizedResult();
        var sessionId = req.Query["session_id"].ToString();
        if (!sessionId.StartsWith("cs_", StringComparison.Ordinal) || sessionId.Length > 300)
            return new BadRequestObjectResult("A checkout session is required.");
        // Same configured Stripe client/key as checkout: test and live sessions cannot cross environments.
        var session = await new Stripe.Checkout.SessionService(new StripeClient(
            Environment.GetEnvironmentVariable("StripeSecretKey"))).GetAsync(sessionId);
        if (!WebsiteFunnelCheckout.IsPaidOwner(session, userId)) return new UnauthorizedResult();
        var projectId = session.Metadata.GetValueOrDefault("projectId") ?? "";
        var funnelId = session.Metadata.GetValueOrDefault("funnelId") ?? "";
        var inviteId = session.Metadata.GetValueOrDefault("id") ?? "";
        if (!await attributionStore.ValidateGenerationContextAsync(userId, projectId, funnelId) ||
            !await ValidateWebsiteInviteAsync(userId, projectId, inviteId)) return new UnauthorizedResult();

        using var db = attributionStore.CreateClient();
        var project = await db.GetItemAsync("Projects", new() { ["Id"] = new() { S = projectId } });
        var cid = project.Item.GetValueOrDefault("LegacyCompanyIdeaId")?.S ?? "";
        var invite = await db.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT ImageId, Name, Industry, Prompt FROM InviteClient WHERE Id = ?",
            Parameters = [new() { S = inviteId }]
        });
        var savedInvite = invite.Items.FirstOrDefault();
        var imageId = savedInvite?.GetValueOrDefault("ImageId")?.S;
        if (string.IsNullOrWhiteSpace(imageId)) return new ConflictObjectResult("Choose a website concept before opening the workspace.");
        var image = await db.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT PromptId, ImageUrl FROM ImageIdeas WHERE Id = ?", Parameters = [new() { S = imageId }]
        });
        var promptId = image.Items.FirstOrDefault()?.GetValueOrDefault("PromptId")?.S;
        if (string.IsNullOrWhiteSpace(promptId)) return new ConflictObjectResult("The selected website concept is not available.");
        var prompt = await db.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = "SELECT CompanyIdeaId FROM Prompt WHERE Id = ? AND InviteId = ?",
            Parameters = [new() { S = promptId }, new() { S = inviteId }]
        });
        if (prompt.Items.FirstOrDefault()?.GetValueOrDefault("CompanyIdeaId")?.S != cid)
            return new UnauthorizedResult();
        var name = savedInvite?.GetValueOrDefault("Name")?.S;
        var industry = savedInvite?.GetValueOrDefault("Industry")?.S;
        var brief = savedInvite?.GetValueOrDefault("Prompt")?.S;
        var imageUrl = image.Items.FirstOrDefault()?.GetValueOrDefault("ImageUrl")?.S;
        if (new[] { name, industry, brief, imageUrl }.Any(string.IsNullOrWhiteSpace))
            return new ConflictObjectResult("The saved website brief or selected image is not available.");
        return new OkObjectResult(new WebsiteFunnelReturn
        {
            ProjectId = projectId, FunnelId = funnelId, CompanyIdeaId = cid, ImageId = imageId,
            WorkspaceUrl = WebsiteFunnelContext.WorkspaceUrl(cid, projectId, imageId, name!, industry!, brief!, inviteId, imageUrl!)
        });
    }
}
