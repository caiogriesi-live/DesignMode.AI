using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryLocalDeliveryTests
{
    internal static Dictionary<string, string?> Settings() => new()
    {
        ["FUNNEL_RECOVERY_EMAIL_ENABLED"] = "true",
        ["FUNNEL_RECOVERY_LOCAL_TEST_ENABLED"] = "true",
        ["FUNNEL_RECOVERY_LOCAL_TEST_EMAIL"] = "canonical@example.test",
        ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Development",
        ["USE_LOCAL_DYNAMODB"] = "true",
        ["LOCAL_DYNAMODB_URL"] = "http://localhost:8000",
        ["AzureWebJobsStorage"] = "UseDevelopmentStorage=true",
        ["HostUrl"] = "http://localhost:5276"
    };

    [TestMethod]
    public void LocalTest_OnlyAllowsExactInbox()
    {
        var settings = Settings();
        var config = new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        Assert.IsTrue(config.CanSendLocalTestReminder("canonical@example.test", settings.GetValueOrDefault));
        Assert.IsFalse(config.CanSendLocalTestReminder("another@example.test", settings.GetValueOrDefault));
    }

    [DataTestMethod]
    [DataRow("FUNNEL_RECOVERY_LOCAL_TEST_ENABLED", "false")]
    [DataRow("FUNNEL_RECOVERY_LOCAL_TEST_EMAIL", "")]
    [DataRow("FUNNEL_RECOVERY_LOCAL_TEST_EMAIL", "Person <canonical@example.test>")]
    [DataRow("FUNNEL_RECOVERY_LOCAL_TEST_EMAIL", "canonical@example.test,other@example.test")]
    [DataRow("AZURE_FUNCTIONS_ENVIRONMENT", "Production")]
    [DataRow("WEBSITE_INSTANCE_ID", "hosted")]
    [DataRow("WEBSITE_SITE_NAME", "hosted")]
    [DataRow("USE_LOCAL_DYNAMODB", "false")]
    [DataRow("LOCAL_DYNAMODB_URL", "https://database.example.test")]
    [DataRow("AzureWebJobsStorage", "remote")]
    [DataRow("HostUrl", "https://app.example.test")]
    public void UnsafeConfiguration_CannotEnableTestDelivery(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;
        var config = new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        Assert.IsFalse(config.CanSendLocalTestReminder("canonical@example.test", settings.GetValueOrDefault));
    }

    [DataTestMethod]
    [DataRow("USE_LOCAL_DYNAMODB", "false")]
    [DataRow("LOCAL_DYNAMODB_URL", "https://database.example.test")]
    [DataRow("WEBSITE_INSTANCE_ID", "hosted")]
    [DataRow("WEBSITE_SITE_NAME", "hosted")]
    public void ActualRuntimeOverrides_CannotBypassLocalGuard(string key, string value)
    {
        var settings = Settings();
        var runtime = Settings();
        runtime[key] = value;
        var config = new FunnelRecoveryConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        Assert.IsFalse(config.CanSendLocalTestReminder("canonical@example.test", runtime.GetValueOrDefault));
    }
}
