using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Server.Services;

public sealed class FunnelRecoveryConfiguration(IConfiguration configuration)
{
    // Core Tools exposes Values as environment settings; dotnet run loads the
    // same file under Values through AddFunctionsLocalSettingsValues.
    public string? Get(string name) => configuration[name] ?? configuration[$"Values:{name}"];

    public bool Enabled => bool.TryParse(Get("FUNNEL_RECOVERY_EMAIL_ENABLED"), out var enabled) && enabled;
    public string? HostUrl => Get("HostUrl");
    public bool LocalTestMode => bool.TryParse(Get("FUNNEL_RECOVERY_LOCAL_TEST_ENABLED"), out var enabled) && enabled;
    public string? LocalTestInbox => Get("FUNNEL_RECOVERY_LOCAL_TEST_EMAIL");

    // Pure configuration check: never resolves credentials, contacts infrastructure,
    // reads customers, or sends mail. Presence does not prove queue/table existence.
    public FunnelRecoveryConfigurationValidation ValidateFunnelRecoveryConfiguration()
    {
        var region = Get("AWS_REGION");
        var regionConfigured = !string.IsNullOrWhiteSpace(region) &&
            System.Text.RegularExpressions.Regex.IsMatch(region, @"^[a-z]{2}(?:-[a-z]+)+-\d+$");
        var senderConfigured = System.Net.Mail.MailAddress.TryCreate(Get("SES_FROM_EMAIL"), out _);
        var publicUrlConfigured = false;
        try
        {
            _ = FunnelRecoveryEmail.Create(HostUrl, null, AllowLocalLinks);
            publicUrlConfigured = true;
        }
        catch (InvalidOperationException) { }
        var storeConfigured = false;
        try { _ = DynamoDbClientFactory.Resolve(Get); storeConfigured = true; }
        catch (InvalidOperationException) { }
        return new(Enabled, regionConfigured, senderConfigured, publicUrlConfigured, storeConfigured,
            !string.IsNullOrWhiteSpace(Get("AzureWebJobsStorage")),
            !string.IsNullOrWhiteSpace(Get("JwtSecretKey")));
    }

    public bool CanSendLocalTestReminder(string email, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        if (!LocalTestMode || !AllowLocalLinks ||
            !string.IsNullOrWhiteSpace(readEnvironment("WEBSITE_INSTANCE_ID")) ||
            !string.IsNullOrWhiteSpace(readEnvironment("WEBSITE_SITE_NAME")) ||
            Get("AzureWebJobsStorage") != "UseDevelopmentStorage=true" ||
            !System.Net.Mail.MailAddress.TryCreate(LocalTestInbox, out var inbox) ||
            inbox.Address != LocalTestInbox ||
            !string.Equals(email, LocalTestInbox, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            // Check both configuration and the actual connection factory's environment.
            // A local worker with production database credentials must not bypass permission.
            var configured = DynamoDbClientFactory.Resolve(Get);
            var actual = DynamoDbClientFactory.Resolve(readEnvironment);
            return configured.IsLocal && actual.IsLocal &&
                new Uri(configured.Endpoint!).IsLoopback && new Uri(actual.Endpoint!).IsLoopback &&
                Uri.TryCreate(HostUrl, UriKind.Absolute, out var host) && host.IsLoopback &&
                host.Scheme is "http" or "https";
        }
        catch (InvalidOperationException) { return false; }
    }
    public FunnelRecoveryTiming Timing => new(
        Delay("FUNNEL_RECOVERY_REMINDER1_DELAY_HOURS", 24),
        Delay("FUNNEL_RECOVERY_REMINDER2_DELAY_HOURS", 72),
        Delay("FUNNEL_RECOVERY_FINAL_DELAY_HOURS", 168));

    private TimeSpan Delay(string name, int fallback)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromHours(fallback);
        if (!int.TryParse(value, out var hours) || hours < 1 || hours > 8760)
            throw new InvalidOperationException("Invalid recovery delay configuration.");
        return TimeSpan.FromHours(hours);
    }
    public bool AllowLocalLinks =>
        string.Equals(Get("AZURE_FUNCTIONS_ENVIRONMENT") ?? Get("ASPNETCORE_ENVIRONMENT") ?? Get("DOTNET_ENVIRONMENT"),
            "Development", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(Get("WEBSITE_INSTANCE_ID")) &&
        string.IsNullOrWhiteSpace(Get("WEBSITE_SITE_NAME"));
}

public sealed record FunnelRecoveryConfigurationValidation(bool RecoveryEnabled, bool SesRegionConfigured,
    bool SenderConfigured, bool PublicBaseUrlConfigured, bool RecoveryStoreConfigured,
    bool QueueConnectionConfigured, bool UnsubscribeSigningConfigured)
{
    public bool CanSendConfigurationValid => RecoveryEnabled && SesRegionConfigured && SenderConfigured &&
        PublicBaseUrlConfigured && RecoveryStoreConfigured && QueueConnectionConfigured && UnsubscribeSigningConfigured;
}
