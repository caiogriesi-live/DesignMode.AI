using DesignMode.AI.Server.Services;
using Microsoft.Azure.Functions.Worker;

namespace DesignMode.AI.Server;

public sealed class FunnelRecoverySchedulerFunction(FunnelRecoverySequence sequence)
{
    [Function("ScheduleFunnelRecoveryReminders")]
    public Task RunAsync([TimerTrigger("0 */30 * * * *")] TimerInfo timer) =>
        sequence.RunAsync(DateTimeOffset.UtcNow);
}
