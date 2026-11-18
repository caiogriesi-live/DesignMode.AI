using DesignMode.AI.Shared.Security;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PhoneNumberValidatorTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void TryNormalize_BlankValuesAreAbsent(string? input)
    {
        Assert.IsTrue(PhoneNumberValidator.TryNormalize(input, out var value, out _));
        Assert.IsNull(value);
    }

    [DataTestMethod]
    [DataRow(" +1 (317) 555-0100 ", "+1 (317) 555-0100")]
    [DataRow("001.234", "001.234")]
    public void TryNormalize_ValidFormattingIsPreserved(string input, string expected)
    {
        Assert.IsTrue(PhoneNumberValidator.TryNormalize(input, out var value, out _));
        Assert.AreEqual(expected, value);
    }

    [DataTestMethod]
    [DataRow("12")]
    [DataRow("1234567890123456")]
    [DataRow("12x345")]
    [DataRow("12+345")]
    [DataRow("+1+234")]
    [DataRow("１２３")]
    public void TryNormalize_InvalidNonemptyValuesReturnUsefulError(string input)
    {
        Assert.IsFalse(PhoneNumberValidator.TryNormalize(input, out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }
}
