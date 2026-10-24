namespace DesignMode.AI.Shared.Models.Attribution;

// Google-issued identifiers only, never DesignMode visitor/session identifiers.
public sealed class MeasurementContext
{
    public bool AnalyticsConsent { get; set; }
    // Legacy persisted name: for policy-versioned NEW contexts this is internal measurement, not user consent.
    // Do not default to true here: deserializing historical data must preserve its original meaning.
    public bool AdvertisingConsent { get; set; }
    public string? AdvertisingMeasurementPolicyVersion { get; set; }
    public bool GoogleAdvertisingConsent { get; set; }
    public string? GaClientId { get; set; }
    public string? GaSessionId { get; set; }
}

public static class AdvertisingMeasurementPolicy
{
    // Effective prospectively when this release is deployed; never stamp historical snapshots on read.
    public const string Version = "internal-always-on-2026-08-31";
    public const string GooglePolicyVersion = "google-advertising-always-on-2026-09-01";
    public const bool AdvertisingMeasurementEnabled = true;
    public static bool IsCurrent(MeasurementContext value) => value.AdvertisingMeasurementPolicyVersion == Version;
    public static bool GoogleConsentGranted(MeasurementContext value) => value.AdvertisingMeasurementPolicyVersion is null
        ? value.AdvertisingConsent : IsCurrent(value) && value.GoogleAdvertisingConsent;
}
