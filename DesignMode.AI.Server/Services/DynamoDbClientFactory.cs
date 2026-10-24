using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace DesignMode.AI.Server.Services;

/// <summary>Single connection policy for every application DynamoDB access path.</summary>
public static class DynamoDbClientFactory
{
    public sealed record Selection(bool IsLocal, string Region, string? Endpoint)
    {
        public AmazonDynamoDBConfig CreateConfig() => IsLocal
            ? new() { ServiceURL = Endpoint, AuthenticationRegion = Region, IgnoreConfiguredEndpointUrls = true }
            : new() { RegionEndpoint = RegionEndpoint.GetBySystemName(Region), IgnoreConfiguredEndpointUrls = true };
    }

    // Pure selection: no credential resolution, network access, or database operations.
    public static Selection Resolve(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        bool local = string.Equals(read("USE_LOCAL_DYNAMODB"), "true", StringComparison.OrdinalIgnoreCase);
        string? region = read("AWS_REGION")?.Trim();
        if (string.IsNullOrWhiteSpace(region))
        {
            if (!local) throw new InvalidOperationException("DynamoDB AWS mode requires an explicit AWS_REGION; local fallback is disabled.");
            region = "us-east-1";
        }
        if (!Regex.IsMatch(region, @"^[a-z]{2}(?:-[a-z]+)+-\d+$"))
            throw new InvalidOperationException("DynamoDB AWS_REGION must be an AWS region name.");

        if (!local)
        {
            if (read("AWS_ACCESS_KEY_ID") == "fakeMyKeyId" || read("AWS_SECRET_ACCESS_KEY") == "fakeSecretAccessKey")
                throw new InvalidOperationException("DynamoDB AWS mode cannot use DynamoDB Local test credentials. Configure the AWS credential provider chain.");
            // Intentionally do not read LOCAL_DYNAMODB_URL in AWS mode.
            return new(false, region, null);
        }

        string? endpoint = read("LOCAL_DYNAMODB_URL");
        endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:8000" : endpoint.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("LOCAL_DYNAMODB_URL must be an absolute HTTP(S) endpoint without credentials, query, or fragment.");
        return new(true, region, endpoint);
    }

    public static AmazonDynamoDBClient CreateClient()
    {
        var selection = Resolve();
        return selection.IsLocal
            ? new AmazonDynamoDBClient(new BasicAWSCredentials("fakeMyKeyId", "fakeSecretAccessKey"), selection.CreateConfig())
            : new AmazonDynamoDBClient(selection.CreateConfig()); // Standard SDK credential chain, including session tokens.
    }

    public static void LogSelection(ILogger logger, Selection selection)
    {
        if (selection.IsLocal) logger.LogInformation("DynamoDB mode: LOCAL; endpoint: {Endpoint}; region: {Region}", selection.Endpoint, selection.Region);
        else logger.LogInformation("DynamoDB mode: AWS; region: {Region}", selection.Region);
    }
}
