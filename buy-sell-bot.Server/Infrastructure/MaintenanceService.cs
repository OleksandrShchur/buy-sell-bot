using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Moderation;
using BuySellBot.Storage;
using Microsoft.Extensions.Options;

namespace BuySellBot.Infrastructure;

public sealed class MaintenanceService(
    IListingRepository listings, ISessionRepository sessions, ListingLocks locks, UserLocks users,
    ModerationService moderation, BuilderUi ui, IOptions<BotOptions> options, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var snapshot in await listings.All(stoppingToken))
        {
            using var lease = await locks.Acquire(snapshot.Id, stoppingToken);
            var listing = await listings.Get(snapshot.Id, stoppingToken);
            if (listing?.Operation == ModerationOperation.Publishing)
            {
                listing.Operation = ModerationOperation.Uncertain;
                await listings.Save(listing, stoppingToken);
                logger.LogError("Interrupted publication {ListingId} needs manual reconciliation", listing.Id);
            }
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            foreach (var snapshot in await listings.All(stoppingToken))
            {
                try
                {
                    if (snapshot.Status == ListingStatus.Draft && snapshot.UpdatedAt < DateTimeOffset.UtcNow.AddDays(-options.Value.DraftTtlDays))
                    {
                        using var userLease = await users.Acquire(snapshot.AuthorUserId, stoppingToken);
                        using var listingLease = await locks.Acquire(snapshot.Id, stoppingToken);
                        var draft = await listings.Get(snapshot.Id, stoppingToken);
                        if (draft?.Status != ListingStatus.Draft || draft.UpdatedAt >= DateTimeOffset.UtcNow.AddDays(-options.Value.DraftTtlDays)) continue;
                        var session = await sessions.Get(draft.AuthorUserId, SessionMode.Author, stoppingToken);
                        if (session?.ListingId == draft.Id)
                        {
                            await ui.Cleanup(session, stoppingToken);
                            await sessions.Delete(session.UserId, session.Mode, stoppingToken);
                        }
                        await listings.Delete(draft.Id, stoppingToken);
                        continue;
                    }
                    using var lease = await locks.Acquire(snapshot.Id, stoppingToken);
                    var listing = await listings.Get(snapshot.Id, stoppingToken);
                    if (listing is null) continue;
                    if (listing.Status == ListingStatus.PendingApproval)
                    {
                        if (listing.Operation == ModerationOperation.Editing && listing.ModeratorId is { } moderator)
                        {
                            var edit = await sessions.Get(moderator, SessionMode.Moderator, stoppingToken);
                            if (edit is null || edit.ListingId != listing.Id || edit.WorkingCopy is null)
                                await moderation.Release(listing, stoppingToken);
                        }
                        await moderation.Deliver(listing, stoppingToken);
                        await moderation.RefreshCards(listing, stoppingToken);
                    }
                    else if (listing.Status is ListingStatus.Published or ListingStatus.Declined)
                    {
                        await moderation.RefreshCards(listing, stoppingToken);
                        await moderation.NotifyAuthor(listing, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError("Maintenance failed for {ListingId} ({ErrorType})", snapshot.Id, ex.GetType().Name); }
            }
            await CleanupSessions(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    private async Task CleanupSessions(CancellationToken ct)
    {
        foreach (var document in await sessions.All(ct))
        foreach (var snapshot in new[] { document.Author, document.Moderator })
        {
            if (snapshot is null) continue;
            try
            {
                using var userLease = await users.Acquire(snapshot.UserId, ct);
                using var listingLease = await locks.Acquire(snapshot.ListingId, ct);
                var session = await sessions.Get(snapshot.UserId, snapshot.Mode, ct);
                if (session is null || session.ListingId != snapshot.ListingId) continue;
                var listing = await listings.Get(session.ListingId, ct);
                var valid = session.Mode == SessionMode.Author
                    ? listing?.Status == ListingStatus.Draft && listing.AuthorUserId == session.UserId
                    : listing?.Status == ListingStatus.PendingApproval && listing.Operation == ModerationOperation.Editing &&
                      listing.ModeratorId == session.UserId && session.WorkingCopy is not null;
                if (valid) continue;
                await ui.Cleanup(session, ct);
                await sessions.Delete(session.UserId, session.Mode, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("Session recovery failed for {UserId} ({ErrorType})", snapshot.UserId, ex.GetType().Name); }
        }
    }
}

public sealed class WebhookRegistrationService(ITelegramGateway telegram) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => telegram.Configure(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
