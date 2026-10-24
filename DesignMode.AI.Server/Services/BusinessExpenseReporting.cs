using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Server.Services;

public sealed record BusinessExpenseRead(IReadOnlyList<BusinessExpense> Rows, ReportingSourceCoverage Coverage);

public interface IBusinessExpenseReportingReader
{
    Task<BusinessExpenseRead> ReadAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken = default);
}

/// <summary>Read-only date-bounded scan using the existing WORK-04 table and fields.</summary>
public sealed class DynamoBusinessExpenseReportingReader(Func<IAmazonDynamoDB>? createClient = null) : IBusinessExpenseReportingReader
{
    private readonly Func<IAmazonDynamoDB> _createClient = createClient ?? DynamoDbClientFactory.CreateClient;
    private static string Table => Environment.GetEnvironmentVariable("BusinessExpensesTableName") ?? "BusinessExpenses";

    public async Task<BusinessExpenseRead> ReadAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken = default)
    {
        try
        {
            using var db = _createClient();
            var rows = new List<BusinessExpense>();
            Dictionary<string, AttributeValue>? key = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await db.ScanAsync(new ScanRequest
                {
                    TableName = Table, ConsistentRead = true, ExclusiveStartKey = key,
                    FilterExpression = "#expenseDate BETWEEN :start AND :end",
                    ExpressionAttributeNames = new() { ["#expenseDate"] = "Date" },
                    ExpressionAttributeValues = new()
                    {
                        [":start"] = new(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        [":end"] = new(end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    }
                }, cancellationToken);
                rows.AddRange((page.Items ?? []).Select(ToExpense));
                key = page.LastEvaluatedKey;
            } while (key is { Count: > 0 });
            return new(rows, new ReportingSourceCoverage { Source = MarketingReportingSource.BusinessExpenses, State = ReportingDataState.Complete,
                RecordsObserved = rows.Count, Explanation = "All expense rows in the requested date range were read from the configured WORK-04 table." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return new([], new ReportingSourceCoverage { Source = MarketingReportingSource.BusinessExpenses, State = ReportingDataState.Error,
                Explanation = "Business expense reporting could not read the configured WORK-04 table.", ErrorCode = "expense_source_read_failed" });
        }
    }

    private static BusinessExpense ToExpense(Dictionary<string, AttributeValue> item) => new()
    {
        ExpenseId = item.GetValueOrDefault("ExpenseId")?.S ?? "",
        Date = item.GetValueOrDefault("Date")?.S ?? "",
        Amount = decimal.TryParse(item.GetValueOrDefault("Amount")?.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : 0,
        Currency = item.GetValueOrDefault("Currency")?.S ?? "",
        Category = item.GetValueOrDefault("Category")?.S ?? "",
        Description = item.GetValueOrDefault("Description")?.S ?? ""
    };
}
