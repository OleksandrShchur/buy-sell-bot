using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Flow;
using BuySellBot.Moderation;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class WorkflowTests
{
    private static CancellationToken Ct => CancellationToken.None;
    [Fact]
    public async Task AuthorCompletesBuilderSubmitsAndAdminPublishesExactlyOnce()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.Sell), Ct);
        async Task Click(string action, string arg = "")
        {
            var session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
            await bot.Router.Handle(BotFixture.Callback(10, session.BuilderMessageId!.Value,
                new(action, session.ListingId, session.Nonce, arg)), Ct);
        }
        await Click("choose", "0");
        await bot.Router.Handle(BotFixture.Message(10, "Гарний велосипед"), Ct);
        await Click("skip");
        await bot.Router.Handle(BotFixture.Message(10, "1 500 грн"), Ct);
        await Click("skip");
        await bot.Router.Handle(BotFixture.Message(10, "Київ"), Ct);
        await Click("skip");
        await Click("choose", "telegram");
        var review = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Equal(FlowEngine.Review, review.CurrentStepId);
        await Click("submit");
        Assert.Null(await bot.Sessions.Get(10, SessionMode.Author, Ct));
        var pending = (await bot.Listings.Get(review.ListingId, Ct))!;
        Assert.Equal(ListingStatus.PendingApproval, pending.Status);
        Assert.Equal(2, pending.ModerationCards.Count);
        var first = BotFixture.Callback(100, pending.ModerationCards.Single(c => c.AdminId == 100).MessageId,
            new("approve", pending.Id, ModerationService.Nonce(pending), Mode: SessionMode.Moderator));
        var second = BotFixture.Callback(200, pending.ModerationCards.Single(c => c.AdminId == 200).MessageId,
            new("approve", pending.Id, ModerationService.Nonce(pending), Mode: SessionMode.Moderator));
        await Task.WhenAll(bot.Router.Handle(first, Ct), bot.Router.Handle(second, Ct));
        var published = (await bot.Listings.Get(pending.Id, Ct))!;
        Assert.Equal(ListingStatus.Published, published.Status);
        Assert.Single(bot.Telegram.SentMessages, m => m.Thread == 42);
        Assert.True(published.AuthorNotified);
        Assert.Contains(bot.Telegram.Answers, a => a.Text == T.Stale);
        Assert.All(published.ModerationCards, card =>
        {
            var message = bot.Telegram.SentMessages.Single(m => m.Chat == card.AdminId && m.Id == card.MessageId);
            Assert.Empty(((InlineKeyboardMarkup)message.Keyboard!).InlineKeyboard);
            Assert.Contains(T.PublishedStatus, message.Text);
        });
    }

    [Fact]
    public async Task StaleButtonsDoNothingAndAreAnswered()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.Buy), Ct);
        var session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        var old = BotFixture.Callback(10, session.BuilderMessageId!.Value, new("choose", session.ListingId, session.Nonce, "0"));
        await bot.Router.Handle(old, Ct);
        await bot.Router.Handle(old, Ct);
        Assert.Equal("title", (await bot.Sessions.Get(10, SessionMode.Author, Ct))!.CurrentStepId);
        Assert.Equal(T.Stale, bot.Telegram.Answers.Last().Text);
    }

    [Fact]
    public async Task DeclineClaimsListingAndReopensOnlyForTheAuthor()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        var originalNonce = ModerationService.Nonce(listing);
        await bot.Router.Handle(BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("decline", listing.Id, originalNonce, Mode: SessionMode.Moderator)), Ct);
        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ModerationOperation.Declining, listing.Operation);
        await bot.Router.Handle(BotFixture.Callback(200, listing.ModerationCards[1].MessageId,
            new("approve", listing.Id, originalNonce, Mode: SessionMode.Moderator)), Ct);
        Assert.Equal(T.Stale, bot.Telegram.Answers.Last().Text);
        await bot.Router.Handle(BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("reason", listing.Id, ModerationService.Nonce(listing), "1", SessionMode.Moderator)), Ct);
        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ListingStatus.Declined, listing.Status);
        var nonce = ModerationService.Nonce(listing);
        await bot.Router.Handle(BotFixture.Callback(999, 999, new("resubmit", listing.Id, nonce)), Ct);
        Assert.Equal(T.Stale, bot.Telegram.Answers.Last().Text);
        await bot.Router.Handle(BotFixture.Callback(10, 999, new("resubmit", listing.Id, nonce)), Ct);
        Assert.Equal(ListingStatus.Draft, (await bot.Listings.Get(listing.Id, Ct))!.Status);
        Assert.Equal(FlowEngine.Review, (await bot.Sessions.Get(10, SessionMode.Author, Ct))!.CurrentStepId);
    }

    [Fact]
    public async Task ModeratorEditsWorkingCopyAndCancelRestoresOriginal()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(100, T.Buy), Ct);
        var own = (await bot.Sessions.Get(100, SessionMode.Author, Ct))!;
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        await bot.Router.Handle(BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("edit", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator)), Ct);
        var session = (await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!;
        await bot.Router.Handle(BotFixture.Callback(100, session.BuilderMessageId!.Value,
            new("change", listing.Id, session.Nonce, "title", SessionMode.Moderator)), Ct);
        await bot.Router.Handle(BotFixture.Message(100, "Змінена модератором"), Ct);
        session = (await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!;
        Assert.Equal("Змінена модератором", session.WorkingCopy!.Title);
        Assert.Equal(listing.Title, (await bot.Listings.Get(listing.Id, Ct))!.Title);
        await bot.Router.Handle(BotFixture.Callback(100, session.BuilderMessageId!.Value,
            new("stop", listing.Id, session.Nonce, Mode: SessionMode.Moderator)), Ct);
        Assert.Null(await bot.Sessions.Get(100, SessionMode.Moderator, Ct));
        Assert.Equal(own.ListingId, (await bot.Sessions.Get(100, SessionMode.Author, Ct))!.ListingId);
        Assert.Equal(ModerationOperation.None, (await bot.Listings.Get(listing.Id, Ct))!.Operation);
    }

    [Fact]
    public async Task AmbiguousPublicationIsPersistedAndCannotBeRetried()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        bot.Telegram.PublishFailure = new HttpRequestException("Response lost");
        await Assert.ThrowsAsync<HttpRequestException>(() => bot.Publisher.Publish(listing, Ct));
        var persisted = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ModerationOperation.Uncertain, persisted.Operation);
        bot.Telegram.PublishFailure = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bot.Publisher.Publish(persisted, Ct));
        Assert.DoesNotContain(bot.Telegram.SentMessages, m => m.Thread is not null);
    }

    [Fact]
    public async Task PendingLimitKeepsDraftAvailableForLater()
    {
        using var bot = new BotFixture();
        var pending = bot.Listing(); pending.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(pending, Ct);
        var draft = bot.Listing(); await bot.Listings.Save(draft, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = draft.Id };
        bot.Engine.CompleteAll(session); await bot.Sessions.Save(session, Ct); await bot.Ui.Render(session, draft, Ct);
        await bot.Router.Handle(BotFixture.Callback(10, session.BuilderMessageId!.Value, new("submit", draft.Id, session.Nonce)), Ct);
        Assert.Equal(ListingStatus.Draft, (await bot.Listings.Get(draft.Id, Ct))!.Status);
        Assert.Equal(T.PendingLimit, (await bot.Sessions.Get(10, SessionMode.Author, Ct))!.Warning);
    }
}
