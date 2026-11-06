using BuySellBot.Bot;
using BuySellBot.Domain;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class BuilderRecoveryTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task StartRestoresAHiddenPromptAndClearsPendingCancellation()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.Sell), Ct);
        var before = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        var oldId = before.BuilderMessageId!.Value;
        bot.Telegram.HiddenMessageIds.Add(oldId);
        before.Confirmation = "cancel";
        before.RestartIntent = Intent.BuyOrSell;
        await bot.Sessions.Save(before, Ct);

        await bot.Router.Handle(BotFixture.Message(10, "/start"), Ct);

        var after = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        var newId = after.BuilderMessageId!.Value;
        Assert.Equal(before.ListingId, after.ListingId);
        Assert.Equal("category", after.CurrentStepId);
        Assert.NotEqual(oldId, newId);
        Assert.Null(after.Confirmation);
        Assert.Null(after.RestartIntent);
        Assert.NotEqual(before.Nonce, after.Nonce);
        Assert.DoesNotContain(newId, bot.Telegram.HiddenMessageIds);
        Assert.DoesNotContain(bot.Telegram.Deleted, message => message.Chat == 10 && message.Id == newId);
        Assert.Contains(bot.Telegram.Deleted, message => message.Chat == 10 && message.Id == oldId);
        Assert.True(bot.Telegram.Operations.IndexOf("send:" + newId) < bot.Telegram.Operations.IndexOf("delete:" + oldId));
        var prompt = bot.Telegram.SentMessages.Single(message => message.Chat == 10 && message.Id == newId);
        Assert.Contains(T.CategoryPrompt, prompt.Text);
        Assert.DoesNotContain(T.CancelQuestion, prompt.Text);
        Assert.IsType<InlineKeyboardMarkup>(prompt.Keyboard);
    }

    [Fact]
    public async Task StartRestoresAHiddenProductAndActionsWithoutLosingDraftFields()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.Photos = [new("file1", "unique1"), new("file2", "unique2")];
        await bot.Listings.Save(listing, Ct);
        var session = new BuilderSession { UserId = 10, ListingId = listing.Id, CurrentStepId = "description" };
        await bot.Ui.Render(session, listing, Ct);
        var oldIds = session.ProductMessageIds.Append(session.BuilderMessageId!.Value).ToArray();
        foreach (var id in oldIds) bot.Telegram.HiddenMessageIds.Add(id);

        await bot.Router.Handle(BotFixture.Message(10, "/start"), Ct);

        session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        var newIds = session.ProductMessageIds.Append(session.BuilderMessageId!.Value).ToArray();
        Assert.All(newIds, id => Assert.DoesNotContain(id, bot.Telegram.HiddenMessageIds));
        Assert.All(newIds, id => Assert.DoesNotContain(bot.Telegram.Deleted, message => message.Id == id));
        Assert.All(oldIds, id => Assert.Contains(bot.Telegram.Deleted, message => message.Id == id));
        Assert.All(oldIds, id => Assert.True(bot.Telegram.Operations.IndexOf("send:" + session.BuilderMessageId) <
            bot.Telegram.Operations.IndexOf("delete:" + id)));
        Assert.Equal("description", session.CurrentStepId);
        var restored = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(listing.Title, restored.Title);
        Assert.Equal(listing.Price, restored.Price);
        Assert.Equal(listing.Photos, restored.Photos);
        Assert.Equal(bot.Formatter.Format(listing), bot.Telegram.SentMessages.Single(message =>
            message.Chat == 10 && message.Id == session.ProductMessageIds[0]).Text);
    }

    [Fact]
    public async Task CancelCommandShowsAVisibleConfirmationWhenTheSavedPromptIsHidden()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.Sell), Ct);
        var before = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        bot.Telegram.HiddenMessageIds.Add(before.BuilderMessageId!.Value);

        await bot.Router.Handle(BotFixture.Message(10, "/cancel"), Ct);

        var session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.NotEqual(before.BuilderMessageId, session.BuilderMessageId);
        Assert.Equal("cancel", session.Confirmation);
        var prompt = bot.Telegram.SentMessages.Single(message => message.Chat == 10 && message.Id == session.BuilderMessageId);
        Assert.Contains(T.CancelQuestion, prompt.Text);
        Assert.DoesNotContain(prompt.Id, bot.Telegram.HiddenMessageIds);
        await bot.Router.Handle(BotFixture.Callback(10, prompt.Id, new("confirm", session.ListingId, session.Nonce)), Ct);
        Assert.Null(await bot.Sessions.Get(10, SessionMode.Author, Ct));
        Assert.Equal(ListingStatus.Cancelled, (await bot.Listings.Get(session.ListingId, Ct))!.Status);
        Assert.IsType<ReplyKeyboardMarkup>(bot.Telegram.LastReplyKeyboard);
    }

    [Fact]
    public async Task StartReturnsTheMainMenuWhenTheSavedSessionIsNoLongerUsable()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.Cancelled;
        await bot.Listings.Save(listing, Ct);
        await bot.Sessions.Save(new() { UserId = 10, ListingId = listing.Id, CurrentStepId = "category" }, Ct);

        await bot.Router.Handle(BotFixture.Message(10, "/start"), Ct);

        Assert.Null(await bot.Sessions.Get(10, SessionMode.Author, Ct));
        Assert.Contains(bot.Telegram.SentMessages, message => message.Chat == 10 && message.Text == T.Welcome(bot.Options.Value.ChatUrl));
        var menu = Assert.IsType<ReplyKeyboardMarkup>(bot.Telegram.LastReplyKeyboard);
        Assert.Equal(new[] { T.Buy, T.Sell }, menu.Keyboard.SelectMany(row => row).Select(button => button.Text));
    }

    [Fact]
    public async Task FailedRecoveryKeepsThePreviousViewAndUserCommandUntilRetrySucceeds()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.Sell), Ct);
        var before = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        var oldId = before.BuilderMessageId!.Value;
        var command = BotFixture.Message(10, "/start");
        bot.Telegram.SendFailure = new HttpRequestException("Temporary failure");

        await Assert.ThrowsAsync<HttpRequestException>(() => bot.Router.Handle(command, Ct));

        Assert.DoesNotContain(bot.Telegram.Deleted, message => message.Chat == 10 && (message.Id == oldId || message.Id == command.Message!.Id));
        Assert.Equal(oldId, (await bot.Sessions.Get(10, SessionMode.Author, Ct))!.BuilderMessageId);
        bot.Telegram.SendFailure = null;
        await bot.Router.Handle(command, Ct);
        var after = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.NotEqual(oldId, after.BuilderMessageId);
        Assert.Equal(before.ListingId, after.ListingId);
        Assert.Contains(bot.Telegram.Deleted, message => message.Chat == 10 && message.Id == command.Message!.Id);
        Assert.DoesNotContain(bot.Telegram.Deleted, message => message.Chat == 10 && message.Id == after.BuilderMessageId);
    }

    [Fact]
    public async Task RepeatedStartLeavesOneVisiblePromptAndNoEmptyProductMessage()
    {
        using var bot = new BotFixture();
        await bot.Router.Handle(BotFixture.Message(10, T.Sell), Ct);
        for (var n = 0; n < 3; n++) await bot.Router.Handle(BotFixture.Message(10, "/start"), Ct);
        var session = (await bot.Sessions.Get(10, SessionMode.Author, Ct))!;
        Assert.Empty(session.ProductMessageIds);
        Assert.Single(bot.Telegram.SentMessages, message => message.Chat == 10 &&
            message.Text?.StartsWith(T.BuilderHeader, StringComparison.Ordinal) == true &&
            !bot.Telegram.Deleted.Contains((10, message.Id)));
        Assert.DoesNotContain(bot.Telegram.Deleted, message => message.Chat == 10 && message.Id == session.BuilderMessageId);
    }
}
