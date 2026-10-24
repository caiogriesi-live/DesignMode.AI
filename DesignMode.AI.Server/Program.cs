using DesignMode.AI.Business;
using DesignMode.AI.Data.Data;
using DesignMode.AI.Shared.Interfaces.Business;
using DesignMode.AI.Shared.Security;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using DesignMode.AI.Services.OpenAI;
using DesignMode.AI.Services.OpenAI.Models.Settings;
using DesignMode.AI.Shared.Models.Settings;
using DesignMode.AI.Shared.Interfaces.Data;
using DesignMode.AI.Data;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Server.Security;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// `dotnet run` does not have Core Tools injecting local.settings.json into process environment.
// Load the Functions-specific Values object explicitly; production remains environment-backed because the file is absent there.
builder.Configuration.AddFunctionsLocalSettingsValues(Path.Combine(builder.Environment.ContentRootPath, "local.settings.json"));

// Load configuration
var configuration = builder.Configuration;

ConfigureServices(builder.Services, configuration);

// Validate routing before accepting requests, without contacting DynamoDB.
var dynamoSelection = DynamoDbClientFactory.Resolve();
var host = builder.Build();
DynamoDbClientFactory.LogSelection(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DynamoDB"), dynamoSelection);
host.Services.LogAdsDeliveryConfiguration();
host.Run();

static void ConfigureServices(IServiceCollection services, IConfiguration config)
{
    // Add configuration as a singleton
    services.AddSingleton<IConfiguration>(config);

    services.AddSingleton<IOpenAIService, OpenAIService>(); // OpenAI API service
    services.AddSingleton<ITestString, TestString>();       // Business layer service
    services.AddSingleton<ITestDac, TestDac>();
    services.AddSingleton<IConnectionManager, ConnectionManager>();
    services.Configure<OpenAISettings>(options =>
    {
        options.BaseUrl = Environment.GetEnvironmentVariable("OpenAISettings:BaseUrl");
        options.ApiKey = Environment.GetEnvironmentVariable("OpenAISettings:ApiKey");
        options.Model = Environment.GetEnvironmentVariable("OpenAISettings:Model");
        options.MaxTokens = int.TryParse(Environment.GetEnvironmentVariable("OpenAISettings:MaxTokens"), out int tokens) ? tokens : 4096;
    });
    services.Configure<ConectionStringSettings>(options =>
    {
        options.Value = Environment.GetEnvironmentVariable("DesignModeAIConnectionString");
    });

    services.AddBlazoredLocalStorage();
    services.AddAuthorizationCore();
    services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();
    services.AddScoped<AuthService>();
    services.AddSingleton<AttributionStore>();
    services.AddSingleton<CanonicalUserStore>();
    services.AddSingleton<AccountSubscriptionService>();
    services.AddSingleton<FunnelRecoveryConfiguration>();
    services.AddHostedService<FunnelRecoveryLocalSetup>();
    services.AddSingleton<DesignMode.AI.Server.FunnelRecoveryPublisher>();
    services.AddSingleton<IFunnelRecoveryStore, FunnelRecoveryStore>();
    services.AddSingleton<IFunnelRecoverySequenceStore>(provider => (FunnelRecoveryStore)provider.GetRequiredService<IFunnelRecoveryStore>());
    services.AddSingleton<FunnelRecoveryUnsubscribe>();
    services.AddSingleton<IFunnelRecoveryMarketingPermission>(provider => provider.GetRequiredService<FunnelRecoveryUnsubscribe>());
    services.AddSingleton<FunnelRecoverySequence>();
    services.AddSingleton<IFunnelRecoveryEmailSender, SesFunnelRecoveryEmailSender>();
    services.AddSingleton<FunnelRecoveryService>();
    services.AddSingleton<DesignMode.AI.Shared.Navigation.IPostAuthRoutingStore, PostAuthRoutingStore>();
services.AddSingleton<AIGenerationUsageStore>();
services.AddSingleton<AIUsageRecorder>();
services.AddSingleton<AIProviderPricingService>();
    services.AddAIUsageQuotaOptions(config);
    services.AddSingleton<AIUsageSummaryService>();
    services.AddSingleton<AICreditPurchaseOptions>();
    services.AddSingleton<AICreditLedger>(sp => new AICreditLedger(sp.GetRequiredService<AttributionStore>())
    {
        TableName = (config["AI_CREDITS_MODE"] ?? config["Values:AI_CREDITS_MODE"]) == "Test" ? "AICreditLedgerTest" : AICreditLedger.Table,
        Enabled = bool.TryParse(config["AI_CREDITS_LEDGER_ENABLED"] ?? config["Values:AI_CREDITS_LEDGER_ENABLED"], out var enabled) && enabled
    });
    services.AddSingleton<StripeCreditPurchaseHandler>();
    services.AddSingleton<JwtUserResolver>();
    services.AddHostedService<StorageQueueInitializer>();
    services.AddSingleton<PurchaseMeasurementStore>();
    services.AddAdsDeliveryOptions(config);
    services.AddSingleton<IAdsDeliveryStore, AdsDeliveryStore>();
    services.AddSingleton<AdsEligibilityService>();
    services.AddSingleton<AdsVerifiedProvenance>();
    services.AddSingleton<IStripeAccountVerifier, StripeAccountVerifier>();
    services.AddSingleton<AdsDryRunService>();
    services.AddSingleton<StripeVerifiedPurchaseHandler>();
    services.AddSingleton<IStripeVerifiedPaymentResolver, StripeVerifiedPaymentResolver>();
    services.AddSingleton(GoogleMeasurementOptions.FromEnvironment());
    services.AddSingleton<IGoogleConversionTransport>(sp => new GoogleConversionTransport(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, sp.GetRequiredService<GoogleMeasurementOptions>()));
    var googleAdsReportingOptions = GoogleAdsReportingOptions.FromEnvironment();
    services.AddSingleton(googleAdsReportingOptions);
    services.AddSingleton<IGoogleAdsReportingTransport>(sp => new GoogleAdsReportingHttpTransport(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, sp.GetRequiredService<GoogleAdsReportingOptions>()));
    services.AddSingleton<GoogleAdsReportingSource>();
    services.AddSingleton<IGoogleAdsCampaignReportingTransport>(sp => (IGoogleAdsCampaignReportingTransport)sp.GetRequiredService<IGoogleAdsReportingTransport>());
    services.AddSingleton(CampaignBudgetRecommendationOptions.FromEnvironment());
    services.AddSingleton<GoogleAdsCampaignReportingSource>();
    services.AddSingleton<IPurchaseReportingReader, DynamoPurchaseReportingReader>();
    services.AddSingleton<IPurchaseClickProvenanceReader, VerifiedPurchaseClickProvenanceReader>();
    services.AddSingleton<PurchaseReportingSource>();
    services.AddSingleton<IBusinessExpenseReportingReader, DynamoBusinessExpenseReportingReader>();
    services.AddSingleton<MarketingDashboardReportService>();
    services.AddSingleton<PurchaseConversionDispatcher>();
}
