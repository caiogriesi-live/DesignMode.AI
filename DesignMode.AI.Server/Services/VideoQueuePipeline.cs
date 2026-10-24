using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace DesignMode.AI.Server.Services;

public static class VideoQueuePipeline
{
    public const string RequestQueueName = "api-requests";
    public const string PollQueueName = "runware-video-polls";
    public const string StandaloneVideoQueueName = "video-jobs";

    public static IReadOnlyList<string> QueueNames { get; } =
        [RequestQueueName, PollQueueName, StandaloneVideoQueueName];

    public static QueueClientOptions CreateClientOptions() => new()
    {
        MessageEncoding = QueueMessageEncoding.Base64
    };

    public static string Serialize<T>(T message) =>
        JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

    public static T? Deserialize<T>(string message) =>
        JsonSerializer.Deserialize<T>(message, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

    public static string DescribeStorage(string connectionString) =>
        connectionString.Trim().Equals("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)
            ? "local"
            : "configured";
}
