using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Flow;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class ContactConsentTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public void ProfileAndPhoneAreHiddenUntilTheAuthorChoosesAContact()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.ContactKind = null; listing.ContactPhone = "+380501234567";
        var text = bot.Formatter.Format(listing);
        Assert.DoesNotContain("@author", text);
        Assert.DoesNotContain("+380501234567", text);
        Assert.DoesNotContain(T.ContactField + ":", text);
        Assert.Equal(T.ContactConsentRequired, bot.Formatter.Validate(listing));
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "contact" };
        bot.Engine.Apply(session, listing, new(Arg: "telegram"));
        Assert.Equal(ContactKind.Telegram, listing.ContactKind);
        Assert.Null(listing.ContactPhone);
        Assert.Contains("@author", bot.Formatter.Format(listing));
        Assert.Equal(FlowEngine.Review, session.CurrentStepId);
    }

    [Fact]
    public void ChoosingPhoneDoesNotPublishTheTelegramUsername()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "contact" };
        bot.Engine.Apply(session, listing, new(Arg: "phone"));
        Assert.True(session.ContactPhoneRequested);
        Assert.Null(listing.ContactKind);
        bot.Engine.Apply(session, listing, new(Text: "+380 (50) 123-45-67"));
        Assert.Equal(ContactKind.Phone, listing.ContactKind);
        Assert.Contains("+380501234567", bot.Formatter.Format(listing));
        Assert.DoesNotContain("@author", bot.Formatter.Format(listing));
        Assert.Null(bot.Formatter.Validate(listing));
    }

    [Fact]
    public void TelegramProfileCanBeChosenWithoutAUsername()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.AuthorUsername = null; listing.ContactKind = null;
        var session = new BuilderSession { UserId = 10, CurrentStepId = "contact" };
        bot.Engine.Apply(session, listing, new(Arg: "telegram"));
        Assert.Contains("tg://user?id=10", bot.Formatter.Format(listing));
        Assert.Null(bot.Formatter.Validate(listing));
    }

    [Fact]
    public void ModeratorsCannotChooseContactsForTheAuthor()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        var session = new BuilderSession { UserId = 100, Mode = SessionMode.Moderator, CurrentStepId = "contact" };
        bot.Engine.Apply(session, listing, new(Arg: "telegram"));
        Assert.Null(listing.ContactKind);
        Assert.Equal(T.ContactConsentRequired, session.Warning);
        Assert.False(bot.Engine.Change(session, listing, "contact"));
        session.CurrentStepId = FlowEngine.Changes;
        var renderer = new BuilderRenderer(bot.Engine, bot.Formatter);
        Assert.DoesNotContain(renderer.Render(listing, session).Keyboard.InlineKeyboard.SelectMany(row => row),
            button => button.Text == T.ContactLabel);
    }

    [Fact]
    public async Task ResumingAnOldReviewRequiresContactConsent()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id };
        bot.Engine.CompleteAll(session);
        await bot.Sessions.Save(session, Ct);
        await bot.Router.Handle(BotFixture.Message(10, "/start"), Ct);
        session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Equal("contact", session.CurrentStepId);
        Assert.True(session.ChangeMode);
        var text = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == session.ProductMessageIds[0]).Text;
        Assert.DoesNotContain("@author", text);
    }

    [Fact]
    public void UnifiedListingsAcceptRangesAndFreePricesWithoutAnIntentStep()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Intent = Intent.BuyOrSell;
        var session = new BuilderSession { UserId = 10, CurrentStepId = "price" };
        bot.Engine.Apply(session, listing, new(Text: "1000-2000"));
        Assert.Equal(2000m, listing.Price!.AmountTo);
        Assert.Null(bot.Formatter.Validate(listing));
        session.CurrentStepId = "price";
        bot.Engine.Apply(session, listing, new(Arg: "free"));
        Assert.Equal(PriceKind.Free, listing.Price!.Kind);
        Assert.Null(bot.Formatter.Validate(listing));
        Assert.Equal(8, bot.Engine.Steps.Count);
    }

    [Fact]
    public async Task SubmittingAnOlderDraftWithoutConsentOpensTheContactStep()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id };
        bot.Engine.CompleteAll(session);
        await bot.Ui.Render(session, listing, Ct);
        await bot.Router.Handle(BotFixture.Callback(10, session.BuilderMessageId!.Value,
            new("submit", listing.Id, session.Nonce)), Ct);
        session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Equal("contact", session.CurrentStepId);
        Assert.Equal(ListingStatus.Draft, (await bot.Listings.Get(listing.Id, Ct))!.Status);
        Assert.DoesNotContain(bot.Telegram.SentMessages, message => message.Chat == 100);
    }

    [Fact]
    public async Task SendingTheAuthorsOwnTelegramContactRecordsThePhoneChoice()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "contact" };
        await bot.Ui.Render(session, listing, Ct);
        var update = BotFixture.Message(10, "");
        update.Message!.Contact = new() { FirstName = "Автор", PhoneNumber = "+380501234567", UserId = 10 };
        await bot.Router.Handle(update, Ct);
        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ContactKind.Phone, listing.ContactKind);
        Assert.Equal("+380501234567", listing.ContactPhone);
        Assert.DoesNotContain("@author", bot.Formatter.Format(listing));
        Assert.Equal(FlowEngine.Review, (await bot.Sessions.Get(10, SessionMode.Author, Ct))!.CurrentStepId);
    }
}
