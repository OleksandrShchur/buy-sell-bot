using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Flow;
using BuySellBot.Infrastructure;
using BuySellBot.Moderation;
using BuySellBot.Publishing;
using BuySellBot.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace BuySellBot.Tests;

public sealed class FakeTelegram : ITelegramGateway
{
    public sealed record Sent(long Chat, int Id, string? Text, ReplyMarkup? Keyboard, int? Thread, int PhotoCount);
    public List<Sent> SentMessages { get; } = [];
    public List<(long Chat, int Id)> Deleted { get; } = [];
    public List<(string Id, string? Text)> Answers { get; } = [];
    public List<(long Chat, int Id, string Caption)> CaptionEdits { get; } = [];
    public List<(long Chat, int Id, string FileId)> PhotoEdits { get; } = [];
    public List<(long Chat, int Count, string? Caption)> PhotoSends { get; } = [];
    public ReplyMarkup? LastReplyKeyboard { get; private set; }
    public HashSet<int> MissingMessageIds { get; } = [];
    public HashSet<int> HiddenMessageIds { get; } = [];
    public Exception? SendFailure { get; set; }
    public List<string> Operations { get; } = [];
    private readonly HashSet<int> nonEditableMessages = [];
    public Exception? PublishFailure { get; set; }
    private int nextId;
    public int EditCount { get; private set; }
    public Task<int> SendText(long chatId, string text, ReplyMarkup? keyboard, CancellationToken ct, int? threadId = null)
    {
        if (SendFailure is not null) throw SendFailure;
        if (threadId is not null && PublishFailure is not null) throw PublishFailure;
        var id = Interlocked.Increment(ref nextId);
        if (keyboard is ReplyKeyboardMarkup or ReplyKeyboardRemove) LastReplyKeyboard = keyboard;
        if (keyboard is ReplyKeyboardMarkup or ReplyKeyboardRemove) nonEditableMessages.Add(id);
        SentMessages.Add(new(chatId, id, text, keyboard, threadId, 0));
        Operations.Add("send:" + id);
        return Task.FromResult(id);
    }
    public Task<int> EditText(long chatId, int? messageId, string text, InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        EditCount++;
        if (messageId is null) return SendText(chatId, text, keyboard, ct);
        if (nonEditableMessages.Contains(messageId.Value) || MissingMessageIds.Contains(messageId.Value)) return SendText(chatId, text, keyboard, ct);
        SentMessages.RemoveAll(m => m.Chat == chatId && m.Id == messageId);
        SentMessages.Add(new(chatId, messageId.Value, text, keyboard, null, 0));
        return Task.FromResult(messageId.Value);
    }
    public Task<IReadOnlyList<int>> SendPhotos(long chatId, IReadOnlyList<ListingPhoto> photos, string? caption, CancellationToken ct, int? threadId = null)
    {
        if (threadId is not null && PublishFailure is not null) throw PublishFailure;
        if (SendFailure is not null) throw SendFailure;
        PhotoSends.Add((chatId, photos.Count, caption));
        var ids = photos.Select(_ => Interlocked.Increment(ref nextId)).ToArray();
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            SentMessages.Add(new(chatId, id, index == 0 ? caption : null, null, threadId, photos.Count));
            Operations.Add("send:" + id);
        }
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }
    public Task<int?> EditCaption(long chatId, int messageId, string caption, CancellationToken ct)
    {
        if (MissingMessageIds.Contains(messageId) || nonEditableMessages.Contains(messageId)) return Task.FromResult<int?>(null);
        CaptionEdits.Add((chatId, messageId, caption));
        var message = SentMessages.Single(m => m.Chat == chatId && m.Id == messageId);
        SentMessages.Remove(message);
        SentMessages.Add(message with { Text = caption });
        return Task.FromResult<int?>(messageId);
    }
    public Task<int?> EditPhoto(long chatId, int messageId, ListingPhoto photo, string caption, CancellationToken ct)
    {
        if (MissingMessageIds.Contains(messageId) || nonEditableMessages.Contains(messageId)) return Task.FromResult<int?>(null);
        PhotoEdits.Add((chatId, messageId, photo.FileId));
        var message = SentMessages.Single(m => m.Chat == chatId && m.Id == messageId);
        SentMessages.Remove(message);
        SentMessages.Add(message with { Text = caption, PhotoCount = Math.Max(1, message.PhotoCount), Keyboard = null });
        return Task.FromResult<int?>(messageId);
    }
    public Task Delete(long chatId, int messageId, CancellationToken ct)
    {
        Deleted.Add((chatId, messageId));
        Operations.Add("delete:" + messageId);
        return Task.CompletedTask;
    }
    public Task Answer(string callbackId, string? text, CancellationToken ct) { Answers.Add((callbackId, text)); return Task.CompletedTask; }
    public async Task<bool> Notify(long chatId, string text, ReplyMarkup? keyboard, CancellationToken ct) { await SendText(chatId, text, keyboard, ct); return true; }
    public Task Configure(CancellationToken ct) => Task.CompletedTask;
}

