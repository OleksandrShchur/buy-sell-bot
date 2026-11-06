using System.Collections.Concurrent;
using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using BuySellBot.Storage;

namespace BuySellBot.Bot;

public sealed class PhotoDebouncer(
    UserLocks users, ListingLocks listingsLock, ISessionRepository sessions, IListingRepository listings,
    BuilderUi ui, ILogger<PhotoDebouncer> logger) : BackgroundService
{
    private sealed record Pending(long UserId, SessionMode Mode, string ListingId, long Version, DateTimeOffset Due);
    private readonly ConcurrentDictionary<(long, SessionMode), Pending> pending = new();
    public void Schedule(BuilderSession session) =>
        pending[(session.UserId, session.Mode)] = new(session.UserId, session.Mode, session.ListingId, session.PhotoBatchVersion, DateTimeOffset.UtcNow.AddMilliseconds(600));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var pair in pending.ToArray())
            {
                if (pair.Value.Due > DateTimeOffset.UtcNow) continue;
                // Conditional removal cannot erase a newer batch.
                if (!pending.TryRemove(pair)) continue;
                try
                {
                    var work = pair.Value;
                    using var userLease = await users.Acquire(work.UserId, stoppingToken);
                    using var listingLease = await listingsLock.Acquire(work.ListingId, stoppingToken);
                    var session = await sessions.Get(work.UserId, work.Mode, stoppingToken);
                    if (session is null || session.ListingId != work.ListingId || session.PhotoBatchVersion != work.Version || session.CurrentStepId != "photos") continue;
                    var listing = session.Mode == SessionMode.Moderator ? session.WorkingCopy : await listings.Get(work.ListingId, stoppingToken);
                    if (listing is not null) await ui.Render(session, listing, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError("Photo batch render failed for {UserId} ({ErrorType})", pair.Key.Item1, ex.GetType().Name); }
            }
        }
    }
}
