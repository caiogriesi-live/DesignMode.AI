using System.Security.Claims;
using System.Text;
using DesignMode.AI.Server;
using DesignMode.AI.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class MainConceptProgressTests
{
    [TestMethod]
    public async Task AnonymousRequest_CannotUpdateProgress()
    {
        var store = new PostAuthDestinationResolverTests.RoutingStore();
        var endpoint = new UpdateUserCurrentPageMain(NullLogger<UpdateUserCurrentPageMain>.Instance, new JwtUserResolver(), store);
        Assert.IsInstanceOfType(await endpoint.UpdateUserCurrentPage(new DefaultHttpContext().Request), typeof(UnauthorizedResult));
        Assert.IsNull(store.SavedUsername);
    }

    [DataTestMethod]
    [DataRow("/init/mainconcept?id=project&e=abc%2Bdef", false, 200)]
    [DataRow("/init/mainconcept?id=project", true, 503)]
    [DataRow("https://evil.example/", false, 400)]
    [DoNotParallelize]
    public async Task Progress_UsesSignedUsernameAndRejectsUnsafeDestinations(string page, bool fail, int status)
    {
        var old = Environment.GetEnvironmentVariable("JwtSecretKey");
        try
        {
            Environment.SetEnvironmentVariable("JwtSecretKey", "progress-unit-test-signing-secret-123456789");
            var store = new PostAuthDestinationResolverTests.RoutingStore { ThrowSave = fail };
            var endpoint = new UpdateUserCurrentPageMain(NullLogger<UpdateUserCurrentPageMain>.Instance, new JwtUserResolver(), store);
            var req = new DefaultHttpContext().Request;
            req.Headers.Authorization = "Bearer " + DesignMode.AI.Shared.Util.Utilities.GenerateJwt("signed-name", "signed-id");
            req.Body = new MemoryStream(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { Email = "other-user", Currentpage = page })));
            var result = (ObjectResult)await endpoint.UpdateUserCurrentPage(req);
            Assert.AreEqual(status, result.StatusCode);
            if (status == 200)
            {
                Assert.AreEqual("signed-name", store.SavedUsername);
                Assert.AreEqual(page, store.SavedCurrentPage);
            }
        }
        finally { Environment.SetEnvironmentVariable("JwtSecretKey", old); }
    }
}
