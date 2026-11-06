using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Flow;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class PhotoUploadTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void DoneIsOnlyAvailableForOneToNinePhotos(int count, bool showDone)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = Enumerable.Range(0, count).Select(n => new ListingPhoto($"file{n}", $"unique{n}")).ToList();
        var session = new BuilderSession { CurrentStepId = "photos" };
        var view = new BuilderRenderer(bot.Engine, bot.Formatter).Render(listing, session);
        var buttons = view.Keyboard.InlineKeyboard.SelectMany(row => row).ToArray();
        Assert.Equal(showDone, buttons.Any(button => button.Text == T.PhotoDone));
        Assert.DoesNotContain(buttons, button => CallbackData.TryParse(button.CallbackData, out var data) && data!.Arg == "remove");
    }

    [Theory]
    [InlineData("done")]
    [InlineData("remove")]
    public void EmptyPhotoStepRejectsRemovedOrUnavailableActions(string action)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession { CurrentStepId = "photos" };
        bot.Engine.Apply(session, listing, new(Arg: action));
        Assert.Equal("photos", session.CurrentStepId);
        Assert.NotNull(session.Warning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task UploadRendersDoneOrAdvancesAndRendersTheNextStep(int count)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "photos" };
        await bot.Listings.Save(listing, Ct);
        await bot.Ui.Render(session, listing, Ct);
        for (var n = 0; n < count; n++) await Upload(bot, n, count == 1 ? null : "album1");

        await bot.Photos.StartAsync(Ct);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do
            {
                // Observe a completed render, not the intermediate product save.
                using (await bot.Locks.Acquire(listing.Id, timeout.Token))
                    session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
                if (session.ProductPhotoIds.Count == count) break;
                await Task.Delay(50, timeout.Token);
            } while (true);

            Assert.Equal(count, (await bot.Listings.Get(listing.Id, Ct))!.Photos.Count);
            var actions = bot.Telegram.SentMessages.Single(message => message.Id == session.BuilderMessageId);
            var buttons = Assert.IsType<InlineKeyboardMarkup>(actions.Keyboard).InlineKeyboard.SelectMany(row => row).ToArray();
            Assert.Equal(count < 10, buttons.Any(button => button.Text == T.PhotoDone));
            Assert.Equal(count == 10 ? "location" : "photos", session.CurrentStepId);
            if (count < 10)
            {
                await bot.Router.Handle(BotFixture.Callback(10, session.BuilderMessageId!.Value,
                    new("choose", listing.Id, session.Nonce, "done")), Ct);
                Assert.Equal("location", (await bot.Sessions.Get(10, SessionMode.Author, Ct))!.CurrentStepId);
            }
            else Assert.Contains(T.LocationPrompt, actions.Text);
        }
        finally { await bot.Photos.StopAsync(Ct); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("album2")]
    public async Task NewUploadReplacesThePreviousSelection(string? nextAlbum)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = [new("old", "old")];
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "photos" };
        await bot.Listings.Save(listing, Ct);
        await bot.Ui.Render(session, listing, Ct);
        await Upload(bot, 1, "album1");
        await Upload(bot, 2, "album1");
        Assert.Equal(2, (await bot.Listings.Get(listing.Id, Ct))!.Photos.Count);
        await Upload(bot, 3, nextAlbum);
        Assert.Equal("unique3", Assert.Single((await bot.Listings.Get(listing.Id, Ct))!.Photos).FileUniqueId);
    }

    [Fact]
    public void TenPhotosCompleteAnEditAndReturnToReview()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession();
        bot.Engine.CompleteAll(session);
        bot.Engine.Change(session, listing, "photos");
        for (var n = 0; n < 10; n++) bot.Engine.Apply(session, listing, new(Photo: new($"file{n}", $"unique{n}")));
        Assert.Equal(FlowEngine.Review, session.CurrentStepId);
        Assert.False(session.ChangeMode);
    }

    private static Task Upload(BotFixture bot, int n, string? album)
    {
        var update = BotFixture.Message(10, "");
        update.Message!.Photo = [new() { FileId = $"file{n}", FileUniqueId = $"unique{n}", Width = 100, Height = 100 }];
        update.Message.MediaGroupId = album;
        return bot.Router.Handle(update, Ct);
    }
}
