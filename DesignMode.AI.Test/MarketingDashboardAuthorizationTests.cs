using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using DesignMode.AI.Shared.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DesignMode.AI.Test;

[TestClass, DoNotParallelize]
public sealed class MarketingDashboardAuthorizationTests
{
    private const string CustomerEmail = "customer@example.test";
    private const string JwtSecret = "marketing-dashboard-authorization-test-secret";
    private readonly string? _oldJwtSecret = Environment.GetEnvironmentVariable("JwtSecretKey");
    private readonly string? _oldAllowlist = Environment.GetEnvironmentVariable("DESIGNMODE_COMPANY_OPERATOR_EMAILS");

    [TestInitialize]
    public void Setup()
    {
        Environment.SetEnvironmentVariable("JwtSecretKey", JwtSecret);
        Environment.SetEnvironmentVariable("DESIGNMODE_COMPANY_OPERATOR_EMAILS", "operator@example.test");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("JwtSecretKey", _oldJwtSecret);
        Environment.SetEnvironmentVariable("DESIGNMODE_COMPANY_OPERATOR_EMAILS", _oldAllowlist);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Get_UnauthenticatedOrOrdinaryCustomer_IsDeniedBeforeReporting(bool authenticated)
    {
        var function = new MarketingDashboardFunction(new JwtUserResolver(), null!);
        var request = new DefaultHttpContext().Request;
        if (authenticated)
            request.Headers.Authorization = "Bearer " + Utilities.GenerateJwt("customer", CustomerEmail);

        var result = await function.Get(request, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(UnauthorizedResult));
    }
}
