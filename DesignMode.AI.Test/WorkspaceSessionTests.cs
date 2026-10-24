using System.Security.Claims;
using DesignMode.AI.Client.Services;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class WorkspaceSessionTests
{
    [DataTestMethod]
    [DataRow("email")]
    [DataRow(ClaimTypes.Email)]
    [DataRow(ClaimTypes.Name)]
    [DataRow("unique_name")]
    public void SignedInAccount_UsesSessionIdentity(string claim)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claim, " person@example.com ")], "jwt"));
        Assert.AreEqual("person@example.com", WorkspaceSession.GetEmail(user));
    }

    [TestMethod]
    public void AnonymousAccount_DoesNotSkipPrompt()
    {
        Assert.IsNull(WorkspaceSession.GetEmail(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "person@example.com")]))));
    }

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("invalid")]
    public void ExpiredSession_DoesNotSkipPrompt(string expiry)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "person@example.com"), new Claim("exp", expiry)], "jwt"));
        Assert.IsNull(WorkspaceSession.GetEmail(user));
    }

    [TestMethod]
    public void MissingIdentity_DoesNotPretendUserIsAnonymous()
    {
        Assert.ThrowsException<InvalidOperationException>(() => WorkspaceSession.GetEmail(new ClaimsPrincipal(new ClaimsIdentity([], "jwt"))));
    }
}
