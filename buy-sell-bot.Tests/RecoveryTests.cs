using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Flow;
using BuySellBot.Infrastructure;
using BuySellBot.Moderation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class RecoveryTests
{
    private static CancellationToken Ct => CancellationToken.None;
    [Fact]
    public async Task StartResumesAnInterruptedModeratorBuilder()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        await bot.Router.Handle(BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("edit", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator)), Ct);
        var session = (await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!;
        session.BuilderMessageId = null; session.LastRenderHash = null;
        await bot.Sessions.Save(session, Ct);
        await bot.Router.Handle(BotFixture.Message(100, "/start"), Ct);
        Assert.Equal(SessionMode.Moderator, (await bot.Sessions.GetDocument(100, Ct)).ActiveMode);
        Assert.NotNull((await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!.BuilderMessageId);
    }

    [Fact]
    public async Task MaintenanceRecoversInterruptedPublishAndRemovesStaleSession()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        listing.Operation = ModerationOperation.Publishing; listing.ModeratorId = 100;
        await bot.Listings.Save(listing, Ct);
        await bot.Sessions.Save(new() { UserId = 100, ListingId = listing.Id, Mode = SessionMode.Moderator, WorkingCopy = listing }, Ct);
        using var service = new MaintenanceService(bot.Listings, bot.Sessions, bot.Locks, new(),
            bot.Moderation, bot.Ui, bot.Options, NullLogger<MaintenanceService>.Instance);
        await service.StartAsync(Ct);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (await bot.Sessions.Get(100, SessionMode.Moderator, Ct) is not null && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(25, Ct);
            Assert.Null(await bot.Sessions.Get(100, SessionMode.Moderator, Ct));
            Assert.Equal(ModerationOperation.Uncertain, (await bot.Listings.Get(listing.Id, Ct))!.Operation);
            Assert.DoesNotContain(bot.Telegram.SentMessages, m => m.Thread is not null);
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task UnchangedModerationCardsDoNotCauseEdits()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        var before = bot.Telegram.EditCount;
        await bot.Moderation.RefreshCards(listing, Ct);
        await bot.Moderation.RefreshCards(listing, Ct);
        Assert.Equal(before, bot.Telegram.EditCount);
    }
    [Fact]
    public void KeepDiscardsPhotoAndCurrencyEdits()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Photos.Add(new("file1", "unique1"));
        var session = new BuilderSession();
        bot.Engine.CompleteAll(session);
        bot.Engine.Change(session, listing, "photos");
        bot.Engine.Apply(session, listing, new(Photo: new("file2", "unique2")));
        Assert.Equal(2, listing.Photos.Count);
        bot.Engine.Keep(session, listing);
        Assert.Single(listing.Photos);
        bot.Engine.Change(session, listing, "price");
        bot.Engine.Apply(session, listing, new(Arg: "EUR"));
        Assert.Equal(Currency.EUR, listing.Price!.Currency);
        bot.Engine.Back(session, listing);
        Assert.Equal(Currency.UAH, listing.Price.Currency);
        Assert.Equal(FlowEngine.Review, session.CurrentStepId);
    }

    [Fact]
    public async Task RetriedTextInputDoesNotOverwriteTheNextStep()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "title" };
        await bot.Listings.Save(listing, Ct); await bot.Sessions.Save(session, Ct);
        var update = BotFixture.Message(10, "Назва оголошення");
        await bot.Router.Handle(update, Ct);
        await bot.Router.Handle(update, Ct);
        Assert.Equal("condition", (await bot.Sessions.Get(10, SessionMode.Author, Ct))!.CurrentStepId);
        Assert.Null((await bot.Sessions.Get(10, SessionMode.Author, Ct))!.Warning);
    }

    [Fact]
    public async Task EditedListingPublishesWithAuthorNotification()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        await bot.Router.Handle(BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("edit", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator)), Ct);
        var session = (await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!;
        await bot.Router.Handle(BotFixture.Callback(100, session.BuilderMessageId!.Value,
            new("change", listing.Id, session.Nonce, "title", SessionMode.Moderator)), Ct);
        await bot.Router.Handle(BotFixture.Message(100, "Уточнена назва"), Ct);
        session = (await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!;
        await bot.Router.Handle(BotFixture.Callback(100, session.BuilderMessageId!.Value,
            new("publish", listing.Id, session.Nonce, Mode: SessionMode.Moderator)), Ct);
        var published = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ListingStatus.Published, published.Status);
        Assert.Equal("Уточнена назва", published.Title);
        Assert.True(published.EditedByModerator);
        var confirmation = bot.Telegram.SentMessages.Single(message => message.Chat == 100 && message.Text == T.PublishedStatus);
        var menu = Assert.IsType<Telegram.Bot.Types.ReplyMarkups.ReplyKeyboardMarkup>(confirmation.Keyboard);
        Assert.Equal(new[] { T.Buy, T.Sell }, menu.Keyboard.SelectMany(row => row).Select(button => button.Text));
        Assert.Contains(bot.Telegram.SentMessages, m => m.Chat == 10 && m.Text == T.AuthorPublished(true));
    }

    [Fact]
    public async Task RejectedPublicationDoesNotCommitWorkingCopy()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        await bot.Router.Handle(BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("edit", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator)), Ct);
        var session = (await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!;
        session.WorkingCopy!.Title = "Незбережені зміни";
        await bot.Sessions.Save(session, Ct);
        listing = (await bot.Listings.Get(listing.Id, Ct))!;
        bot.Telegram.PublishFailure = new ApiRequestException("Forbidden", 403);
        await bot.Moderation.PublishEdit(listing, session, Ct);
        Assert.Equal("Велосипед", (await bot.Listings.Get(listing.Id, Ct))!.Title);
        Assert.Equal(ModerationOperation.Editing, (await bot.Listings.Get(listing.Id, Ct))!.Operation);
        Assert.Equal(T.PublicationTelegramRejected(403), (await bot.Sessions.Get(100, SessionMode.Moderator, Ct))!.Warning);
    }

    [Fact]
    public async Task InboxRecoversUnfinishedUpdatesAfterRestart()
    {
        using var bot = new BotFixture();
        var options = Options.Create(new StorageOptions { Path = bot.DirectoryPath });
        var inbox = new UpdateInbox(options);
        var update = BotFixture.Message(10, "/start");
        Assert.True(await inbox.Accept(update, Ct));
        var restarted = new UpdateInbox(options);
        await restarted.Recover(Ct);
        Assert.True(restarted.Channel.Reader.TryRead(out var recovered));
        Assert.Equal(update.Id, recovered!.Id);
        await restarted.Finish(update.Id, true, Ct);
        Assert.True(await restarted.Accept(update, Ct));
        Assert.False(restarted.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task PhotoAlbumIsRenderedOnceAfterTheBatch()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "photos" };
        await bot.Listings.Save(listing, Ct); await bot.Sessions.Save(session, Ct);
        await bot.Ui.Render(session, listing, Ct);
        var rendersBefore = bot.Telegram.EditCount;
        await bot.Photos.StartAsync(Ct);
        try
        {
            for (var n = 0; n < 4; n++)
            {
                var update = BotFixture.Message(10, "");
                update.Message!.Photo = [new() { FileId = "file" + n, FileUniqueId = "unique" + n, Width = 100, Height = 100 }];
                update.Message.MediaGroupId = "album1";
                await bot.Router.Handle(update, Ct);
            }
            await Task.Delay(1000, Ct);
            Assert.Equal(rendersBefore + 1, bot.Telegram.EditCount);
            Assert.Equal(4, (await bot.Listings.Get(listing.Id, Ct))!.Photos.Count);
        }
        finally { await bot.Photos.StopAsync(Ct); }
    }
}
