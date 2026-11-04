using System.Text.Json;
using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using BuySellBot.Moderation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Tests;

public sealed class PublicationFailureTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingDestinationSettingsExplainTheProblemWithoutClaimingTheListing(bool missingChat)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        if (missingChat) bot.Options.Value.ChatId = 0;
        else bot.Catalog.Categories[0] = bot.Catalog.Categories[0] with { TopicId = 0 };
        var callback = BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("approve", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator));

        await bot.Router.Handle(callback, Ct);

        var pending = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ListingStatus.PendingApproval, pending.Status);
        Assert.Equal(ModerationOperation.None, pending.Operation);
        Assert.Null(pending.ModeratorId);
        Assert.Equal(listing.Revision, pending.Revision);
        Assert.DoesNotContain(bot.Telegram.SentMessages, message => message.Thread is not null);
        var setting = missingChat ? "Bot:ChatId" : "Categories:0:TopicId";
        Assert.Contains(bot.Telegram.SentMessages, message => message.Chat == 100 && message.Text!.Contains(setting));

        bot.Options.Value.ChatId = -1001234567890;
        bot.Catalog.Categories[0] = bot.Catalog.Categories[0] with { TopicId = 42 };
        await bot.Router.Handle(callback, Ct);
        Assert.Equal(ListingStatus.Published, (await bot.Listings.Get(listing.Id, Ct))!.Status);
        Assert.Single(bot.Telegram.SentMessages, message => message.Thread == 42);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    public async Task TelegramRejectionsRestoreModerationAndNotifyTheActingAdmin(int code)
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        bot.Telegram.PublishFailure = new ApiRequestException("Publication rejected", code);

        await bot.Router.Handle(BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("approve", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator)), Ct);

        var pending = (await bot.Listings.Get(listing.Id, Ct))!;
        Assert.Equal(ListingStatus.PendingApproval, pending.Status);
        Assert.Equal(ModerationOperation.None, pending.Operation);
        Assert.Null(pending.ModeratorId);
        Assert.Contains(bot.Telegram.SentMessages, message => message.Chat == 100 && message.Text == T.PublicationTelegramRejected(code));
        Assert.DoesNotContain(bot.Telegram.SentMessages, message => message.Thread is not null);
    }

    [Fact]
    public async Task MissingChatConfigurationCompletesTheInboxUpdateWithoutSchedulingARetry()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.Status = ListingStatus.PendingApproval;
        await bot.Listings.Save(listing, Ct); await bot.Moderation.Deliver(listing, Ct);
        bot.Options.Value.ChatId = 0;
        var update = BotFixture.Callback(100, listing.ModerationCards[0].MessageId,
            new("approve", listing.Id, ModerationService.Nonce(listing), Mode: SessionMode.Moderator));
        var inbox = new UpdateInbox(Options.Create(new StorageOptions { Path = bot.DirectoryPath }));
        using var processor = new UpdateProcessor(inbox, bot.Router, new(), bot.Options, NullLogger<UpdateProcessor>.Instance);
        await processor.StartAsync(Ct);
        try
        {
            Assert.True(await inbox.Accept(update, Ct));
            var entryPath = Path.Combine(bot.DirectoryPath, "inbox", update.Id + ".json");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            bool complete;
            do
            {
                await Task.Delay(25, Ct);
                using var entry = JsonDocument.Parse(await File.ReadAllTextAsync(entryPath, Ct));
                complete = entry.RootElement.GetProperty("complete").GetBoolean();
            } while (!complete && DateTimeOffset.UtcNow < deadline);
            Assert.True(complete);
            Assert.Contains(bot.Telegram.SentMessages, message => message.Chat == 100 && message.Text == T.PublicationChatMissing);
        }
        finally { await processor.StopAsync(Ct); }
    }

    [Fact]
    public void ExceptionDiagnosticsRetainTheCauseAndRedactSecrets()
    {
        const string token = "123456:abcdefghijklmnopqrstuvwxyz123456";
        const string secret = "test-webhook-secret-12345";
        var exception = new InvalidOperationException("Outer failure", new HttpRequestException(
            $"POST https://api.telegram.org/bot{token}/sendMessage; secret={secret}"));
        var details = ExceptionDiagnostics.Format(exception, new BotOptions { Token = token, WebhookSecret = secret });
        Assert.Contains("Outer failure", details);
        Assert.Contains("HttpRequestException", details);
        Assert.Contains("[REDACTED]", details);
        Assert.DoesNotContain(token, details);
        Assert.DoesNotContain(secret, details);
    }
}
