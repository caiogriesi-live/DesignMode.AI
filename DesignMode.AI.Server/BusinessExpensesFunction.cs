using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Server.Services;
using DesignMode.AI.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace DesignMode.AI.Server;

public sealed class BusinessExpensesFunction
{
    private readonly JwtUserResolver _jwt;
    private readonly Func<IAmazonDynamoDB> _createClient;

    public BusinessExpensesFunction(JwtUserResolver jwt, Func<IAmazonDynamoDB>? createClient = null)
    {
        _jwt = jwt;
        _createClient = createClient ?? DynamoDbClientFactory.CreateClient;
    }

    public static readonly string[] Categories =
    ["software", "AI/API", "hosting/infrastructure", "contractors", "payroll", "advertising/marketing", "professional services", "other"];

    private static string Table => Environment.GetEnvironmentVariable("BusinessExpensesTableName") ?? "BusinessExpenses";

    [Function("BusinessExpensesList")]
    public async Task<IActionResult> List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "business-expenses")] HttpRequest request)
    {
        if (!IsOperator(request)) return new UnauthorizedResult();
        if (!TryDate(request.Query["date"], out var date) || string.IsNullOrWhiteSpace(request.Query["currency"])) return new BadRequestResult();
        try
        {
            using var db = _createClient();
            var items = new List<BusinessExpense>();
            Dictionary<string, AttributeValue>? startKey = null;
            do
            {
                var response = await db.ScanAsync(new ScanRequest
                {
                    TableName = Table,
                    ExclusiveStartKey = startKey,
                    FilterExpression = "#expenseDate = :date AND #currency = :currency",
                    ExpressionAttributeNames = new() { ["#expenseDate"] = "Date", ["#currency"] = "Currency" },
                    ExpressionAttributeValues = new() { [":date"] = new AttributeValue(date), [":currency"] = new AttributeValue(request.Query["currency"].ToString()) }
                });
                items.AddRange(response.Items.Select(ToExpense));
                startKey = response.LastEvaluatedKey;
            } while (startKey is { Count: > 0 });
            return new OkObjectResult(items);
        }
        catch { return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable); }
    }

    [Function("BusinessExpensesCreate")]
    public Task<IActionResult> Create([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "business-expenses")] HttpRequest request)
        => Save(request, null);

    [Function("BusinessExpensesEdit")]
    public Task<IActionResult> Edit([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "business-expenses/{id}")] HttpRequest request, string id)
        => Save(request, id);

    private async Task<IActionResult> Save(HttpRequest request, string? id)
    {
        if (!IsOperator(request)) return new UnauthorizedResult();
        BusinessExpense? expense;
        try { expense = await request.ReadFromJsonAsync<BusinessExpense>(); }
        catch { return new BadRequestResult(); }
        if (expense is null || !TryDate(expense.Date, out var date) || expense.Amount < 0 ||
            string.IsNullOrWhiteSpace(expense.Currency) || expense.Currency.Length > 12 ||
            !Categories.Contains(expense.Category, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(expense.Description))
            return new BadRequestObjectResult("Invalid expense fields.");

        expense.Date = date;
        expense.ExpenseId = id ?? Guid.NewGuid().ToString("N");
        var item = new Dictionary<string, AttributeValue>
        {
            ["ExpenseId"] = new(expense.ExpenseId), ["Date"] = new(expense.Date),
            ["Amount"] = new() { N = expense.Amount.ToString(CultureInfo.InvariantCulture) }, ["Currency"] = new(expense.Currency),
            ["Category"] = new(expense.Category), ["Description"] = new(expense.Description)
        };
        try
        {
            using var db = _createClient();
            if (id is not null)
            {
                var existing = await db.GetItemAsync(new GetItemRequest
                {
                    TableName = Table,
                    Key = new Dictionary<string, AttributeValue> { ["ExpenseId"] = new(id) },
                    ConsistentRead = true
                });
                if (existing.Item is not { Count: > 0 }) return new NotFoundResult();
            }
            await db.PutItemAsync(new PutItemRequest { TableName = Table, Item = item });
            return new OkObjectResult(expense);
        }
        catch { return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable); }
    }

    private bool IsOperator(HttpRequest request)
    {
        var principal = _jwt.ResolvePrincipal(request);
        var email = principal?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(email)) return false;
        var allowlist = Environment.GetEnvironmentVariable("DESIGNMODE_COMPANY_OPERATOR_EMAILS") ?? string.Empty;
        return allowlist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(email, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryDate(string? value, out string date)
    {
        date = string.Empty;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static BusinessExpense ToExpense(Dictionary<string, AttributeValue> item) => new()
    {
        ExpenseId = item.GetValueOrDefault("ExpenseId")?.S ?? string.Empty,
        Date = item.GetValueOrDefault("Date")?.S ?? string.Empty,
        Amount = decimal.TryParse(item.GetValueOrDefault("Amount")?.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : 0,
        Currency = item.GetValueOrDefault("Currency")?.S ?? string.Empty,
        Category = item.GetValueOrDefault("Category")?.S ?? string.Empty,
        Description = item.GetValueOrDefault("Description")?.S ?? string.Empty
    };
}
