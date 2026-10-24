using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace DesignMode.AI.Server.Services;

public enum AdsDeliveryMode { Off, DryRun, Canary, Enabled }
public enum AdsDeliveryStatus { Pending, Eligible, Accepted, RetryScheduled, Submitted, NotEligible, Expired, Rejected, NeedsReview }
public sealed record AdsEligibilityResult(AdsDeliveryStatus Status, string Reason, string? ClickType = null);

// No credentials: Stage 1 has no transport, even in Canary/Enabled mode.
public sealed record AdsDeliveryOptions
{
    public AdsDeliveryMode Mode { get; init; }
    public bool Valid { get; init; }
    public bool CanCaptureSidecar { get; init; }
    public bool CanEvaluateDryRun { get; init; }
    public bool CanDeliverToGoogle { get; init; }
    public IReadOnlyList<string> ValidationFailures { get; init; } = [];
    public string EligibilityVersion { get; init; } = "";
    public DateTimeOffset ActivationCutoff { get; init; }
    public string DeploymentEnvironment { get; init; } = "";
    public string MeasurementEnvironment { get; init; } = "";
    public string CustomerId { get; init; } = "";
    public string ConversionActionId { get; init; } = "";
    public string ExpectedStripeAccountId { get; init; } = "";
    public bool LocalDatabase { get; init; } = true;
    public string ConfigurationSource { get; init; } = "missing";
    public bool CutoffPresent { get; init; }
    public int CutoffLength { get; init; }
    public bool CutoffParseable { get; init; }
    public bool MeasurementLedgerEnabled { get; init; }
    public static AdsDeliveryOptions FromConfiguration(IConfiguration configuration, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        static string? FirstValue(params string?[] candidates) =>
            candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? candidates.FirstOrDefault(value => value is not null);
        var environmentCutoff = readEnvironment("ADS_ACTIVATION_CUTOFF");
        var directCutoff = configuration["ADS_ACTIVATION_CUTOFF"];
        var nestedCutoff = configuration["Values:ADS_ACTIVATION_CUTOFF"];
        var localValues = string.Equals(configuration["Values:ADS_MEASUREMENT_ENVIRONMENT"], "Local", StringComparison.OrdinalIgnoreCase)
            && string.Equals(configuration["Values:USE_LOCAL_DYNAMODB"], "true", StringComparison.OrdinalIgnoreCase);
        // Core Tools may project a timezone-qualified local JSON timestamp into the worker environment without its suffix.
        // The explicit Functions Values entry is authoritative only for a confirmed local-DynamoDB configuration.
        var selectedCutoff = localValues && nestedCutoff is not null
            ? nestedCutoff : FirstValue(environmentCutoff, directCutoff, nestedCutoff);
        var source = localValues && nestedCutoff is not null ? "configuration"
            : !string.IsNullOrWhiteSpace(environmentCutoff) && selectedCutoff == environmentCutoff ? "environment"
            : selectedCutoff is not null ? "configuration" : "missing";
        string? Read(string key) => key == "ADS_ACTIVATION_CUTOFF" ? selectedCutoff
            : FirstValue(readEnvironment(key), configuration[key], configuration[$"Values:{key}"]);
        var rawCutoff = Read("ADS_ACTIVATION_CUTOFF");
        return FromEnvironment(Read) with
        {
            ConfigurationSource = source,
            CutoffPresent = rawCutoff is not null,
            CutoffLength = rawCutoff?.Length ?? 0,
            CutoffParseable = TryParseActivationCutoff(rawCutoff, out _),
            MeasurementLedgerEnabled = string.Equals(Read("MEASUREMENT_LEDGER_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)
        };
    }

    public static AdsDeliveryOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        string Get(string key) => (read(key) ?? "").Trim();
        var modeText = Get("ADS_DELIVERY_MODE");
        var mode = modeText.ToLowerInvariant() switch
        { "dry-run" => AdsDeliveryMode.DryRun, "canary" => AdsDeliveryMode.Canary, "enabled" => AdsDeliveryMode.Enabled, _ => AdsDeliveryMode.Off };
        var modeValid = new[] { "", "off", "dry-run", "canary", "enabled" }.Contains(modeText.ToLowerInvariant());
        var eligibilityVersion = Get("ADS_ELIGIBILITY_VERSION");
        var versionValid = Regex.IsMatch(eligibilityVersion, @"\A[a-zA-Z0-9._-]{1,64}\z");
        var destinationsValid = Regex.IsMatch(Get("ADS_DESTINATION_CUSTOMER_ID"), @"\A[0-9]{10}\z")
            && Regex.IsMatch(Get("ADS_DESTINATION_CONVERSION_ACTION_ID"), @"\A[0-9]{1,20}\z");
        var explicitlyLocal = string.Equals(Get("USE_LOCAL_DYNAMODB"), "true", StringComparison.OrdinalIgnoreCase);
        var measurementEnvironment = Get("ADS_MEASUREMENT_ENVIRONMENT");
        // Local dry-run may capture inspectable provenance before a real Google account exists.
        // Canary/enabled and every nonlocal environment still require real-shaped pinned destinations.
        var localDryRun = mode == AdsDeliveryMode.DryRun && explicitlyLocal
            && string.Equals(measurementEnvironment, "Local", StringComparison.OrdinalIgnoreCase);
        // Require an explicit timezone; never interpret a cutoff using the host's local timezone.
        var timestamp = Get("ADS_ACTIVATION_CUTOFF");
        var timeValid = TryParseActivationCutoff(timestamp, out var activationCutoff);
        var canCaptureSidecar = mode != AdsDeliveryMode.Off && modeValid && versionValid && timeValid
            && (destinationsValid || localDryRun);
        var failures = new List<string>();
        if (!modeValid) failures.Add("INVALID_DELIVERY_MODE");
        if (!versionValid) failures.Add("MISSING_ELIGIBILITY_VERSION");
        if (!timeValid) failures.Add("INVALID_ACTIVATION_CUTOFF");
        if (mode != AdsDeliveryMode.Off && !destinationsValid && !localDryRun)
            failures.Add(explicitlyLocal ? "LOCAL_DATABASE_CONFIGURATION_MISMATCH" : "DESTINATION_REQUIRED_FOR_PRODUCTION");
        var production = !explicitlyLocal
            && string.Equals(measurementEnvironment, "Production", StringComparison.Ordinal)
            && string.Equals(Get("AZURE_FUNCTIONS_ENVIRONMENT"), "Production", StringComparison.Ordinal);
        return new()
        {
            Mode = mode, Valid = canCaptureSidecar, CanCaptureSidecar = canCaptureSidecar,
            CanEvaluateDryRun = canCaptureSidecar && mode == AdsDeliveryMode.DryRun,
            CanDeliverToGoogle = canCaptureSidecar && destinationsValid && production
                && mode is AdsDeliveryMode.Canary or AdsDeliveryMode.Enabled,
            ValidationFailures = failures,
            ActivationCutoff = timeValid ? activationCutoff : default,
            EligibilityVersion = eligibilityVersion, CustomerId = Get("ADS_DESTINATION_CUSTOMER_ID"),
            ConversionActionId = Get("ADS_DESTINATION_CONVERSION_ACTION_ID"),
            DeploymentEnvironment = Get("AZURE_FUNCTIONS_ENVIRONMENT"), MeasurementEnvironment = measurementEnvironment,
            ExpectedStripeAccountId = Get("ADS_EXPECTED_STRIPE_ACCOUNT_ID"),
            // Require an explicit AWS selection for Ads; omission fails closed independently of the shared DB factory.
            LocalDatabase = !string.Equals(Get("USE_LOCAL_DYNAMODB"), "false", StringComparison.OrdinalIgnoreCase)
        };
    }

    public static bool TryParseActivationCutoff(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)
            || !Regex.IsMatch(normalized, @"(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal, out var parsed)) return false;
        timestamp = parsed.ToUniversalTime();
        return true;
    }
}

public static class AdsDeliveryRuntimeRegistration
{
    public static IConfigurationBuilder AddFunctionsLocalSettingsValues(this IConfigurationBuilder builder, string path)
    {
        if (!File.Exists(path)) return builder;
        using var stream = File.OpenRead(path);
        return builder.AddFunctionsLocalSettingsValues(stream);
    }

