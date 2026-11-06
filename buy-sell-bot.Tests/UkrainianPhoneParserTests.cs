using BuySellBot.Flow;

namespace BuySellBot.Tests;

public sealed class UkrainianPhoneParserTests
{
    [Theory]
    [InlineData("+380501234567")]
    [InlineData("380501234567")]
    [InlineData("00380501234567")]
    [InlineData("0501234567")]
    [InlineData("501234567")]
    [InlineData("+38 (050) 123-45-67")]
    [InlineData("(050) 123 45 67")]
    [InlineData("050.123.45.67")]
    [InlineData("  +380 50 123 45 67  ")]
    [InlineData("050\u00a0123\u201145\u201167")]
    public void CommonFormatsNormalizeToAnInternationalNumber(string input)
    {
        Assert.True(UkrainianPhoneParser.TryParse(input, out var phone));
        Assert.Equal("+380501234567", phone);
    }

    [Theory]
    [InlineData("+380441234567", "+380441234567")]
    [InlineData("032 123 45 67", "+380321234567")]
    [InlineData("0800 123 456", "+380800123456")]
    public void LandlineAndServiceNumbersAreAccepted(string input, string expected)
    {
        Assert.True(UkrainianPhoneParser.TryParse(input, out var phone));
        Assert.Equal(expected, phone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("050123456")]
    [InlineData("05012345678")]
    [InlineData("+48123456789")]
    [InlineData("+15012345678")]
    [InlineData("+0501234567")]
    [InlineData("++380501234567")]
    [InlineData("38050+1234567")]
    [InlineData("0000000000")]
    [InlineData("+380001234567")]
    [InlineData("0501234567 ext 12")]
    [InlineData("call 0501234567")]
    [InlineData("０５０１２３４５６７")]
    [InlineData("050/123/45/67")]
    public void InvalidOrForeignNumbersAreRejected(string? input)
    {
        Assert.False(UkrainianPhoneParser.TryParse(input, out var phone));
        Assert.Null(phone);
    }
}
