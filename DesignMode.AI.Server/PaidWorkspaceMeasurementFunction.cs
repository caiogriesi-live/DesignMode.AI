using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace DesignMode.AI.Server;

public sealed class PaidWorkspaceMeasurementFunction(AttributionStore clients, JwtUserResolver auth)
{
    [Function("PaidWorkspaceMeasurement")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "measurement/paid-workspace")] HttpRequest request)
    {
        var userId = auth.ResolveUserId(request);
        if (string.IsNullOrWhiteSpace(userId)) return new UnauthorizedResult();
        using var db = clients.CreateClient(); Dictionary<string, AttributeValue>? key = null;
        do
        {
            var page = await db.ScanAsync(new ScanRequest { TableName = "Purchases", ExclusiveStartKey = key,
                FilterExpression = "UserId = :user AND PaymentStatus = :paid", ProjectionExpression = "Id",
                ExpressionAttributeValues = new() { [":user"] = new() { S = userId }, [":paid"] = new() { S = "Paid" } } });
            if (page.Items is { Count: > 0 }) return new OkObjectResult(true);
            key = page.LastEvaluatedKey;
        } while (key is { Count: > 0 });
        return new OkObjectResult(false);
    }
}
