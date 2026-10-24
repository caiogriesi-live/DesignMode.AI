using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Azure.Storage.Queues;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server.Services;

// This only prepares empty local stores. It neither evaluates users nor sends email.
public sealed class FunnelRecoveryLocalSetup : IHostedService
{
    private readonly FunnelRecoveryConfiguration _configuration;
    private readonly ILogger<FunnelRecoveryLocalSetup> _logger;
    private readonly Func<CancellationToken, Task> _prepare;

    public FunnelRecoveryLocalSetup(FunnelRecoveryConfiguration configuration, ILogger<FunnelRecoveryLocalSetup> logger)
        : this(configuration, logger, PrepareAsync) { }

    public FunnelRecoveryLocalSetup(FunnelRecoveryConfiguration configuration, ILogger<FunnelRecoveryLocalSetup> logger,
        Func<CancellationToken, Task> prepare)
    {
        _configuration = configuration;
        _logger = logger;
        _prepare = prepare;
    }

    public static bool CanPrepare(FunnelRecoveryConfiguration configuration) =>
        configuration.Enabled && configuration.AllowLocalLinks &&
        bool.TryParse(configuration.Get("USE_LOCAL_DYNAMODB"), out var local) && local &&
        Uri.TryCreate(configuration.Get("LOCAL_DYNAMODB_URL") ?? "http://localhost:8000", UriKind.Absolute, out var endpoint) &&
        endpoint.IsLoopback && endpoint.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) &&
        string.Equals(configuration.Get("AzureWebJobsStorage")?.Trim(), "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var validation = _configuration.ValidateFunnelRecoveryConfiguration();
        _logger.LogInformation("[FUNNEL RECOVERY CONFIG] enabled={Enabled} ses-region-configured={Region} sender-configured={Sender} public-base-url-configured={PublicUrl} recovery-store-configured={Store} queue-connection-configured={Queue} unsubscribe-signing-configured={Signing} can-send-configuration-valid={Valid} infrastructure-verified=false",
            validation.RecoveryEnabled, validation.SesRegionConfigured, validation.SenderConfigured,
            validation.PublicBaseUrlConfigured, validation.RecoveryStoreConfigured,
            validation.QueueConnectionConfigured, validation.UnsubscribeSigningConfigured, validation.CanSendConfigurationValid);
        if (!CanPrepare(_configuration)) return;
        try
        {
            await _prepare(cancellationToken);
            _logger.LogInformation("[FUNNEL RECOVERY] local table and queues ready");
        }
        catch
        {
            _logger.LogWarning("[FUNNEL RECOVERY] local setup failed; start DynamoDB Local and Azurite, then restart the backend");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task PrepareAsync(CancellationToken cancellationToken)
    {
        // Use the same factory as the application and independently verify its
        // effective target before issuing any write, even if config sources differ.
        var selection = DynamoDbClientFactory.Resolve();
        if (!selection.IsLocal || !Uri.TryCreate(selection.Endpoint, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
            throw new InvalidOperationException("Local recovery setup requires the canonical local database.");
        using var client = DynamoDbClientFactory.CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = FunnelRecoveryStore.TableName, BillingMode = BillingMode.PAY_PER_REQUEST,
                KeySchema = [new("Id", KeyType.HASH)], AttributeDefinitions = [new("Id", ScalarAttributeType.S)]
            }, timeout.Token);
        }
        catch (ResourceInUseException) { }

        var queueOptions = VideoQueuePipeline.CreateClientOptions();
        queueOptions.Retry.MaxRetries = 0;
        queueOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        foreach (var name in new[] { FunnelRecoveryPublisher.QueueName, FunnelRecoveryPublisher.QueueName + "-poison" })
        {
            var queue = new QueueClient("UseDevelopmentStorage=true", name, queueOptions);
            await queue.CreateIfNotExistsAsync(cancellationToken: timeout.Token);
        }
    }
}
