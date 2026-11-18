using DesignMode.AI.Server;
using DesignMode.AI.Shared.Security;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class SignupPersistenceTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void SignupItem_OmitsOptionalPhoneAndRetainsExistingAttributes(string? phone)
    {
        var item = DesignModeFunctions.CreateSignupUserItem("fixture-id", "fixture@example.invalid", "hash", "salt", phone);
        Assert.IsFalse(item.ContainsKey("PhoneNumber"));
        Assert.AreEqual("fixture-id", item["Id"].S);
        Assert.AreEqual("fixture@example.invalid", item["Username"].S);
        Assert.AreEqual("fixture@example.invalid", item["NormalizedUsername"].S);
        Assert.AreEqual("hash", item["PasswordHash"].S);
        Assert.AreEqual("0", item["Onboarded"].S);
        Assert.AreEqual("Password", item["AuthenticationProvider"].S);
    }

    [TestMethod]
    public void SignupItem_PersistsValidatedPhoneAsFormattedString()
    {
        Assert.IsTrue(PhoneNumberValidator.TryNormalize(" +1 (317) 555-0100 ", out var phone, out _));
        var item = DesignModeFunctions.CreateSignupUserItem("fixture-id", "fixture@example.invalid", "hash", "salt", phone);
        Assert.AreEqual("+1 (317) 555-0100", item["PhoneNumber"].S);
        Assert.AreEqual("Password", item["AuthenticationProvider"].S);
    }

    [TestMethod]
    public void SignupRequest_LegacyPayloadWithoutPhoneDeserializes()
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<DesignModeFunctions.SignupRequest>(
            "{\"Username\":\"fixture@example.invalid\",\"Password\":\"fixture\"}",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(request);
        Assert.IsNull(request.PhoneNumber);
    }
}
