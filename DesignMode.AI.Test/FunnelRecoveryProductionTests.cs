using Azure;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class FunnelRecoveryProductionTests
{
    private static Dictionary<string, string?> Production() => new()
    {
        ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Production",
        ["FUNNEL_RECOVERY_EMAIL_ENABLED"] = "TRUE",
        ["AWS_REGION"] = "us-east-1",
        ["SES_FROM_EMAIL"] = "DesignMode <sender@designmode.ai>",
        ["HostUrl"] = "https://www.designmode.ai",
        ["USE_LOCAL_DYNAMODB"] = "false",
        ["AzureWebJobsStorage"] = "test-only-connection-presence",
        ["JwtSecretKey"] = "test-only-signing-secret"
    };

    private static FunnelRecoveryConfiguration Configuration(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [TestMethod]
    public void ProductionConfiguration_ValidWithoutCredentialsOrExternalCalls()
    {
        var config = Configuration(Production());
        Assert.IsTrue(config.ValidateFunnelRecoveryConfiguration().CanSendConfigurationValid);
        Assert.IsFalse(config.AllowLocalLinks);
        Assert.IsFalse(FunnelRecoveryLocalSetup.CanPrepare(config));
        Assert.AreEqual("us-east-1", config.Get("AWS_REGION"));
        Assert.AreEqual("DesignMode <sender@designmode.ai>", config.Get("SES_FROM_EMAIL"));
    }

    [DataTestMethod]
    [DataRow("FUNNEL_RECOVERY_EMAIL_ENABLED")]
    [DataRow("AWS_REGION")]
    [DataRow("SES_FROM_EMAIL")]
    [DataRow("HostUrl")]
    [DataRow("AzureWebJobsStorage")]
    [DataRow("JwtSecretKey")]
    public void MissingRequiredSetting_InvalidWithoutSending(string name)
    {
        var values = Production();
        values.Remove(name);
        Assert.IsFalse(Configuration(values).ValidateFunnelRecoveryConfiguration().CanSendConfigurationValid);
    }

    [TestMethod]
    public void RootSettings_OverrideFunctionsValuesIncludingExplicitDisable()
    {
        var values = Production();
        values["Values:FUNNEL_RECOVERY_EMAIL_ENABLED"] = "true";
        values["FUNNEL_RECOVERY_EMAIL_ENABLED"] = "false";
        values["Values:AWS_REGION"] = "eu-west-1";
        var config = Configuration(values);
        Assert.IsFalse(config.ValidateFunnelRecoveryConfiguration().RecoveryEnabled);
        Assert.AreEqual("us-east-1", config.Get("AWS_REGION"));
    }

    [TestMethod]
    public async Task MissingProductionQueue_LogsActionableCategoryWithoutPrivateException()
    {
        var log = new CaptureLogger<FunnelRecoveryPublisher>();
        var calls = 0;
        var publisher = new FunnelRecoveryPublisher(log, () => true, _ =>
        {
            calls++;
            throw new RequestFailedException(404, "private-provider-payload", "QueueNotFound", null);
        });
        await publisher.PublishSafelyAsync("private-job-id");
        Assert.AreEqual(1, calls);
        StringAssert.Contains(string.Join(" ", log.Messages), "category=QUEUE_NOT_FOUND");
        Assert.IsFalse(log.Messages.Any(message => message.Contains("private")));
        Assert.IsFalse(log.Exceptions.Any(exception => exception is not null));
    }

    [TestMethod]
    public async Task ProductionStartup_ReportsConfigurationWithoutProvisioning()
    {
        var log = new CaptureLogger<FunnelRecoveryLocalSetup>();
        var calls = 0;
        await new FunnelRecoveryLocalSetup(Configuration(Production()), log,
            _ => { calls++; return Task.CompletedTask; }).StartAsync(default);
        Assert.AreEqual(0, calls);
        var output = string.Join(" ", log.Messages);
        StringAssert.Contains(output, "infrastructure-verified=false");
        Assert.IsFalse(output.Contains("test-only"));
        Assert.IsFalse(output.Contains("sender@"));
        Assert.IsFalse(output.Contains("https://"));
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
