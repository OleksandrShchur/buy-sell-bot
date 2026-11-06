using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Moderation;
using BuySellBot.Publishing;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class CustomDeclineReasonTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task TypedReasonDeclinesTheListingAndNotifiesItsAuthor()
    {
        using var bot = new BotFixture();
        var listing = await BeginDecline(bot);
        var ownerCard = bot.Telegram.SentMessages.Single(message => message.Chat == 100 && message.Id == Card(listing, 100));
        Assert.Contains(T.ChooseReason, ownerCard.Text);
        Assert.Contains(T.Declining, bot.Telegram.SentMessages.Single(message => message.Chat == 200 && message.Id == Card(listing, 200)).Text);
        var oldNonce = ModerationService.Nonce(listing);
        var update = BotFixture.Message(100, "  Додайте <фото> & точну локацію.\nДякуємо!  ");

        await bot.Router.Handle(update, Ct);

        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ListingStatus.Declined, listing.Status);
        Assert.Equal(ModerationOperation.None, listing.Operation);
        Assert.Equal("Додайте <фото> & точну локацію.\nДякуємо!", listing.DeclineReason);
        Assert.True(listing.AuthorNotified);
        var notification = Assert.Single(bot.Telegram.SentMessages, message => message.Chat == 10 && message.Text!.StartsWith("❌ Оголошення відхилено."));
        Assert.Equal(T.AuthorDeclined(PostFormatter.Escape(listing.DeclineReason)), notification.Text);
        Assert.Contains(Assert.IsType<InlineKeyboardMarkup>(notification.Keyboard).InlineKeyboard.SelectMany(row => row), button => button.Text == T.Resubmit);
        Assert.All(listing.ModerationCards, card =>
        {
            var message = bot.Telegram.SentMessages.Single(message => message.Chat == card.AdminId && message.Id == card.MessageId);
            Assert.Empty(Assert.IsType<InlineKeyboardMarkup>(message.Keyboard).InlineKeyboard);
            Assert.Contains(PostFormatter.Escape(listing.DeclineReason), message.Text);
        });
        await bot.Router.Handle(update, Ct);
        Assert.Single(bot.Telegram.SentMessages, message => message.Chat == 10 && message.Text == notification.Text);
        await bot.Router.Handle(BotFixture.Callback(100, Card(listing, 100),
            new("reason", listing.Id, oldNonce, "0", SessionMode.Moderator)), Ct);
        Assert.Equal(T.Stale, bot.Telegram.Answers.Last().Text);
    }

    [Fact]
    public async Task TypingAReasonDoesNotModifyTheAdminsOwnDraft()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(100, T.Sell), Ct);
        var own = (await bot.Sessions.Get(100, SessionMode.Author, Ct))!;
        var listing = await BeginDecline(bot);
        await bot.Router.Handle(BotFixture.Message(100, "Опис не відповідає фотографіям"), Ct);
        Assert.Equal(ListingStatus.Declined, (await bot.Listings.Get(listing.Id, Ct))!.Status);
        Assert.Equal(own.CurrentStepId, (await bot.Sessions.Get(100, SessionMode.Author, Ct))!.CurrentStepId);
        Assert.Empty((await bot.Listings.Get(own.ListingId, Ct))!.Title);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    public async Task OnlyTheAdminWhoClaimedTheListingCanTypeItsReason(long userId)
    {
        using var bot = new BotFixture();
        var listing = await BeginDecline(bot);
        await bot.Router.Handle(BotFixture.Message(userId, "Чужа причина"), Ct);
        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ListingStatus.PendingApproval, listing.Status);
        Assert.Equal(ModerationOperation.Declining, listing.Operation);
        Assert.Null(listing.DeclineReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("Причина\u0001")]
    [InlineData(null)]
    public async Task InvalidOrNonTextInputKeepsTheReasonSelectionOpen(string? reason)
    {
        using var bot = new BotFixture();
        var listing = await BeginDecline(bot);
        var update = BotFixture.Message(100, reason!);
        await bot.Router.Handle(update, Ct);
        Assert.Equal(ModerationOperation.Declining, (await bot.Listings.Get(listing.Id, Ct))!.Operation);
        Assert.Contains(bot.Telegram.SentMessages, message => message.Chat == 100 && message.Text == T.DeclineReasonError);
        Assert.DoesNotContain(bot.Telegram.SentMessages, message => message.Chat == 10);
    }

    [Fact]
    public async Task ReasonsAreLimitedToFiveHundredCharacters()
    {
        using var bot = new BotFixture();
        var listing = await BeginDecline(bot);
        await bot.Router.Handle(BotFixture.Message(100, new string('а', 501)), Ct);
        Assert.Equal(ModerationOperation.Declining, (await bot.Listings.Get(listing.Id, Ct))!.Operation);
        await bot.Router.Handle(BotFixture.Message(100, new string('а', 500)), Ct);
        Assert.Equal(500, (await bot.Listings.Get(listing.Id, Ct))!.DeclineReason!.Length);
    }

    [Fact]
    public async Task BackStopsCollectingTheCustomReason()
    {
        using var bot = new BotFixture();
        var listing = await BeginDecline(bot);
        await bot.Router.Handle(BotFixture.Callback(100, Card(listing, 100),
            new("release", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator)), Ct);
        await bot.Router.Handle(BotFixture.Message(100, "Це вже не причина"), Ct);
        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ModerationOperation.None, listing.Operation);
        Assert.Equal(ListingStatus.PendingApproval, listing.Status);
        Assert.Null(listing.DeclineReason);
    }

    [Fact]
    public async Task MultiplePendingRejectionsRequireAReplyToTheTargetCard()
    {
        using var bot = new BotFixture();
        var first = await BeginDecline(bot);
        var second = await BeginDecline(bot);
        await bot.Router.Handle(BotFixture.Message(100, "Неоднозначна причина"), Ct);
        Assert.Contains(bot.Telegram.SentMessages, message => message.Chat == 100 && message.Text == T.DeclineReplyRequired);
        Assert.Equal(ListingStatus.PendingApproval, (await bot.Listings.Get(first.Id, Ct))!.Status);
        Assert.Equal(ListingStatus.PendingApproval, (await bot.Listings.Get(second.Id, Ct))!.Status);
        var update = BotFixture.Message(100, "Причина для другого оголошення");
        update.Message!.ReplyToMessage = new Message { Id = Card(second, 100), Chat = update.Message.Chat };
        await bot.Router.Handle(update, Ct);
        Assert.Equal(ListingStatus.Declined, (await bot.Listings.Get(second.Id, Ct))!.Status);
        Assert.Equal(ListingStatus.PendingApproval, (await bot.Listings.Get(first.Id, Ct))!.Status);
    }

    [Fact]
    public async Task RetriedDeliveryCannotApplyTheReasonToAnotherPendingListing()
    {
        using var bot = new BotFixture();
        var first = await BeginDecline(bot);
        var update = BotFixture.Message(100, "Уточніть опис");
        bot.Telegram.SendFailure = new HttpRequestException("Delivery failed");
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.Router.Handle(update, Ct));
        Assert.Equal(ListingStatus.Declined, (await bot.Listings.Get(first.Id, Ct))!.Status);
        bot.Telegram.SendFailure = null;
        var second = await BeginDecline(bot);
        await bot.Router.Handle(update, Ct);
        Assert.True((await bot.Listings.Get(first.Id, Ct))!.AuthorNotified);
        Assert.Equal(ListingStatus.PendingApproval, (await bot.Listings.Get(second.Id, Ct))!.Status);
        Assert.Null((await bot.Listings.Get(second.Id, Ct))!.DeclineReason);
    }

    [Fact]
    public async Task CommandsAreNotUsedAsRejectionReasons()
    {
        using var bot = new BotFixture();
        var listing = await BeginDecline(bot);
        await bot.Router.Handle(BotFixture.Message(100, "/help"), Ct);
        Assert.Equal(ModerationOperation.Declining, (await bot.Listings.Get(listing.Id, Ct))!.Operation);
        Assert.Contains(bot.Telegram.SentMessages, message => message.Chat == 100 && message.Text == T.Help);
    }

    private static async Task<Listing> BeginDecline(BotFixture bot)
    {
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct);
        await bot.Moderation.Deliver(listing, Ct);
        await bot.Router.Handle(BotFixture.Callback(100, Card(listing, 100),
            new("decline", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator)), Ct);
        return (await bot.Listings.Get(listing.Id, Ct))!;
    }

    private static int Card(Listing listing, long admin) => listing.ModerationCards.Single(card => card.AdminId == admin).MessageId;
}
