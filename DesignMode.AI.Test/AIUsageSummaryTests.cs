using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesignMode.AI.Test;

[TestClass]
[DoNotParallelize]
public sealed class AIUsageSummaryTests
{
    [DataTestMethod]
    [DataRow("gen_main_image")]
    [DataRow("social_image")]
    public void InitialFiveImages_UseHalfACreditOfProAllowance(string mainFeature)
    {
        var options = AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var rows = Enumerable.Range(0, 5).Select(index =>
        {
            var feature = index == 0 ? mainFeature : "gen_secondary_image";
            return (Id: $"usage-{index}", JobId: (string?)$"image-{index}",
                Feature: (string?)feature, Units: options.UnitsFor(feature));
        });
        var used = AIUsageSummaryService.AggregateGenerationUnits(rows);
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var summary = AIUsageSummaryService.Calculate("pro", start, start.AddMonths(1), options.LimitFor("pro"), used);

        Assert.AreEqual(0.5m, used);
        Assert.AreEqual("0.5 / 50 used", AIUsageSummaryDisplay.PrimaryText(summary));
        Assert.AreEqual(49.5m, summary.GenerationsRemaining);
        Assert.AreEqual(3m, options.UnitsFor("social_post"));
        Assert.AreEqual(10m, options.UnitsFor("commercial_video"));
    }

    [TestMethod]
    public void Quotas_ProAndExtremeHaveSeparateAllowances()
    {
        var defaults = AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.AreEqual(50m, defaults.LimitFor("pro"));
        Assert.AreEqual(130m, defaults.LimitFor("extreme"));
        var configured = AIUsageQuotaOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI_GENERATION_LIMIT_EXTREME"] = "150" }).Build());
        Assert.AreEqual(50m, configured.LimitFor("pro"));
        Assert.AreEqual(150m, configured.LimitFor("extreme"));
    }

    [TestMethod]
    public void Calculate_ClampsRemainingAndPercentAtExhaustion()
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var summary = AIUsageSummaryService.Calculate("pro", start, start.AddMonths(1), 10, 13);