    public static IConfigurationBuilder AddFunctionsLocalSettingsValues(this IConfigurationBuilder builder, Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Object) return builder;
        var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Azure Functions local settings use flat scalar values. Last occurrence matches common JSON/runtime behavior.
        foreach (var property in values.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                entries[$"Values:{property.Name}"] = property.Value.ToString();
        builder.AddInMemoryCollection(entries);
        return builder;
    }

    // The single runtime construction path used by Program and host-level tests.
    public static IServiceCollection AddAdsDeliveryOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => AdsDeliveryOptions.FromConfiguration(configuration));
        return services;
    }

    public static void LogAdsDeliveryConfiguration(this IServiceProvider services)
    {
        var options = services.GetRequiredService<AdsDeliveryOptions>();
        // Console is intentional for temporary pre-host diagnostics: ILogger providers start later in the Functions lifecycle.
        Console.WriteLine($"[ADS CONFIG] cutoff present={options.CutoffPresent.ToString().ToLowerInvariant()}");
        Console.WriteLine($"[ADS CONFIG] cutoff length={options.CutoffLength}");
        Console.WriteLine($"[ADS CONFIG] cutoff parseable={options.CutoffParseable.ToString().ToLowerInvariant()}");
        if (options.CutoffParseable) Console.WriteLine($"[ADS CONFIG] cutoff utc={options.ActivationCutoff.ToString("O", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"[ADS CONFIG] source={options.ConfigurationSource}");
        Console.WriteLine($"[ADS CONFIG] mode={options.Mode}");
        Console.WriteLine($"[ADS CONFIG] measurementEnvironment={options.MeasurementEnvironment}");
        Console.WriteLine($"[ADS CONFIG] localDatabase={options.LocalDatabase.ToString().ToLowerInvariant()}");
        Console.WriteLine($"[ADS CONFIG] canCaptureSidecar={options.CanCaptureSidecar.ToString().ToLowerInvariant()}");
        Console.WriteLine($"[ADS CONFIG] validationReasons={(options.ValidationFailures.Count == 0 ? "none" : string.Join(",", options.ValidationFailures))}");
    }
}

