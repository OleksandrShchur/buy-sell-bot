using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BuySellBot.Storage;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace BuySellBot.Infrastructure;

public sealed class InboxEntry
{
    public int Id { get; set; }
    public string Payload { get; set; } = "";
    public bool Complete { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RetryAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class UpdateInbox
{
    public static readonly JsonSerializerOptions TelegramJson = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var json = new JsonSerializerOptions();
        JsonBotAPI.Configure(json);
        return json;
    }
    private readonly JsonFileStore<InboxEntry> store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<int, byte> scheduled = new();
    private int incompleteCount;
    public Channel<Update> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<Update>(
        new() { SingleReader = true, AllowSynchronousContinuations = false });
    public UpdateInbox(IOptions<StorageOptions> options)
    {
        store = new(Path.Combine(options.Value.Path, "inbox"));
        incompleteCount = store.All(CancellationToken.None).GetAwaiter().GetResult().Count(e => !e.Complete);
    }

    public async Task<bool> Accept(Update update, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var key = update.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var entry = await store.Get(key, ct);
            if (entry is not null)
            {
                if (!entry.Complete) Schedule(entry);
                return true;
            }
            if (incompleteCount >= 4096) return false;
            entry = new() { Id = update.Id, Payload = JsonSerializer.Serialize(update, TelegramJson) };
            await store.Save(key, entry, ct);
            incompleteCount++;
            Schedule(entry);
            return true;
        }
        finally { gate.Release(); }
    }
    private void Schedule(InboxEntry entry)
    {
        if (entry.RetryAt > DateTimeOffset.UtcNow || !scheduled.TryAdd(entry.Id, 0)) return;
        var update = JsonSerializer.Deserialize<Update>(entry.Payload, TelegramJson)!;
        if (!Channel.Writer.TryWrite(update)) scheduled.TryRemove(entry.Id, out _);
    }
    public async Task Finish(int updateId, bool success, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var key = updateId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var entry = await store.Get(key, ct);
            if (entry is null) return;
            var wasComplete = entry.Complete;
            entry.Complete = success; entry.Attempts++;
            entry.RetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(entry.Attempts, 6))));
            await store.Save(key, entry, ct);
            if (success && !wasComplete) incompleteCount--;
        }
        finally { scheduled.TryRemove(updateId, out _); gate.Release(); }
    }
    public async Task Recover(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var entry in (await store.All(ct)).OrderBy(e => e.Id))
            {
                if (!entry.Complete) Schedule(entry);
                else if (entry.ReceivedAt < DateTimeOffset.UtcNow.AddDays(-7))
                    await store.Delete(entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            }
        }
        finally { gate.Release(); }
    }
}
