using BuySellBot.Domain;
using BuySellBot.Flow;

namespace BuySellBot.Tests;

public sealed class PriceParserTests
{
    [Theory]
    [InlineData("1500", 1500)]
    [InlineData("1 500 грн", 1500)]
    [InlineData("1\u00a0500,50 ₴", 1500.5)]
    [InlineData("25.75 USD", 25.75)]
    public void ParsesAmounts(string text, decimal expected)
    {
        Assert.True(PriceParser.TryParse(text, Intent.Sell, Currency.UAH, out var price));
        Assert.Equal(expected, price!.Amount);
    }
    [Theory]
    [InlineData("1000-2000")]
    [InlineData("1 000 – 2 000 EUR")]
    public void LegacyIntentRulesAllowBuyRangesAndRejectSellRanges(string text)
    {
        Assert.True(PriceParser.TryParse(text, Intent.Buy, Currency.UAH, out var price));
        Assert.Equal(1000m, price!.Amount); Assert.Equal(2000m, price.AmountTo);
        Assert.False(PriceParser.TryParse(text, Intent.Sell, Currency.UAH, out _));
    }
    [Theory]
    [InlineData("-1")][InlineData("0")][InlineData("2000-1000")][InlineData("1.234")][InlineData("1e3")][InlineData("NaN")]
    [InlineData("1000000000")][InlineData("12 руб")][InlineData("")][InlineData("10-20-30")]
    public void RejectsInvalid(string text) => Assert.False(PriceParser.TryParse(text, Intent.Buy, Currency.UAH, out _));
}
