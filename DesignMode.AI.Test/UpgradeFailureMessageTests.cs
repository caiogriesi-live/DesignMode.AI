using System.Net;
using DesignMode.AI.Client.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class UpgradeFailureMessageTests
{
    [DataTestMethod]
    [DataRow("This older subscription needs help from contact@designmode.ai before changing plans.")]
    [DataRow("\"This older subscription needs help from contact@designmode.ai before changing plans.\"")]
    public void LegacySubscription_ShowsSpecificServerReason(string body) =>
        StringAssert.Contains(AICreditPurchaseClient.UpgradeFailureMessage(HttpStatusCode.Conflict, body), "older subscription");

    [TestMethod]
    public void MissingConfiguration_ShowsConfigurationProblem() =>
        StringAssert.Contains(AICreditPurchaseClient.UpgradeFailureMessage(HttpStatusCode.ServiceUnavailable,
            "Plan changes are not configured for this environment."), "not configured");

    [DataTestMethod]
    [DataRow("No current recurring subscription was found for your account. Please contact contact@designmode.ai to set up your plan.")]
    [DataRow("Your account has multiple current subscriptions. Please contact contact@designmode.ai to choose which subscription to upgrade.")]
    public void SubscriptionCountConflict_PreservesSpecificReason(string message) =>
        Assert.AreEqual(message, AICreditPurchaseClient.UpgradeFailureMessage(HttpStatusCode.Conflict, message));

    [DataTestMethod]
    [DataRow("<html>private stack trace</html>")]
    [DataRow("Stripe error: sk_test_private sub_private")]
    [DataRow("\"broken JSON")]
    public void UnexpectedErrors_DoNotExposeProviderDetails(string body) =>
        Assert.AreEqual("We couldn't open the plan change confirmation. Please try again.",
            AICreditPurchaseClient.UpgradeFailureMessage(HttpStatusCode.InternalServerError, body));
}
