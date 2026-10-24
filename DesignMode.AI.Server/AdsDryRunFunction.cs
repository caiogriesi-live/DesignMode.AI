using DesignMode.AI.Server.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server;

public sealed class AdsDryRunFunction(AdsDryRunService service, AdsDeliveryOptions options, ILogger<AdsDryRunFunction> logger)
{
    [Function("EvaluateAdsDryRun")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        logger.LogInformation("[ADS DRY RUN] timer started");
        logger.LogInformation("[ADS DRY RUN] canEvaluate={CanEvaluate}", options.CanEvaluateDryRun.ToString().ToLowerInvariant());
        if (!options.MeasurementLedgerEnabled)
        { logger.LogWarning("[ADS DRY RUN] skipped: MEASUREMENT_LEDGER_DISABLED"); return; }
        if (!options.CanEvaluateDryRun)
        {
            var reasons = options.ValidationFailures.Count == 0 ? "MODE_NOT_DRY_RUN" : string.Join(",", options.ValidationFailures);
            logger.LogWarning("[ADS DRY RUN] skipped: {Reasons}", reasons);
            return;
        }
        await service.RunAsync();
    }
}
