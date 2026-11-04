using System.Threading.Channels;
using BuySellBot.Bot;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace BuySellBot.Infrastructure;

public sealed class UpdateProcessor(UpdateInbox inbox, UpdateRouter router, UserLocks users, IOptions<BotOptions> options, ILogger<UpdateProcessor> logger) : BackgroundService
{
    public static long Actor(Update update) => update.CallbackQuery?.From.Id ?? update.Message?.From?.Id ?? update.Message?.Chat.Id ?? 0;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = shutdown.Token;
        var partitions = Enumerable.Range(0, 16).Select(_ => Channel.CreateBounded<Update>(
            new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait })).ToArray();
        async Task Dispatch()
        {
            await foreach (var update in inbox.Channel.Reader.ReadAllAsync(ct))
                await partitions[(uint)Actor(update).GetHashCode() % (uint)partitions.Length].Writer.WriteAsync(update, ct);
        }
        var tasks = partitions.Select(channel => Consume(channel.Reader, ct)).Append(Recover(ct)).Append(Dispatch()).ToArray();
        try
        {
            var ended = await Task.WhenAny(tasks);
            await ended;
            if (!stoppingToken.IsCancellationRequested) throw new InvalidOperationException("An update worker stopped unexpectedly.");
        }
        finally
        {
            await shutdown.CancelAsync();
            foreach (var partition in partitions) partition.Writer.TryComplete();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }
    private async Task Recover(CancellationToken ct)
    {
        await inbox.Recover(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct)) await inbox.Recover(ct);
    }
    private async Task Consume(ChannelReader<Update> reader, CancellationToken ct)
    {
        await foreach (var update in reader.ReadAllAsync(ct))
        {
            var success = false;
            try
            {
                using var lease = await users.Acquire(Actor(update), ct);
                await router.Handle(update, ct);
                success = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ApiRequestException ex) when (ex.ErrorCode == 403)
            {
                logger.LogWarning("User {UserId} blocked the bot or never started it", Actor(update));
                success = true;
            }
            catch (Exception ex)
            {
                logger.LogError("Update {UpdateId} for {UserId} failed; durable retry scheduled. {Details}",
                    update.Id, Actor(update), ExceptionDiagnostics.Format(ex, options.Value));
            }
            await inbox.Finish(update.Id, success, ct);
        }
    }
}