        Assert.AreEqual(13m, summary.GenerationsUsed);
        Assert.AreEqual(0m, summary.GenerationsRemaining);
        Assert.AreEqual(100m, summary.PercentUsed);
        Assert.IsTrue(summary.EnforcementEnabled);
    }

    [TestMethod]
    public void BillingPeriodEnd_UsesOnlyQualifiedStoredIntervals()
    {
        var start = DateTimeOffset.Parse("2026-01-31T12:00:00Z");
        Assert.AreEqual(start.AddMonths(1), AIUsageSummaryService.BillingPeriodEnd(start, "month"));
        Assert.AreEqual(start.AddYears(1), AIUsageSummaryService.BillingPeriodEnd(start, "yearly"));
        Assert.IsNull(AIUsageSummaryService.BillingPeriodEnd(start, "one_time"));
        Assert.IsNull(AIUsageSummaryService.BillingPeriodEnd(start, null));
    }

    [TestMethod]
    public void EffectiveCost_UsesActualInsteadOfAddingEstimate()
    {
        var usage = new AIGenerationUsage { ActualCost = 0.40m, EstimatedCost = 0.25m };
        Assert.AreEqual(0.40m, usage.EffectiveCost);
        usage.ActualCost = null;
        Assert.AreEqual(0.25m, usage.EffectiveCost);
    }

    [TestMethod]
    public void LocalSettingsConfiguration_LoadsLimitsAndWeights()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Values:AI_GENERATION_LIMIT_PRO"] = "10",
            ["Values:AI_GENERATION_UNITS_IMAGE"] = "0.1",
            ["Values:AI_GENERATION_UNITS_WEBSITE"] = "3",
            ["Values:AI_GENERATION_UNITS_SOCIAL_ASSET"] = "4",
            ["Values:AI_GENERATION_UNITS_COMMERCIAL_VIDEO"] = "5",
            ["Values:AI_GENERATION_UNITS_RECOMMENDATIONS"] = "6",
            ["Values:AI_GENERATION_UNITS_PRINT_MATERIALS"] = "7",
            ["Values:AI_GENERATION_UNITS_EVENTS_ACTIVATIONS"] = "8"
        }).Build();
        var services = new ServiceCollection();
        services.AddAIUsageQuotaOptions(config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AIUsageQuotaOptions>();

        Assert.AreEqual(10m, options.LimitFor("pro"));
        Assert.AreEqual(0.1m, options.UnitsFor("gen_main_image"));
        Assert.AreEqual(7m, options.UnitsFor("package_label"));
        Assert.AreEqual(3m, options.UnitsFor("website_generation"));
        Assert.AreEqual(0.1m, options.UnitsFor("social_image"));
        Assert.AreEqual(4m, options.UnitsFor("social_post"));
        Assert.AreEqual(5m, options.UnitsFor("commercial_video"));
        Assert.AreEqual(6m, options.UnitsFor("ai_recommendations"));
        Assert.AreEqual(7m, options.UnitsFor("print_material"));
        Assert.AreEqual(8m, options.UnitsFor("event_activation"));
        Assert.AreEqual(0m, options.UnitsFor("intro_concept_image"));
    }

    [TestMethod]
    public void LocalSettingsConfiguration_ParsesFractionalImageUnitsAndRejectsInvalidValues()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Values:AI_GENERATION_UNITS_IMAGE"] = "0.1",
            ["Values:AI_GENERATION_UNITS_WEBSITE"] = "1.5",
            ["Values:AI_GENERATION_UNITS_SOCIAL_ASSET"] = "2.25",
            ["Values:AI_GENERATION_UNITS_COMMERCIAL_VIDEO"] = "-1",
            ["Values:AI_GENERATION_UNITS_RECOMMENDATIONS"] = "abc"
        }).Build();
        var services = new ServiceCollection();
        services.AddAIUsageQuotaOptions(config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AIUsageQuotaOptions>();

        Assert.AreEqual(0.1m, options.UnitsFor("gen_main_image"));
        Assert.AreEqual(1.5m, options.UnitsFor("website_generation"));
        Assert.AreEqual(0.1m, options.UnitsFor("social_image"));
        Assert.AreEqual(2.25m, options.UnitsFor("social_post"));
        Assert.AreEqual(10m, options.UnitsFor("commercial_video"));
        Assert.AreEqual(1m, options.UnitsFor("ai_recommendations"));
    }

    [DataTestMethod]
    [DataRow(0.05, 0.1, false)]
    [DataRow(0.1, 0.1, true)]
    [DataRow(0.15, 0.1, true)]
    public void WebsiteQuota_RequiresAllRequestedUnits(double remaining, double requested, bool expected)
    {
        var summary = AIUsageSummaryService.Calculate("pro", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(1), 100m, 100m - (decimal)remaining);
        Assert.AreEqual(expected, AIUsageSummaryService.IsAllowed(summary, (decimal)requested));
    }

    [TestMethod]
    public void IntroUsage_DoesNotCountTowardPaidAllowance()
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        Assert.IsFalse(AIUsageSummaryService.CountsTowardPaidAllowance("Intro", "Succeeded", start, start, start.AddMonths(1)));
        Assert.IsTrue(AIUsageSummaryService.CountsTowardPaidAllowance("Paid", "Succeeded", start, start, start.AddMonths(1)));
    }

    [TestMethod]
    public void PaidAllowance_RequiresPaidScopeSuccessfulStatusAndCurrentBillingPeriod()
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var end = start.AddMonths(1);
        Assert.IsFalse(AIUsageSummaryService.CountsTowardPaidAllowance("Paid", "Failed", start, start, end));
        Assert.IsFalse(AIUsageSummaryService.CountsTowardPaidAllowance("Paid", "Succeeded", start.AddDays(-1), start, end));
        Assert.IsFalse(AIUsageSummaryService.CountsTowardPaidAllowance("Paid", "Succeeded", end, start, end));
    }

    [TestMethod]
    public void DuplicateAndInternalProviderRows_CountOneWebsiteGeneration()
    {
        var rows = new[]
        {
            ("1", (string?)"website-job", (string?)"website_generation", 0.1m),
            ("2", (string?)"website-job", (string?)"website_generation", 0.1m),
            ("3", (string?)"website-job", (string?)"website_generation", 0m)
        };
        Assert.AreEqual(0.1m, AIUsageSummaryService.AggregateGenerationUnits(rows));
    }

    [TestMethod]
    public void CommercialVideo_RunwareAndVoiceRows_CountOneConfiguredGeneration()
    {
        var rows = new[]
        {
            ("video-1", (string?)"commercial-job", (string?)"commercial_video", 5m),
            ("video-2", (string?)"commercial-job", (string?)"commercial_video", 0m),
            ("voice-1", (string?)"commercial-job", (string?)"commercial_video", 0m),
            ("voice-2", (string?)"commercial-job", (string?)"commercial_video", 0m)
        };

        Assert.AreEqual(5m, AIUsageSummaryService.AggregateGenerationUnits(rows));
    }

    [TestMethod]
    public void AggregateGenerationUnits_ExcludesOtherUsersByCallerFilter()
    {
        var rows = new[]
        {
            ("1", (string?)"job-a", (string?)"website_generation", 3m),
            ("2", (string?)"job-b", (string?)"website_generation", 4m)
        };

        Assert.AreEqual(7m, AIUsageSummaryService.AggregateGenerationUnits(rows));
    }

    [TestMethod]
    public void AggregateGenerationUnits_SumsMixedFractionalValues()
    {
        var rows = new[]
        {
            ("1", (string?)"job-a", (string?)"image", 0.1m),
            ("2", (string?)"job-b", (string?)"image", 0.1m),
            ("3", (string?)"job-c", (string?)"text", 1m),
            ("4", (string?)"job-d", (string?)"video", 3m)
        };

        Assert.AreEqual(4.2m, AIUsageSummaryService.AggregateGenerationUnits(rows));
    }

    [TestMethod]
    public void UnlimitedSummary_UsesUsageTextAndUnlimitedState()
    {
        var summary = AIUsageSummaryService.Calculate("starter", DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-01T00:00:00Z"), null, 27);

        Assert.AreEqual("AI Credits", AIUsageSummaryDisplay.PrimaryLabel);
        Assert.AreEqual("Credits: 27", AIUsageSummaryDisplay.PrimaryText(summary));
        Assert.AreEqual("Unlimited", AIUsageSummaryDisplay.RemainingText(summary));
        Assert.IsFalse(AIUsageSummaryDisplay.IsExhausted(summary));
    }

    [TestMethod]
    public void ExhaustedSummary_ShowsLimitReachedState()
    {
        var summary = AIUsageSummaryService.Calculate("pro", DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-01T00:00:00Z"), 100, 100);

        Assert.AreEqual("100 / 100 used", AIUsageSummaryDisplay.PrimaryText(summary));
        Assert.AreEqual("0 remaining", AIUsageSummaryDisplay.RemainingText(summary));
        Assert.IsTrue(AIUsageSummaryDisplay.IsExhausted(summary));
    }

    [TestMethod]
    public void OverLimitSummary_ClampsRemainingToZero()
    {
        var summary = AIUsageSummaryService.Calculate("pro", DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-01T00:00:00Z"), 100, 105);

        Assert.AreEqual(0m, summary.GenerationsRemaining);
        Assert.AreEqual("105 / 100 used", AIUsageSummaryDisplay.PrimaryText(summary));
        Assert.AreEqual("0 remaining", AIUsageSummaryDisplay.RemainingText(summary));
    }

    [TestMethod]
    public void FractionalSummary_DisplaysCleanDecimals()
    {
        var summary = AIUsageSummaryService.Calculate("pro", DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-01T00:00:00Z"), 50m, 4.3m);

        Assert.AreEqual("4.3 / 50 used", AIUsageSummaryDisplay.PrimaryText(summary));
        Assert.AreEqual("45.7 remaining", AIUsageSummaryDisplay.RemainingText(summary));
        Assert.IsFalse(AIUsageSummaryDisplay.IsExhausted(summary));
    }
}
