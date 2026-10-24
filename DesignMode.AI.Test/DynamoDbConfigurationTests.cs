using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class DynamoDbConfigurationTests
{
    [TestMethod]
    [DoNotParallelize]
    public void Local_ClientConstructionNeedsNoAwsCredentials()
    {
        string[] names = ["USE_LOCAL_DYNAMODB", "LOCAL_DYNAMODB_URL", "AWS_REGION", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN"];
        var previous = names.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("USE_LOCAL_DYNAMODB", "TRUE");
            using var client = DynamoDbClientFactory.CreateClient();
            Assert.AreEqual("http://localhost:8000/", client.Config.ServiceURL);
            Assert.AreEqual("us-east-1", client.Config.AuthenticationRegion);
        }
        finally { foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]); }
    }

    [TestMethod]
    [DataRow("true", "http://localhost:8000", "us-east-1")]
    [DataRow("TRUE", null, null)]
    [DataRow("TrUe", "http://127.0.0.1:8123", "eu-west-1")]
    [DataRow("true", "", "")]
    public void Local_UsesOnlyExplicitLocalBranch(string flag, string? url, string? region)
    {
        var values = new Dictionary<string, string?> { ["USE_LOCAL_DYNAMODB"] = flag, ["LOCAL_DYNAMODB_URL"] = url, ["AWS_REGION"] = region };
        var result = DynamoDbClientFactory.Resolve(k => values.GetValueOrDefault(k));
        Assert.IsTrue(result.IsLocal);
        Assert.AreEqual(string.IsNullOrEmpty(url) ? "http://localhost:8000" : url, result.Endpoint);
        Assert.AreEqual(string.IsNullOrEmpty(region) ? "us-east-1" : region, result.CreateConfig().AuthenticationRegion);
    }

    [TestMethod]
    [DataRow("false")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("invalid")]
    [DataRow("1")]
    [DataRow(" true ")]
    public void Aws_NeverReadsLocalEndpoint(string? flag)
    {
        string? Read(string key) => key switch
        {
            "USE_LOCAL_DYNAMODB" => flag,
            "AWS_REGION" => "eu-west-1",
            "LOCAL_DYNAMODB_URL" => throw new AssertFailedException("AWS mode read local endpoint"),
            _ => null
        };
        var result = DynamoDbClientFactory.Resolve(Read);
        Assert.IsFalse(result.IsLocal);
        Assert.IsNull(result.Endpoint);
        var config = result.CreateConfig();
        Assert.IsTrue(string.IsNullOrEmpty(config.ServiceURL));
        Assert.AreEqual("eu-west-1", config.RegionEndpoint.SystemName);
        Assert.IsTrue(config.IgnoreConfiguredEndpointUrls);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("localhost")]
    public void Aws_MissingOrInvalidRegionFailsClosed(string? region)
    {
        Assert.ThrowsException<InvalidOperationException>(() => DynamoDbClientFactory.Resolve(k => k == "AWS_REGION" ? region : null));
    }

    [TestMethod]
    public void Aws_RejectsKnownLocalCredentials()
    {
        Assert.ThrowsException<InvalidOperationException>(() => DynamoDbClientFactory.Resolve(k => k switch
        {
            "AWS_REGION" => "us-east-1", "AWS_ACCESS_KEY_ID" => "fakeMyKeyId", _ => null
        }));
    }

    [TestMethod]
    public void Aws_ExplicitLocalUrlCannotInfluenceSelection()
    {
        var values = new Dictionary<string, string?>
        {
            ["USE_LOCAL_DYNAMODB"] = "false", ["AWS_REGION"] = "eu-west-1",
            ["LOCAL_DYNAMODB_URL"] = "http://localhost:8000", ["AWS_ENDPOINT_URL"] = "http://localhost:8000",
            ["AWS_ENDPOINT_URL_DYNAMODB"] = "http://localhost:8000"
        };
        var result = DynamoDbClientFactory.Resolve(k => values.GetValueOrDefault(k));
        Assert.IsFalse(result.IsLocal);
        Assert.IsNull(result.Endpoint);
        Assert.IsTrue(string.IsNullOrEmpty(result.CreateConfig().ServiceURL));
        Assert.IsTrue(result.CreateConfig().IgnoreConfiguredEndpointUrls);
    }

    [TestMethod]
    public void Local_RejectsEndpointsThatCouldLeakSecretsInDiagnostics()
    {
        Assert.ThrowsException<InvalidOperationException>(() => DynamoDbClientFactory.Resolve(k => k switch
        {
            "USE_LOCAL_DYNAMODB" => "true", "LOCAL_DYNAMODB_URL" => "http://user:password@localhost:8000", _ => null
        }));
    }

    [TestMethod]
    public void ApplicationClients_CannotBypassCanonicalFactory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DesignMode.AI.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        foreach (var project in Directory.GetDirectories(root.FullName, "DesignMode.AI.*"))
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "publish" or "publish-test") ||
                file.EndsWith("DynamoDbClientFactory.cs") || file.Contains("DesignMode.AI.Test")) continue;
            var source = File.ReadAllText(file);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(source, @"new\s+(?:Amazon\.DynamoDBv2\.)?AmazonDynamoDBClient\s*\("), file);
            Assert.IsFalse(source.Contains("new AmazonDynamoDBConfig"), file);
        }
    }
}

internal static class LocalDynamoDbTestSafety
{
    public static void RequireLoopback()
    {
        var selection = DynamoDbClientFactory.Resolve();
        Assert.IsTrue(selection.IsLocal && selection.Endpoint != null && new Uri(selection.Endpoint).IsLoopback,
            "Integration writes require USE_LOCAL_DYNAMODB=true and a loopback LOCAL_DYNAMODB_URL. AWS writes are forbidden.");
    }
}