// Delivery/provenance only. Purchase facts, click IDs and customer data remain in the canonical ledger/snapshot.
public sealed record AdsDeliveryRecord
{
    public string PurchaseTransactionId { get; init; } = "";
    public AdsDeliveryStatus AdsStatus { get; init; } = AdsDeliveryStatus.Pending;
    public string AdsEligibilityReason { get; init; } = "NOT_EVALUATED";
    public string AdsEligibilityVersion { get; init; } = "";
    public string AdsTransportVersion { get; init; } = "data-manager-v1-not-implemented";
    public bool? StripeLivemode { get; init; }
    public bool? StripeAccountVerified { get; init; }
    public string StripeAccountVerificationMethod { get; init; } = "";
    public string DeploymentEnvironment { get; init; } = "";
    public string MeasurementEnvironment { get; init; } = "";
    public bool LocalDatabase { get; init; } = true;
    public string ProvenanceVersion { get; init; } = "";
    public DateTimeOffset ProvenanceCapturedAt { get; init; }
    public string DestinationCustomerId { get; init; } = "";
    public string DestinationConversionActionId { get; init; } = "";
    public int AdsAttempts { get; init; }
    public DateTimeOffset? AdsLastAttemptAt { get; init; }
    public DateTimeOffset? AdsNextAttemptAt { get; init; }
    public string? AdsLastErrorCode { get; init; }
    public string? AdsLastErrorCategory { get; init; }
    public string? AdsRequestId { get; init; }
    public string? AdsPayloadHash { get; init; }
    public string? AdsLeaseOwner { get; init; }
    public DateTimeOffset? AdsLeaseUntil { get; init; }
    public DateTimeOffset? AdsLastDiagnosticAt { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
