using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

public sealed class StorageQueueInitializer(
    IConfiguration configuration,
    ILogger<StorageQueueInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration["AzureWebJobsStorage"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("[VIDEO QUEUE] AzureWebJobsStorage is not configured; queue initialization was skipped.");
            return;
        }

        logger.LogInformation("[VIDEO QUEUE] producer storage={Storage}", VideoQueuePipeline.DescribeStorage(connectionString));

        try
        {
            foreach (var queueName in VideoQueuePipeline.QueueNames)
            {
                var queue = new QueueClient(connectionString, queueName, VideoQueuePipeline.CreateClientOptions());
                await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                logger.LogInformation("[VIDEO QUEUE] queue={Queue} ready", queueName);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[VIDEO QUEUE] unable to initialize video pipeline queues.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
