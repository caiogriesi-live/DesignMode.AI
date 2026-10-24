using System.Text;
using System.Text.Json;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Shared.Models;
using DesignMode.AI.Shared.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DesignMode.AI.Test;

[TestClass, DoNotParallelize]
public sealed class BusinessExpensesAuthorizationTests
{
    private const string CustomerEmail = "customer@example.test";
    private const string OperatorEmail = "operator@example.test";
    private const string JwtSecret = "business-expense-authorization-test-secret-123456";
    private readonly Dictionary<string, string?> _oldEnvironment = new();
    private int _dataAccessAttempts;

    [TestInitialize]
    public void Setup()
    {
        SetEnvironment("JwtSecretKey", JwtSecret);
        _dataAccessAttempts = 0;
        SetEnvironment("DESIGNMODE_COMPANY_OPERATOR_EMAILS", OperatorEmail);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var (name, value) in _oldEnvironment)
            Environment.SetEnvironmentVariable(name, value);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task List_UnauthenticatedOrOrdinaryCustomer_IsDeniedBeforeDataAccess(bool authenticated)
    {
        var function = CreateIsolatedFunction();

        var result = await function.List(Request(authenticated));

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.AreEqual(0, _dataAccessAttempts, "Denied requests must not access the financial data store.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Create_UnauthenticatedOrOrdinaryCustomer_IsDeniedBeforeDataAccess(bool authenticated)
    {
        var function = CreateIsolatedFunction();

        var result = await function.Create(Request(authenticated));

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.AreEqual(0, _dataAccessAttempts, "Denied requests must not access the financial data store.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Edit_UnauthenticatedOrOrdinaryCustomer_IsDeniedBeforeDataAccess(bool authenticated)
    {
        var function = CreateIsolatedFunction();

        var result = await function.Edit(Request(authenticated), "expense-id");

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
        Assert.AreEqual(0, _dataAccessAttempts, "Denied requests must not access the financial data store.");
    }

    private BusinessExpensesFunction CreateIsolatedFunction()
        => new(new JwtUserResolver(), () =>
        {
            _dataAccessAttempts++;
            throw new InvalidOperationException("Authorization tests must never access DynamoDB.");
        });
    private void SetEnvironment(string name, string value)
    {
        _oldEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private static HttpRequest Request(bool authenticated)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = "application/json";
        request.QueryString = new QueryString("?date=2026-10-08&currency=USD");
        if (authenticated)
            request.Headers.Authorization = "Bearer " + Utilities.GenerateJwt("customer", CustomerEmail);
        var expense = new BusinessExpense
        {
            Date = "2026-10-08",
            Amount = 12.50m,
            Currency = "USD",
            Category = "software",
            Description = "Test subscription"
        };
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expense)));
        return request;
    }
}
