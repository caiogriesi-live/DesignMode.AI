using System.Globalization;
using System.Security.Claims;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace DesignMode.AI.Server;

public sealed class MarketingDashboardFunction(JwtUserResolver jwt, MarketingDashboardReportService reports)
{
    [Function("MarketingDashboardReport")]
    public async Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "marketing-dashboard")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsOperator(request)) return new UnauthorizedResult();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(MarketingReportingPeriodResolver.DefaultTimeZoneId)).DateTime);
        var endText = request.Query["end"].ToString();
        var startText = request.Query["start"].ToString();
        if ((!string.IsNullOrWhiteSpace(endText) && !TryDate(endText, out _)) ||
            (!string.IsNullOrWhiteSpace(startText) && !TryDate(startText, out _)))
            return new BadRequestObjectResult("Dates must use yyyy-MM-dd format.");
        var end = ParseDate(endText, today);
        var start = ParseDate(startText, end.AddDays(-29));
        if (end < start || end.DayNumber - start.DayNumber > 365)
            return new BadRequestObjectResult("Choose a date range of 1 to 366 days.");
        try { return new OkObjectResult(await reports.GetAsync(start, end, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new ObjectResult("Marketing reporting is temporarily unavailable. Retry in a moment.") { StatusCode = StatusCodes.Status503ServiceUnavailable }; }
    }

    private bool IsOperator(HttpRequest request)
    {
        var email = jwt.ResolvePrincipal(request)?.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(email)) return false;
        return (Environment.GetEnvironmentVariable("DESIGNMODE_COMPANY_OPERATOR_EMAILS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(email, StringComparer.OrdinalIgnoreCase);
    }

    private static DateOnly ParseDate(string? value, DateOnly fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryDate(string value, out DateOnly parsed)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
}
