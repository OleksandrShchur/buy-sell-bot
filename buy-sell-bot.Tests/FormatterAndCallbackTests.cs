using System.Text;
using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Publishing;

namespace BuySellBot.Tests;

public sealed class FormatterAndCallbackTests
{
    [Fact]
    public void EscapesEveryUserField()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Title = "<b>Річ & річ</b>"; listing.Description = "<script>alert('x')</script>";
        listing.Location = "<Київ>"; listing.AuthorUsername = null; listing.AuthorName = "<Автор>";
        var text = bot.Formatter.Format(listing);
        Assert.DoesNotContain("<script>", text); Assert.DoesNotContain("<b>Річ", text);
        Assert.Contains("&lt;b&gt;", text); Assert.Contains("&amp;", text); Assert.Contains("&lt;Автор&gt;", text);
        Assert.Contains("tg://user?id=10", text);
    }
    [Fact]
    public void MaximumFieldsFitTelegramCaptionAfterHtmlParsing()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Title = new('&', 60); listing.Location = new('<', 60); listing.Description = new('>', 500);
        listing.Condition = BuySellBot.Texts.Texts.Conditions[3];
        listing.Intent = Intent.Buy; listing.Price = new(PriceKind.Fixed, 999999998.99m, 999999999m, Currency.EUR);
        listing.AuthorUsername = new('u', 32);
        bot.Catalog.Categories[0] = bot.Catalog.Categories[0] with { Title = new string('К', 80) };
        var text = bot.Formatter.Format(listing);
        Assert.Null(bot.Formatter.Validate(listing));
        Assert.True(PostFormatter.VisibleLength(text) <= 1024);
        Assert.True(text.Length > 1024); // Telegram limits count parsed entities, not HTML source bytes.
    }
    [Fact]
    public void CallbacksRoundTripAndEnforceByteLimit()
    {
        var original = new CallbackData("change", "1234abcd", "deadbeef", "description", SessionMode.Moderator);
        Assert.True(CallbackData.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original, parsed);
        Assert.True(Encoding.UTF8.GetByteCount(original.ToString()) <= 64);
        Assert.Throws<ArgumentException>(() => new CallbackData("action", "1234abcd", "deadbeef", new string('a', 60)).ToString());
        Assert.False(CallbackData.TryParse("v1|x|../../secret|bad||a", out _));
        Assert.False(CallbackData.TryParse("v1|x|1234abcd|nonce|🛒|a", out _));
    }
    [Fact]
    public void BuildsTopicLink() => Assert.Equal("https://t.me/c/1234567890/42/99", Publisher.BuildLink(-1001234567890, 42, 99));
}
