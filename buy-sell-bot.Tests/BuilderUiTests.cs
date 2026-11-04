using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Flow;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class BuilderUiTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task SingleStartButtonIsRemovedAndProductAndActionsAreSeparate()
    {
        using var bot = new BotFixture();
        bot.Options.Value.ChatUrl = "https://t.me/test_community";
        await bot.Router.Handle(BotFixture.Message(10, "/start"), Ct);
        Assert.Contains("<a href=\"https://t.me/test_community\">у спільноті</a>", Assert.Single(bot.Telegram.SentMessages).Text);
        var menu = Assert.IsType<ReplyKeyboardMarkup>(bot.Telegram.LastReplyKeyboard);
        Assert.Equal(T.CreateListing, Assert.Single(Assert.Single(menu.Keyboard)).Text);
        await bot.Router.Handle(BotFixture.Message(10, T.CreateListing), Ct);
        var session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        var listing = (await bot.Listings.Get(session.ListingId, Ct))!;
        Assert.Equal(Intent.BuyOrSell, listing.Intent);
        Assert.Equal("category", session.CurrentStepId);
        Assert.IsType<ReplyKeyboardRemove>(bot.Telegram.LastReplyKeyboard);
        var productId = Assert.Single(session.ProductMessageIds);
        Assert.True(productId < session.BuilderMessageId);
        var product = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == productId);
        var actions = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == session.BuilderMessageId);
        Assert.Equal(T.PostPlaceholder, product.Text);
        Assert.IsNotType<InlineKeyboardMarkup>(product.Keyboard);
        Assert.Contains("Крок 1 з 8", actions.Text);
        Assert.Contains(T.CategoryPrompt, actions.Text);
        Assert.DoesNotContain(T.PostPlaceholder, actions.Text);
        Assert.DoesNotContain("▰", actions.Text);
        Assert.DoesNotContain("▱", actions.Text);
        Assert.DoesNotContain("0/10", actions.Text);
        var buttons = Assert.IsType<InlineKeyboardMarkup>(actions.Keyboard).InlineKeyboard.SelectMany(row => row);
        Assert.DoesNotContain(buttons, button => button.Text == T.Back);
        Assert.Contains(buttons, button => button.Text == T.Cancel);
    }

    [Fact]
    public async Task EnteringFieldsEditsTheSameProductAndActionsMessages()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.CreateListing), Ct);
        var session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        var productId = Assert.Single(session.ProductMessageIds);
        var actionsId = session.BuilderMessageId!.Value;
        await bot.Router.Handle(BotFixture.Callback(10, actionsId, new("choose", session.ListingId, session.Nonce, "0")), Ct);
        await bot.Router.Handle(BotFixture.Message(10, "Велосипед для міста"), Ct);
        session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Equal(productId, Assert.Single(session.ProductMessageIds));
        Assert.Equal(actionsId, session.BuilderMessageId);
        Assert.DoesNotContain(bot.Telegram.Deleted, m => m.Id == productId || m.Id == actionsId);
        var product = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == productId);
        var actions = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == actionsId);
        Assert.Contains("Велосипед для міста", product.Text);
        Assert.Contains(T.ListingTag, product.Text);
        Assert.DoesNotContain(T.SharedConditionPrompt, product.Text);
        Assert.DoesNotContain("@author", product.Text);
        Assert.DoesNotContain(T.ContactField + ":", product.Text);
        Assert.Contains("Крок 3 з 8", actions.Text);
        Assert.Contains(Assert.IsType<InlineKeyboardMarkup>(actions.Keyboard).InlineKeyboard.SelectMany(row => row), button => button.Text == T.Back);
        Assert.DoesNotContain("Велосипед для міста", actions.Text);
    }

    [Fact]
    public async Task FirstPhotoIsAttachedToTheExistingProductWithItsCaption()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "photos" };
        await bot.Ui.Render(session, listing, Ct);
        var postId = Assert.Single(session.ProductMessageIds);
        var actionsId = session.BuilderMessageId;
        listing.Photos.Add(new("file1", "unique1"));
        await bot.Ui.Render(session, listing, Ct);
        Assert.Equal(postId, Assert.Single(session.ProductMessageIds));
        Assert.Equal(actionsId, session.BuilderMessageId);
        Assert.Equal(postId, Assert.Single(bot.Telegram.PhotoEdits).Id);
        var product = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == postId);
        Assert.Equal(1, product.PhotoCount);
        Assert.Equal(bot.Formatter.Format(listing), product.Text);
        Assert.DoesNotContain(bot.Telegram.Deleted, m => m.Id == postId);
    }

    [Fact]
    public async Task AlbumCarriesProductTextAndActionsFollowItWithFreshCallbacks()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "photos" };
        await bot.Ui.Render(session, listing, Ct);
        var oldProduct = Assert.Single(session.ProductMessageIds);
        var oldActions = session.BuilderMessageId!.Value;
        var oldNonce = session.Nonce;
        listing.Photos = [new("file1", "unique1"), new("file2", "unique2")];
        await bot.Listings.Save(listing, Ct);
        await bot.Ui.Render(session, listing, Ct);
        Assert.Equal(2, session.ProductMessageIds.Count);
        Assert.All(session.ProductMessageIds, id => Assert.True(id < session.BuilderMessageId));
        Assert.NotEqual(oldNonce, session.Nonce);
        Assert.Contains(bot.Telegram.Deleted, m => m.Id == oldProduct);
        Assert.Contains(bot.Telegram.Deleted, m => m.Id == oldActions);
        var first = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == session.ProductMessageIds[0]);
        var second = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == session.ProductMessageIds[1]);
        Assert.Equal(bot.Formatter.Format(listing), first.Text);
        Assert.Null(second.Text);
        Assert.Null(first.Keyboard);
        await bot.Router.Handle(BotFixture.Callback(10, oldActions, new("choose", listing.Id, oldNonce, "done")), Ct);
        Assert.Equal(T.Stale, bot.Telegram.Answers.Last().Text);
    }

    [Fact]
    public async Task TextChangesAndReviewEditTheAlbumCaptionWithoutResendingPhotos()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = [new("file1", "unique1"), new("file2", "unique2")];
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "location" };
        await bot.Ui.Render(session, listing, Ct);
        var ids = session.ProductMessageIds.ToArray();
        var actions = session.BuilderMessageId;
        listing.Location = "Львів";
        session.CurrentStepId = "description";
        await bot.Ui.Render(session, listing, Ct);
        session.CurrentStepId = FlowEngine.Review;
        await bot.Ui.Render(session, listing, Ct);
        session.CurrentStepId = FlowEngine.Changes;
        await bot.Ui.Render(session, listing, Ct);
        Assert.Equal(ids, session.ProductMessageIds);
        Assert.Equal(actions, session.BuilderMessageId);
        Assert.Single(bot.Telegram.PhotoSends);
        Assert.Equal(ids[0], Assert.Single(bot.Telegram.CaptionEdits).Id);
        Assert.DoesNotContain(bot.Telegram.Deleted, m => ids.Contains(m.Id));
        var product = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == ids[0]);
        Assert.Contains("Львів", product.Text);
    }

    [Fact]
    public async Task MissingAlbumCaptionRecreatesTheAlbumAndKeepsActionsBelowIt()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = [new("file1", "unique1"), new("file2", "unique2")];
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "location" };
        await bot.Ui.Render(session, listing, Ct);
        var oldIds = session.ProductMessageIds.ToArray();
        bot.Telegram.MissingMessageIds.Add(oldIds[0]);
        listing.Location = "Одеса";
        await bot.Ui.Render(session, listing, Ct);
        Assert.All(oldIds, id => Assert.DoesNotContain(id, session.ProductMessageIds));
        Assert.All(oldIds, id => Assert.Contains(bot.Telegram.Deleted, m => m.Id == id));
        Assert.All(session.ProductMessageIds, id => Assert.True(id < session.BuilderMessageId));
        Assert.Equal(2, bot.Telegram.PhotoSends.Count);
    }

    [Fact]
    public async Task RemovingAllPhotosRestoresATextPostAndHidesTheEmptyPhotoCount()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = [new("file1", "unique1"), new("file2", "unique2")];
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "photos" };
        await bot.Ui.Render(session, listing, Ct);
        var oldIds = session.ProductMessageIds.ToArray();
        listing.Photos.Clear();
        await bot.Ui.Render(session, listing, Ct);
        var post = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == Assert.Single(session.ProductMessageIds));
        Assert.Equal(0, post.PhotoCount);
        Assert.Equal(bot.Formatter.Format(listing), post.Text);
        Assert.All(oldIds, id => Assert.Contains(bot.Telegram.Deleted, m => m.Id == id));
        var actions = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == session.BuilderMessageId);
        Assert.DoesNotContain("0/10", actions.Text);
    }

    [Fact]
    public async Task SubmissionKeepsTheProductAndRemovesOnlyItsActions()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = [new("file1", "unique1"), new("file2", "unique2")];
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id };
        bot.Engine.CompleteAll(session);
        await bot.Ui.Render(session, listing, Ct);
        var ids = session.ProductMessageIds.ToArray();
        var actions = session.BuilderMessageId!.Value;
        await bot.Router.Handle(BotFixture.Callback(10, actions, new("submit", listing.Id, session.Nonce)), Ct);
        Assert.Null(await bot.Sessions.Get(10, SessionMode.Author, Ct));
        Assert.All(ids, id => Assert.DoesNotContain(bot.Telegram.Deleted, m => m.Chat == 10 && m.Id == id));
        Assert.Contains(bot.Telegram.Deleted, m => m.Chat == 10 && m.Id == actions);
        Assert.Equal(ListingStatus.PendingApproval, (await bot.Listings.Get(listing.Id, Ct))!.Status);
    }

    [Fact]
    public async Task OldCombinedBuilderAndCaptionlessPreviewAreReplacedByTheNewLayout()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = [new("file1", "unique1"), new("file2", "unique2")];
        await bot.Listings.Save(listing, Ct);
        var oldPhotos = await bot.Telegram.SendPhotos(10, listing.Photos, null, Ct);
        var oldBuilder = await bot.Telegram.SendText(10, "Старий формат", null, Ct);
        var session = new BuilderSession
        {
            UserId = 10, ListingId = listing.Id, CurrentStepId = "location",
            BuilderMessageId = oldBuilder, ExtraMessageIds = oldPhotos.ToList()
        };
        await bot.Sessions.Save(session, Ct);
        await bot.Router.Handle(BotFixture.Message(10, "/start"), Ct);
        session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Contains(bot.Telegram.Deleted, m => m.Chat == 10 && m.Id == oldBuilder);
        Assert.All(oldPhotos, id => Assert.Contains(bot.Telegram.Deleted, m => m.Chat == 10 && m.Id == id));
        Assert.Empty(session.ExtraMessageIds);
        Assert.Equal(2, session.ProductMessageIds.Count);
        Assert.All(session.ProductMessageIds, id => Assert.True(id < session.BuilderMessageId));
        var caption = bot.Telegram.SentMessages.Single(m => m.Chat == 10 && m.Id == session.ProductMessageIds[0]).Text;
        Assert.Equal(bot.Formatter.Format(listing), caption);
        Assert.Equal("location", session.CurrentStepId);
    }
}
