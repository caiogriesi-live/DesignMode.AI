using DesignMode.AI.Server.Services;
using Microsoft.Azure.Functions.Worker;

namespace DesignMode.AI.Server;

public sealed class PurchaseMeasurementFunction(PurchaseMeasurementStore store, PurchaseConversionDispatcher dispatcher)
{
    [Function("DeliverPurchaseMeasurements")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MEASUREMENT_LEDGER_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var purchase in await store.PendingAsync()) await dispatcher.DispatchAsync(purchase);
    }
}
