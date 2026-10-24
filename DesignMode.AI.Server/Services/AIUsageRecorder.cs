using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

/// <summary>Durable starts for paid work; best-effort provider outcome updates.</summary>
public sealed class AIUsageRecorder(
    AIGenerationUsageStore store,
    AIProviderPricingService pricing,
    ILogger<AIUsageRecorder> logger)
{
    public Task<string?> StartCreditJobAsync(string userId, string projectId, string? funnelId, string jobId, string feature, decimal units) =>
        StartAsync(new AIGenerationUsage
        {
            UserId = userId, ProjectId = projectId, FunnelId = funnelId, GenerationJobId = jobId,
            ProviderTaskId = jobId + ":credit-job", Provider = "DesignMode", Operation = "GenerationJob",
            Feature = feature, UsageScope = "Paid", GenerationUnits = units, GenerationCategory = "PaidWorkspace"
        });

    public async Task<string?> StartAsync(AIGenerationUsage usage)
    {
        try { return await store.CreateStartedAsync(usage); }
        catch (Exception ex)
        {
            // Recording paid work is required even when transactional credit holds
            // are disabled. Otherwise a storage outage silently grants untracked work.
            if (usage.UsageScope == "Paid") throw;
            logger.LogWarning(ex, "[AI USAGE] Accounting start failed; provider operation will continue");
            return null;
        }
    }

    public async Task CompleteOpenAiAsync(string? usageId, string responseJson, string model, string operation, string? requestId)
    {
        if (usageId is null) return;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var usage = OpenAIUsageParser.ParseResponse(document.RootElement, model);
            await store.CompleteOpenAiSuccessAsync(usageId, usage, pricing, operation, requestId);
        }
        catch (Exception ex) { logger.LogError(ex, "[AI USAGE] OpenAI cost persistence failed for usage {UsageId}; reconciliation required", usageId); }
    }

    public async Task CompleteSuccessAsync(string? usageId, decimal? actualCost = null)
    {
        if (usageId is null) return;
        try { await store.CompleteSuccessAsync(usageId, actualCost: actualCost); }
        catch (Exception ex) { logger.LogError(ex, "[AI USAGE] Provider cost persistence failed for usage {UsageId}; reconciliation required", usageId); }
    }

    public async Task CompleteFailureAsync(string? usageId, string category)
    {
        if (usageId is null) return;
        try { await store.CompleteFailureAsync(usageId, category); }
        catch (Exception ex) { logger.LogWarning(ex, "[AI USAGE] Accounting failure update failed"); }
    }
}
