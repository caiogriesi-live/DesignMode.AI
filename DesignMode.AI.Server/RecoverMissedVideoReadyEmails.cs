using DesignMode.AI.Server.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Server
{

    public sealed class RecoverMissedVideoReadyEmails(
        FunnelRecoveryService recovery,
        FunnelRecoveryConfiguration configuration,
        ILogger<RecoverMissedVideoReadyEmails> logger)
    {
        [Function("RecoverMissedVideoReadyEmails")]
        public async Task RunAsync(
            [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
            CancellationToken ct)
        {
            if (!configuration.Enabled)
                return;

            string tableName =
                Environment.GetEnvironmentVariable("DynamoDbTableName")
                ?? "DesignModeJobs";

            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);

            using var db = DynamoDbClientFactory.CreateClient();

            Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?
                lastKey = null;

            do
            {
                var result = await db.ScanAsync(
                    new Amazon.DynamoDBv2.Model.ScanRequest
                    {
                        TableName = tableName,
                        ExclusiveStartKey = lastKey,
                        FilterExpression =
                            "#status = :completed AND " +
                            "PreviewKind = :social AND " +
                            "CompletedAtUtc >= :cutoff",
                        ExpressionAttributeNames = new()
                        {
                            ["#status"] = "Status"
                        },
                        ExpressionAttributeValues = new()
                        {
                            [":completed"] = new() { S = "Completed" },
                            [":social"] = new() { S = "Social" },
                            [":cutoff"] = new() { S = cutoff.ToString("O") }
                        }
                    }, ct);

                foreach (var job in result.Items)
                {
                    ct.ThrowIfCancellationRequested();

                    string jobId =
                        job.GetValueOrDefault("JobId")?.S ?? "";

                    if (string.IsNullOrWhiteSpace(jobId) ||
                        string.IsNullOrWhiteSpace(
                            job.GetValueOrDefault("VideoUrl")?.S))
                        continue;

                    try
                    {
                        await recovery.ProcessAsync(
                            new FunnelVideoReadyEvent(
                                jobId,
                                DateTimeOffset.UtcNow),
                            true,
                            configuration.HostUrl,
                            configuration.AllowLocalLinks);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "[FUNNEL RECOVERY] Failed to recover video job {JobId}",
                            jobId);
                    }
                }

                lastKey = result.LastEvaluatedKey;
            }
            while (lastKey != null && lastKey.Count > 0);
        }
    }

}
