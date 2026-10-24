using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryLocalTests
{
    [DataTestMethod]
    [DataRow("https://localhost:7178")]
    [DataRow("http://localhost:5276")]
    [DataRow("http://127.0.0.1:5276")]
    [DataRow("http://[::1]:5276")]
    public void LocalDevelopment_PreservesClientOriginAndPort(string origin)
    {
        var email = FunnelRecoveryEmail.Create(origin, "/init/mainconcept?id=owned", true);
        Assert.AreEqual($"{origin}/login?returnUrl=%2Finit%2Fmainconcept%3Fid%3Downed", email.ResumeUrl);
        StringAssert.Contains(email.Text, email.ResumeUrl);
        Assert.ThrowsException<InvalidOperationException>(() => FunnelRecoveryEmail.Create(origin, null));
    }

    [DataTestMethod]
    [DataRow("http://example.test")]
    [DataRow("http://localhost.evil.test:5276")]
    [DataRow("http://192.168.1.10:5276")]
    [DataRow("ftp://localhost:5276")]
    [DataRow("http://user@localhost:5276")]
    [DataRow("http://localhost:5276/redirect")]
    [DataRow("http://localhost:5276?next=https://evil.test")]
    public void LocalDevelopment_DoesNotRelaxNonLoopbackOrOriginValidation(string origin) =>
        Assert.ThrowsException<InvalidOperationException>(() => FunnelRecoveryEmail.Create(origin, null, true));

    [TestMethod]
    public void LocalSettings_ReuseExistingSesAndHostConfiguration()
    {
        var config = Settings(new()
        {
            ["Values:AZURE_FUNCTIONS_ENVIRONMENT"] = "Development",
            ["Values:FUNNEL_RECOVERY_EMAIL_ENABLED"] = "true",
            ["Values:HostUrl"] = "https://localhost:7178",
            ["Values:SES_FROM_EMAIL"] = "sender@example.test",
            ["Values:AWS_REGION"] = "us-east-1",
            ["Values:AWS_ACCESS_KEY_ID"] = "test-only-key",
            ["Values:AWS_SECRET_ACCESS_KEY"] = "test-only-secret",
            ["Values:AzureWebJobsStorage"] = "UseDevelopmentStorage=true"
        });
        Assert.IsTrue(config.Enabled);
        Assert.IsTrue(config.AllowLocalLinks);
        Assert.AreEqual("https://localhost:7178", config.HostUrl);
        Assert.AreEqual("sender@example.test", config.Get("SES_FROM_EMAIL"));
        Assert.AreEqual("us-east-1", config.Get("AWS_REGION"));
        Assert.AreEqual("test-only-key", config.Get("AWS_ACCESS_KEY_ID"));
        Assert.AreEqual("test-only-secret", config.Get("AWS_SECRET_ACCESS_KEY"));
        Assert.AreEqual("UseDevelopmentStorage=true", config.Get("AzureWebJobsStorage"));
    }

    [TestMethod]
    public void EnvironmentSettings_OverrideLocalValues()
    {
        var config = Settings(new()
        {
            ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Production",
            ["Values:AZURE_FUNCTIONS_ENVIRONMENT"] = "Development",
            ["FUNNEL_RECOVERY_EMAIL_ENABLED"] = "false",
            ["Values:FUNNEL_RECOVERY_EMAIL_ENABLED"] = "true",
            ["HostUrl"] = "https://app.example.test",
            ["Values:HostUrl"] = "https://localhost:7178"
        });
        Assert.IsFalse(config.AllowLocalLinks);
        Assert.IsFalse(config.Enabled);
        Assert.AreEqual("https://app.example.test", config.HostUrl);
    }

    [DataTestMethod]
    [DataRow("WEBSITE_INSTANCE_ID")]
    [DataRow("WEBSITE_SITE_NAME")]
    public void AzureHostedDevelopment_StillRejectsLoopback(string hostMarker)
    {
        var config = Settings(new() { ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development", [hostMarker] = "hosted" });
        Assert.IsFalse(config.AllowLocalLinks);
    }

    [TestMethod]
    public void MissingConfiguration_RemainsDisabledAndProductionSafe()
    {
        var config = Settings(new());
        Assert.IsFalse(config.Enabled);
        Assert.IsFalse(config.AllowLocalLinks);
    }

    [DataTestMethod]
    [DataRow("FUNNEL_RECOVERY_EMAIL_ENABLED", "false")]
    [DataRow("AZURE_FUNCTIONS_ENVIRONMENT", "Production")]
    [DataRow("WEBSITE_INSTANCE_ID", "hosted")]
    [DataRow("USE_LOCAL_DYNAMODB", "false")]
    [DataRow("LOCAL_DYNAMODB_URL", "https://dynamodb.us-east-1.amazonaws.com")]
    [DataRow("LOCAL_DYNAMODB_URL", "http://user@localhost:8000")]
    [DataRow("AzureWebJobsStorage", "DefaultEndpointsProtocol=https;AccountName=remote")]
    public async Task LocalSetup_NeverProvisionsRemoteOrDisabledStores(string key, string value)
    {
        var values = LocalStorage();
        values[key] = value;
        var calls = 0;
        var setup = new FunnelRecoveryLocalSetup(Settings(values), NullLogger<FunnelRecoveryLocalSetup>.Instance,
            _ => { calls++; return Task.CompletedTask; });
        await setup.StartAsync(default);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task LocalSetup_PreparesStoresAndIsolatesFailures()
    {
        var calls = 0;
        var setup = new FunnelRecoveryLocalSetup(Settings(LocalStorage()), NullLogger<FunnelRecoveryLocalSetup>.Instance,
            _ => { calls++; throw new InvalidOperationException("local emulator unavailable"); });
        await setup.StartAsync(default);
        Assert.AreEqual(1, calls);
    }

    private static Dictionary<string, string?> LocalStorage() => new()
    {
        ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development", ["FUNNEL_RECOVERY_EMAIL_ENABLED"] = "true",
        ["USE_LOCAL_DYNAMODB"] = "true", ["LOCAL_DYNAMODB_URL"] = "http://localhost:8000",
        ["AzureWebJobsStorage"] = "UseDevelopmentStorage=true"
    };

    private static FunnelRecoveryConfiguration Settings(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