public sealed class BotFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "buy-sell-tests-" + Guid.NewGuid().ToString("N"));
    public Catalog Catalog { get; } = new()
    {
        Categories = [new() { Id = "other", Title = "📦 Інше", TopicId = 42 }],
        Locations = ["Київ"]
    };
    public IOptions<BotOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new BotOptions
    {
        ChatId = -1001234567890, AdminIds = [100, 200], MaxPendingPerUser = 1
    });
    public FakeTelegram Telegram { get; } = new();
    public IListingRepository Listings { get; }
    public ISessionRepository Sessions { get; }
    public FlowEngine Engine { get; }
    public PostFormatter Formatter { get; }
    public BuilderUi Ui { get; }
    public ModerationService Moderation { get; }
    public Publisher Publisher { get; }
    public UpdateRouter Router { get; }
    public PhotoDebouncer Photos { get; }
    public ListingLocks Locks { get; } = new();
    public BotFixture()
    {
        var storage = Microsoft.Extensions.Options.Options.Create(new StorageOptions { Path = DirectoryPath });
        Listings = new JsonListingRepository(storage); Sessions = new JsonSessionRepository(storage);
        Engine = new(new StepCatalog(Catalog)); Formatter = new(Catalog);
        Ui = new(Telegram, new(Engine, Formatter), Sessions, NullLogger<BuilderUi>.Instance);
        Publisher = new(Telegram, Listings, Formatter, Catalog, Options, NullLogger<Publisher>.Instance);
        Moderation = new(Telegram, Listings, Sessions, Publisher, Formatter, Catalog, Engine, Ui, Locks, Options, NullLogger<ModerationService>.Instance);
        Photos = new PhotoDebouncer(new(), Locks, Sessions, Listings, Ui, NullLogger<PhotoDebouncer>.Instance);
        Router = new(Telegram, Listings, Sessions, Locks, Engine, Ui, Formatter, Moderation, Photos, Options);
    }
    public Listing Listing() => new()
    {
        AuthorUserId = 10, AuthorUsername = "author", AuthorName = "Автор", Intent = Intent.Sell,
        CategoryId = "other", Title = "Велосипед", Price = new(PriceKind.Fixed, 1500), Location = "Київ", ContactKind = ContactKind.Telegram
    };
    public static Update Message(long userId, string text, string? username = "author") => new()
    {
        Id = Random.Shared.Next(1, int.MaxValue), Message = new()
        {
            Id = Random.Shared.Next(1, int.MaxValue), From = new() { Id = userId, FirstName = "Автор", Username = username },
            Chat = new() { Id = userId, Type = ChatType.Private }, Text = text
        }
    };
    public static Update Callback(long userId, int messageId, CallbackData data, string? username = "author") => new()
    {
        Id = Random.Shared.Next(1, int.MaxValue), CallbackQuery = new()
        {
            Id = Guid.NewGuid().ToString("N"), From = new() { Id = userId, FirstName = "Автор", Username = username }, Data = data.ToString(),
            ChatInstance = "test", Message = new() { Id = messageId, Chat = new() { Id = userId, Type = ChatType.Private } }
        }
    };
    public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
}
