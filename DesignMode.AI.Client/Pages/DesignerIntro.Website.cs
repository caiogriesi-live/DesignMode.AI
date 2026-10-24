using System.Net.Http.Headers;
using System.Net.Http.Json;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Shared.Services;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.WebUtilities;
using DesignMode.AI.Shared.Models.Attribution;

namespace DesignMode.AI.Client.Pages;

public partial class DesignerIntro
{
    private async Task<bool> RestoreWebsiteLandingContextAsync()
    {
        var query = QueryHelpers.ParseQuery(new Uri(NavigationManager.Uri).Query);
        if (!query.TryGetValue("projectId", out var project) || !Guid.TryParse(project, out _) ||
            !query.TryGetValue("funnelId", out var funnel) || !Guid.TryParse(funnel, out _)) return false;
        await AttributionContext.SetActiveProjectAsync(new ProjectContextResponse { ProjectId = project.ToString(), FunnelId = funnel.ToString() });
        // Claim an unowned landing project before PrepareWebsiteFunnel checks its owner.
        // If browser attribution is stale, the service accepts the bind only when the
        // JWT can independently verify this exact project/funnel pair.
        return await AttributionContext.BindOrVerifyOwnedProjectAsync(project.ToString(), funnel.ToString());
    }

    private string WebsiteSelectionUrl(string email) => QueryHelpers.AddQueryString("/init/mainconceptwebsite",
        new Dictionary<string, string?>
        {
            ["i"] = "1", ["id"] = inviteId, ["e"] = email, ["eid"] = email,
            ["projectId"] = _canonicalProjectId, ["funnelId"] = _canonicalFunnelId
        });

    private async Task LoadWebsiteConceptsAsync()
    {
        IsLoadingInviteData = true;
        _allInviteSavesCompleted = false;
        _inviteSaveFailed = false;
        try
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Industry))
                throw new InvalidOperationException("Return to the website landing page and enter your company and industry.");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiConfig.ApiBaseUrl}api/PrepareWebsiteFunnel")
            {
                Content = JsonContent.Create(new WebsiteFunnelPreparation
                {
                    Name = Name, Industry = Industry,
                    ProjectId = _canonicalProjectId ?? "", FunnelId = _canonicalFunnelId ?? ""
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _localStorage.GetItemAsync<string>("authToken"));
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var prepared = await response.Content.ReadFromJsonAsync<WebsiteFunnelPrepared>()
                ?? throw new InvalidOperationException("Your website project could not be prepared.");
            inviteId = prepared.InviteId;
            if (!await AttributionContext.BindOrVerifyOwnedProjectAsync(
                _canonicalProjectId ?? string.Empty,
                _canonicalFunnelId ?? string.Empty,
                legacyCompanyIdeaId: prepared.CompanyIdeaId, legacyInviteId: inviteId))
                throw new InvalidOperationException("Your website project could not be connected to your account.");

            for (var index = 0; index < WebsiteFunnelPromptResolver.ConceptCount; index++)
            {
                var prompt = WebsiteFunnelPromptResolver.Concept(Name, Industry, index);
                var jobId = Guid.NewGuid().ToString();
                var imageUrl = await CallRunwareImageFunction(prompt, 1344, 768, 1, jobId);
                if (string.IsNullOrWhiteSpace(imageUrl)) throw new InvalidOperationException("A website concept could not be generated. Please try again.");
                var imageId = await CallSaveImageIdeaFunction("Website UI/UX", "Homepage", prompt, imageUrl,
                    prepared.PromptId, DateTime.UtcNow.ToString("O"), "1");
                if (string.IsNullOrWhiteSpace(imageId)) throw new InvalidOperationException("A website concept could not be saved.");
                var saved = await SaveInviteClientImageWithRetryAsync(inviteId, imageId, imageUrl,
                    (index + 1).ToString(), WebsiteFunnelPromptResolver.Styles[index], prompt,
                    "Preserve website hierarchy and responsive composition.", WebsiteFunnelPromptResolver.Brief(Name, Industry),
                    "High-fidelity homepage concept.");
                if (string.IsNullOrWhiteSpace(saved)) throw new InvalidOperationException("Your website selection could not be saved.");

                if (index == 0) { imageUrl1 = imageUrl; imageId1 = imageId; style1 = WebsiteFunnelPromptResolver.Styles[index]; }
                if (index == 1) { imageUrl2 = imageUrl; imageId2 = imageId; style2 = WebsiteFunnelPromptResolver.Styles[index]; }
                if (index == 2) { imageUrl3 = imageUrl; imageId3 = imageId; style3 = WebsiteFunnelPromptResolver.Styles[index]; }
                var queuedJob = await QueueIdeaGeneration(new OpenAIIdeaRequestQueue
                {
                    Name = Name, Industry = Industry, ImageId = imageId, ImageUrl = imageUrl,
                    ConceptNumber = index + 1, JobId = jobId, GenerationJobId = jobId,
                    UserId = _canonicalUserId, ProjectId = _canonicalProjectId, FunnelId = _canonicalFunnelId,
                    LandingVariant = WebsiteFunnelPromptResolver.LandingVariant
                });
                if (string.IsNullOrWhiteSpace(queuedJob)) throw new InvalidOperationException("The website preview could not be queued.");
                await InvokeAsync(StateHasChanged);
            }
            _allInviteSavesCompleted = true;
            await JSRuntime.InvokeVoidAsync("designerIntroGenerationComplete");
        }
        catch (Exception)
        {
            _inviteSaveFailed = true;
            InviteErrorMessage = "We couldn't finish your website directions. Please refresh to try again.";
            await JSRuntime.InvokeVoidAsync("designerIntroGenerationFailed", InviteErrorMessage);
        }
        finally
        {
            IsLoadingInviteData = false;
            await InvokeAsync(StateHasChanged);
        }
        if (_allInviteSavesCompleted) await TryNavigateWhenReadyAsync();
    }
}
