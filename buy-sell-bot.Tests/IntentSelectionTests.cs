using BuySellBot.Bot;
using BuySellBot.Domain;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class IntentSelectionTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public void BuyAndSellHaveIdenticalStepsAndPostsExceptForTheFirstTag()
    {
        using var bot = new BotFixture();
        var buy = bot.Listing(); buy.Intent = Intent.Buy;
        var sell = bot.Listing(); sell.Intent = Intent.Sell;
        buy.Price = sell.Price = new(PriceKind.Fixed, 100, 200);
        buy.Condition = sell.Condition = T.Conditions[0];
        var renderer = new BuilderRenderer(bot.Engine, bot.Formatter);
        foreach (var step in bot.Engine.Steps)
        {
            var session = new BuilderSession { UserId = 10, CurrentStepId = step.Id };
            var buyView = renderer.Render(buy, session);
            var sellView = renderer.Render(sell, session);
            Assert.Equal(buyView.ActionsText, sellView.ActionsText);
            Assert.Equal(buyView.Keyboard.InlineKeyboard.SelectMany(row => row).Select(button => button.Text),
                sellView.Keyboard.InlineKeyboard.SelectMany(row => row).Select(button => button.Text));
        }
        Assert.Null(bot.Formatter.Validate(buy));
        Assert.Null(bot.Formatter.Validate(sell));
        Assert.StartsWith("#купівля ", bot.Formatter.Format(buy));
        Assert.StartsWith("#продаж ", bot.Formatter.Format(sell));
        Assert.Equal(bot.Formatter.Format(buy).Replace("#купівля", "#продаж"), bot.Formatter.Format(sell));
    }

    [Fact]
    public async Task ConfirmingARestartUsesTheNewlySelectedIntent()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.Buy), Ct);
        var original = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Equal(Intent.Buy, (await bot.Listings.Get(original.ListingId, Ct))!.Intent);
        await bot.Router.Handle(BotFixture.Message(10, T.Sell), Ct);
        var confirmation = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Equal(Intent.Sell, confirmation.RestartIntent);
        await bot.Router.Handle(BotFixture.Callback(10, confirmation.BuilderMessageId!.Value,
            new("confirm", confirmation.ListingId, confirmation.Nonce)), Ct);
        var restarted = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.NotEqual(original.ListingId, restarted.ListingId);
        Assert.Equal(Intent.Sell, (await bot.Listings.Get(restarted.ListingId, Ct))!.Intent);
        Assert.Equal(ListingStatus.Cancelled, (await bot.Listings.Get(original.ListingId, Ct))!.Status);
    }

    [Fact]
    public async Task OldCombinedButtonShowsTheTwoChoices()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.CreateListing), Ct);
        Assert.Null(await bot.Sessions.Get(10, SessionMode.Author, Ct));
        var menu = Assert.IsType<ReplyKeyboardMarkup>(bot.Telegram.LastReplyKeyboard);
        Assert.Equal(new[] { T.Buy, T.Sell }, menu.Keyboard.SelectMany(row => row).Select(button => button.Text));
        Assert.Equal(T.ChooseIntent, bot.Telegram.SentMessages.Last().Text);
    }
}
