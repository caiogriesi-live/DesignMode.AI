using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Server.Services;

/// <summary>Resolves inclusive local calendar dates to independent instant boundaries.</summary>
public static class MarketingReportingPeriodResolver
{
    public const string DefaultTimeZoneId = "America/New_York";

    public static MarketingReportingPeriod FromDates(MarketingReportingPeriodSelection selection, DateOnly start, DateOnly end,
        string timeZoneId = DefaultTimeZoneId)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end), "End date must be on or after start date.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var startLocal = DateTime.SpecifyKind(start.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var endLocal = DateTime.SpecifyKind(end.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new MarketingReportingPeriod { Selection = selection, StartDate = start, EndDate = end,
            StartInclusive = TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), EndExclusive = TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeZoneId = timeZoneId };
    }

    public static MarketingReportingComparison WithPreviousEquivalentPeriod(MarketingReportingPeriod selected)
    {
        var days = selected.EndDate.DayNumber - selected.StartDate.DayNumber + 1;
        if (days < 1) throw new ArgumentException("Selected period must have ordered dates.", nameof(selected));
        var comparisonEnd = selected.StartDate.AddDays(-1);
        var comparisonStart = comparisonEnd.AddDays(-(days - 1));
        return new MarketingReportingComparison { Selected = selected,
            Comparison = FromDates(MarketingReportingPeriodSelection.CustomRange, comparisonStart, comparisonEnd, selected.TimeZoneId) };
    }
}
