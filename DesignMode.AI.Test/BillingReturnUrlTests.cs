using DesignMode.AI.Server.Services;
using Microsoft.Extensions.Configuration;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class BillingReturnUrlTests
{
    [DataTestMethod]
    [DataRow("https://localhost:5001", true, "https://localhost:5001/init/dashboard")]
    [DataRow("https://designmode.ai", false, "https://designmode.ai/init/dashboard")]
    public void Dashboard_UsesValidatedAppOrigin(string origin, bool test, string expected) =>
        Assert.AreEqual(expected, BillingReturnUrl.Dashboard(origin, test, new ConfigurationBuilder().Build()));

    [DataTestMethod]
    [DataRow("https://attacker.example", true)]
    [DataRow("http://localhost:5000", false)]
    [DataRow("https://designmode.ai@attacker.example", false)]
    [DataRow("https://designmode.ai/phishing", false)]
    [DataRow(null, true)]
    public void Dashboard_RejectsUntrustedOrigin(string? origin, bool test) =>
        Assert.ThrowsException<InvalidOperationException>(() => BillingReturnUrl.Dashboard(origin, test, new ConfigurationBuilder().Build()));
}
