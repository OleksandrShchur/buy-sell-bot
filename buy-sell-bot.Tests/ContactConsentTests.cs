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
        listing.ContactKind = null;
        var text = bot.Formatter.Format(listing);
        Assert.DoesNotContain("@author", text);
        Assert.DoesNotContain("+380501234567", text);
        Assert.DoesNotContain(T.ContactField + ":", text);
        Assert.Equal(T.ContactConsentRequired, bot.Formatter.Validate(listing));
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "contact" };
        bot.Engine.Apply(session, listing, new(Arg: "share"));
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
        Assert.Null(listing.ContactKind);
        bot.Engine.Apply(session, listing, new(Text: "+380 (50) 123-45-67"));
        Assert.Equal(ContactKind.Phone, listing.ContactKind);
        Assert.Contains("+380501234567", bot.Formatter.Format(listing));
        Assert.DoesNotContain("@author", bot.Formatter.Format(listing));
        Assert.Null(bot.Formatter.Validate(listing));
    }

    [Fact]
    public void ShareIsHiddenAndRejectedWithoutAKnownPhoneOrUsername()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.AuthorUsername = null; listing.ContactKind = null;
        var session = new BuilderSession { UserId = 10, CurrentStepId = "contact" };
        var renderer = new BuilderRenderer(bot.Engine, bot.Formatter);
        var view = renderer.Render(listing, session);
        Assert.DoesNotContain(view.Keyboard.InlineKeyboard.SelectMany(row => row), button => button.Text == T.ShareContact);
        Assert.Contains(T.ManualContactPrompt, view.ActionsText);
        bot.Engine.Apply(session, listing, new(Arg: "share"));
        Assert.Null(listing.ContactKind);
        Assert.Equal("contact", session.CurrentStepId);
        Assert.Equal(T.ContactError, session.Warning);
    }

    [Fact]
    public void ModeratorsCannotChooseContactsForTheAuthor()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        var session = new BuilderSession { UserId = 100, Mode = SessionMode.Moderator, CurrentStepId = "contact" };
        bot.Engine.Apply(session, listing, new(Arg: "share"));
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

    [Theory]
    [InlineData(Intent.Buy)]
    [InlineData(Intent.Sell)]
    public void BothIntentsAcceptRangesAndFreePricesWithoutAnIntentStep(Intent intent)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Intent = intent;
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

    [Theory]
    [InlineData("author", null, ContactKind.Telegram)]
    [InlineData(null, "050 123 45 67", ContactKind.Phone)]
    [InlineData("author", "+380501234567", ContactKind.TelegramAndPhone)]
    public void SingleShareButtonPublishesAllAvailableContacts(string? username, string? phone, ContactKind expected)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.AuthorUsername = username; listing.ContactPhone = phone; listing.ContactKind = null;
        var session = new BuilderSession { UserId = 10, CurrentStepId = "contact" };
        var view = new BuilderRenderer(bot.Engine, bot.Formatter).Render(listing, session);
        var choices = view.Keyboard.InlineKeyboard.SelectMany(row => row)
            .Where(button => CallbackData.TryParse(button.CallbackData, out var data) && data!.Action == "choose");
        Assert.Equal(T.ShareContact, Assert.Single(choices).Text);
        Assert.DoesNotContain(T.ContactField + ":", bot.Formatter.Format(listing));

        bot.Engine.Apply(session, listing, new(Arg: "share"));

        Assert.Equal(expected, listing.ContactKind);
        Assert.Equal(FlowEngine.Review, session.CurrentStepId);
        Assert.Null(bot.Formatter.Validate(listing));
        var text = bot.Formatter.Format(listing);
        Assert.Equal(username is not null, text.Contains("@author"));
        Assert.Equal(phone is not null, text.Contains("+380501234567"));
    }

    [Theory]
    [InlineData("+48123456789")]
    [InlineData("1234567")]
    [InlineData("05012345678")]
    public void InvalidPhoneCannotAdvanceOrPassPublicationValidation(string phone)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        var session = new BuilderSession { UserId = 10, CurrentStepId = "contact" };
        bot.Engine.Apply(session, listing, new(Text: phone));
        Assert.Equal("contact", session.CurrentStepId);
        Assert.Equal(T.ContactError, session.Warning);
        Assert.Null(listing.ContactKind);
        listing.ContactPhone = phone; listing.ContactKind = ContactKind.Phone;
        Assert.Equal(T.ContactError, bot.Formatter.Validate(listing));
    }

    [Fact]
    public async Task EnteredPhoneIsRememberedAcrossListingsAndSharedWithTheCurrentUsername()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.AuthorUsername = null; listing.ContactKind = null;
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "contact" };
        await bot.Ui.Render(session, listing, Ct);
        await bot.Router.Handle(BotFixture.Message(10, "(050) 123-45-67", username: null), Ct);
        Assert.Equal("+380501234567", (await bot.Sessions.GetDocument(10, Ct)).ContactPhone);
        Assert.Equal(ContactKind.Phone, (await bot.Listings.Get(listing.Id, Ct))!.ContactKind);
        await bot.Sessions.Delete(10, SessionMode.Author, Ct);
        await bot.Router.Handle(BotFixture.Message(10, T.Sell, username: "new_username"), Ct);
        session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        listing = (await bot.Listings.Get(session.ListingId, Ct))!;
        Assert.Equal("+380501234567", listing.ContactPhone);
        Assert.Null(listing.ContactKind);
        Assert.DoesNotContain("+380501234567", bot.Formatter.Format(listing));
        session.CurrentStepId = "contact";
        await bot.Ui.Render(session, listing, Ct);

        await bot.Router.Handle(BotFixture.Callback(10, session.BuilderMessageId!.Value,
            new("choose", listing.Id, session.Nonce, "share"), username: "current_username"), Ct);

        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ContactKind.TelegramAndPhone, listing.ContactKind);
        Assert.Contains("@current_username, +380501234567", bot.Formatter.Format(listing));
    }

    [Fact]
    public async Task RemovedUsernameCannotBeSharedByAnOldButton()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.ContactKind = null;
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "contact" };
        await bot.Ui.Render(session, listing, Ct);
        await bot.Router.Handle(BotFixture.Callback(10, session.BuilderMessageId!.Value,
            new("choose", listing.Id, session.Nonce, "share"), username: null), Ct);
        session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal("contact", session.CurrentStepId);
        Assert.Null(listing.ContactKind);
        Assert.Null(listing.AuthorUsername);
        var actions = bot.Telegram.SentMessages.Single(message => message.Id == session.BuilderMessageId);
        Assert.Contains(T.ManualContactPrompt, actions.Text);
    }
}
