using BuySellBot.Domain;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Infrastructure;

public interface ITelegramGateway
{
    Task<int> SendText(long chatId, string text, ReplyMarkup? keyboard, CancellationToken ct, int? threadId = null);
    Task<int> EditText(long chatId, int? messageId, string text, InlineKeyboardMarkup? keyboard, CancellationToken ct);
    Task<int?> EditCaption(long chatId, int messageId, string caption, CancellationToken ct);
    Task<int?> EditPhoto(long chatId, int messageId, ListingPhoto photo, string caption, CancellationToken ct);
    Task<IReadOnlyList<int>> SendPhotos(long chatId, IReadOnlyList<ListingPhoto> photos, string? caption, CancellationToken ct, int? threadId = null);
    Task Delete(long chatId, int messageId, CancellationToken ct);
    Task Answer(string callbackId, string? text, CancellationToken ct);
    Task<bool> Notify(long chatId, string text, ReplyMarkup? keyboard, CancellationToken ct);
    Task Configure(CancellationToken ct);
}

public sealed class TelegramGateway(ITelegramBotClient client, IOptions<BotOptions> options, ILogger<TelegramGateway> logger) : ITelegramGateway
{
    private async Task<T> Retry<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await call(); }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && attempt < 5)
            {
                var seconds = Math.Max(1, ex.Parameters?.RetryAfter ?? 1);
                logger.LogWarning("Telegram rate limit; retrying in {Seconds}s", seconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
        }
    }
    public async Task<int> SendText(long chatId, string text, ReplyMarkup? keyboard, CancellationToken ct, int? threadId = null) =>
        (await Retry(() => client.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard,
            linkPreviewOptions: new() { IsDisabled = true }, messageThreadId: threadId, cancellationToken: ct), ct)).Id;

    public async Task<int> EditText(long chatId, int? messageId, string text, InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        if (messageId is null) return await SendText(chatId, text, keyboard, ct);
        try
        {
            await Retry(() => client.EditMessageText(chatId, messageId.Value, text, parseMode: ParseMode.Html,
                replyMarkup: keyboard, linkPreviewOptions: new() { IsDisabled = true }, cancellationToken: ct), ct);
            return messageId.Value;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        { return messageId.Value; }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
            (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("can't be edited", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("message_id_invalid", StringComparison.OrdinalIgnoreCase)))
        {
            return await SendText(chatId, text, keyboard, ct);
        }
    }
    public async Task<IReadOnlyList<int>> SendPhotos(long chatId, IReadOnlyList<ListingPhoto> photos, string? caption, CancellationToken ct, int? threadId = null)
    {
        if (photos.Count == 0) return [];
        if (photos.Count > 10) throw new ArgumentOutOfRangeException(nameof(photos));
        if (photos.Count == 1)
        {
            var photo = await Retry(() => client.SendPhoto(chatId, InputFile.FromFileId(photos[0].FileId), caption: caption,
                parseMode: ParseMode.Html, messageThreadId: threadId, cancellationToken: ct), ct);
            return [photo.Id];
        }
        var media = photos.Select((p, n) => (IAlbumInputMedia)new InputMediaPhoto(InputFile.FromFileId(p.FileId))
        { Caption = n == 0 ? caption : null, ParseMode = ParseMode.Html }).ToArray();
        var messages = await Retry(() => client.SendMediaGroup(chatId, media, messageThreadId: threadId, cancellationToken: ct), ct);
        return messages.Select(m => m.Id).ToArray();
    }
    public async Task<int?> EditCaption(long chatId, int messageId, string caption, CancellationToken ct)
    {
        try
        {
            await Retry(() => client.EditMessageCaption(chatId, messageId, caption, parseMode: ParseMode.Html, cancellationToken: ct), ct);
            return messageId;
        }
        catch (ApiRequestException ex) when (NotModified(ex)) { return messageId; }
        catch (ApiRequestException ex) when (CannotEdit(ex)) { return null; }
    }
    public async Task<int?> EditPhoto(long chatId, int messageId, ListingPhoto photo, string caption, CancellationToken ct)
    {
        try
        {
            var media = new InputMediaPhoto(InputFile.FromFileId(photo.FileId)) { Caption = caption, ParseMode = ParseMode.Html };
            await Retry(() => client.EditMessageMedia(chatId, messageId, media, cancellationToken: ct), ct);
            return messageId;
        }
        catch (ApiRequestException ex) when (NotModified(ex)) { return messageId; }
        catch (ApiRequestException ex) when (CannotEdit(ex)) { return null; }
    }
    private static bool NotModified(ApiRequestException ex) => ex.ErrorCode == 400 &&
        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
    private static bool CannotEdit(ApiRequestException ex) => ex.ErrorCode == 400 &&
        (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("can't be edited", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("message_id_invalid", StringComparison.OrdinalIgnoreCase));
    public async Task Delete(long chatId, int messageId, CancellationToken ct)
    {
        try { await Retry(async () => { await client.DeleteMessage(chatId, messageId, ct); return true; }, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { logger.LogDebug("Cannot delete message {MessageId} in {ChatId} ({ErrorType})", messageId, chatId, ex.GetType().Name); }
    }
    public async Task Answer(string callbackId, string? text, CancellationToken ct)
    {
        try { await Retry(async () => { await client.AnswerCallbackQuery(callbackId, text, cancellationToken: ct); return true; }, ct); }
        catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
        { logger.LogDebug("Callback answer expired or unavailable: {Code}", ex.ErrorCode); }
    }
    public async Task<bool> Notify(long chatId, string text, ReplyMarkup? keyboard, CancellationToken ct)
    {
        try { await SendText(chatId, text, keyboard, ct); return true; }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        { logger.LogWarning("User/admin {ChatId} blocked the bot or has not started it", chatId); return true; }
    }
    public async Task Configure(CancellationToken ct)
    {
        await Retry(async () =>
        {
            await client.SetMyCommands(
                [new() { Command = "start", Description = T.StartCommand }, new() { Command = "cancel", Description = T.CancelCommand },
                 new() { Command = "help", Description = T.HelpCommand }], cancellationToken: ct);
            return true;
        }, ct);
        await Retry(async () =>
        {
            await client.SetWebhook(options.Value.WebhookUrl, secretToken: options.Value.WebhookSecret,
                allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], maxConnections: 1, cancellationToken: ct);
            return true;
        }, ct);
    }
}
