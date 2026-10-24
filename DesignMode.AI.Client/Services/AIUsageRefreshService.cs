namespace DesignMode.AI.Client.Services;

public sealed class AIUsageRefreshService
{
    public event Func<Task>? RefreshRequested;
    public event Action? Changed;
    public DesignMode.AI.Shared.Models.AIUsageSummary? Summary { get; private set; }
    public bool IsModalOpen { get; private set; }

    public void SetSummary(DesignMode.AI.Shared.Models.AIUsageSummary? summary)
    {
        var wasExhausted = Summary is not null && DesignMode.AI.Shared.Models.AIUsageSummaryDisplay.IsExhausted(Summary);
        var changedPeriod = Summary?.BillingPeriodStart != summary?.BillingPeriodStart;
        Summary = summary;
        var exhausted = summary is not null && DesignMode.AI.Shared.Models.AIUsageSummaryDisplay.IsExhausted(summary);
        if (summary is not null && !exhausted && wasExhausted) IsModalOpen = false;
        else if (exhausted && (!wasExhausted || changedPeriod)) IsModalOpen = true;
        Changed?.Invoke();
    }

    public void Open() { IsModalOpen = true; Changed?.Invoke(); }
    public void Close() { IsModalOpen = false; Changed?.Invoke(); }

    public async Task<bool> HandleResponseAsync(HttpResponseMessage response)
    {
        if (!await AICreditsQuotaResponse.IsExhaustedAsync(response)) return false;
        // A refresh failure must not swallow the canonical rejection or break generation cleanup.
        try { await RequestRefreshAsync(); }
        catch { Summary = null; }
        finally { Open(); }
        return true;
    }

    public async Task RequestRefreshAsync()
    {
        var handlers = RefreshRequested;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<Task> handler in handlers.GetInvocationList())
        {
            await handler();
        }
    }
}
