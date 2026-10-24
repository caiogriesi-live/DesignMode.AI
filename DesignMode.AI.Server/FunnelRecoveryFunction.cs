using Azure.Storage.Queues;
using DesignMode.AI.Server.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server;

public sealed class FunnelRecoveryPublisher
{
    public const string QueueName = "funnel-recovery-video-ready";
    private readonly ILogger<FunnelRecoveryPublisher> _logger;
    private readonly Func<bool> _enabled;
    private readonly Func<FunnelVideoReadyEvent, Task> _publish;

    public FunnelRecoveryPublisher(ILogger<FunnelRecoveryPublisher> logger, FunnelRecoveryConfiguration configuration)
        : this(logger, () => configuration.Enabled, async notification =>
        {
            var options = VideoQueuePipeline.CreateClientOptions();
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            var queue = new QueueClient(configuration.Get("AzureWebJobsStorage"), QueueName, options);
            await queue.SendMessageAsync(VideoQueuePipeline.Serialize(notification));
        }) { }

    public FunnelRecoveryPublisher(ILogger<FunnelRecoveryPublisher> logger, Func<bool> enabled,
        Func<FunnelVideoReadyEvent, Task> publish)
    {
        _logger = logger;
        _enabled = enabled;
        _publish = publish;
    }

    public async Task PublishSafelyAsync(string jobId)
    {
        _logger.LogInformation("[FUNNEL RECOVERY] video-ready hook invoked");
        if (!_enabled())
        {
            _logger.LogInformation("[FUNNEL RECOVERY] skipped reason=FEATURE_DISABLED");
            return;
        }
        try
        {
            // Queue/table provisioning is explicit; never create production infrastructure here.
            await _publish(new FunnelVideoReadyEvent(jobId, DateTimeOffset.UtcNow));
            _logger.LogInformation("[FUNNEL RECOVERY] video-ready email queued");
        }
        catch (Azure.RequestFailedException ex) when (ex.ErrorCode == "QueueNotFound")
        {
            _logger.LogWarning("[FUNNEL RECOVERY] queue publication failed category=QUEUE_NOT_FOUND; provision recovery queues in AzureWebJobsStorage");
        }
        catch
        {
            // This observer must never turn completed video generation into a failure.
            _logger.LogWarning("[FUNNEL RECOVERY] queue publication failed");
        }
    }
}

public sealed class FunnelRecoveryFunction(FunnelRecoveryService recovery, ILogger<FunnelRecoveryFunction> logger,
    FunnelRecoveryConfiguration configuration)
{
    [Function("SendFunnelRecoveryVideoReady")]
    public async Task RunAsync([QueueTrigger(FunnelRecoveryPublisher.QueueName, Connection = "AzureWebJobsStorage")] string body)
    {
        if (!configuration.Enabled) return;
        try
        {
            var notification = VideoQueuePipeline.Deserialize<FunnelVideoReadyEvent>(body);
            if (notification is null || string.IsNullOrWhiteSpace(notification.JobId) ||
                notification.ObservedAt == default) throw new InvalidOperationException();
            await recovery.ProcessAsync(notification, true, configuration.HostUrl, configuration.AllowLocalLinks);
        }
        catch
        {
            logger.LogWarning("[FUNNEL RECOVERY] email processing failed");
            // Azure Queue's existing bounded retry/poison behavior; never include
            // original exceptions that may contain recipient or request details.
            throw new InvalidOperationException("Funnel recovery processing failed.");
        }
    }
}
