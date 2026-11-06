using BuySellBot.Domain;
using BuySellBot.Flow;
using BuySellBot.Infrastructure;
using BuySellBot.Moderation;
using BuySellBot.Publishing;
using BuySellBot.Storage;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Bot;

public sealed class UpdateRouter(
    ITelegramGateway telegram, IListingRepository listings, ISessionRepository sessions,
    ListingLocks locks, FlowEngine engine, BuilderUi ui, PostFormatter formatter,
    ModerationService moderation, PhotoDebouncer photos, IOptions<BotOptions> options)
{
    public async Task Handle(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback) { await HandleCallback(callback, ct); return; }
        if (update.Message is not { From: { } user } message) return;
        var command = message.Text?.Split(' ', 2)[0].Split('@', 2)[0];
        if (message.Chat.Type != ChatType.Private)
        {
            if (command == "/topicid" && moderation.IsAdmin(user.Id) && message.Chat.Type == ChatType.Supergroup && message.MessageThreadId is not null)
                await telegram.SendText(message.Chat.Id, T.Topic(message.Chat.Id, message.MessageThreadId), null, ct, message.MessageThreadId);
            return;
        }
        var processed = true;
        try
        {
            if (command == "/help") { await telegram.SendText(user.Id, T.Help, null, ct); return; }
            if (command == "/topicid")
            {
                if (moderation.IsAdmin(user.Id)) await telegram.SendText(user.Id, T.Topic(message.Chat.Id, null), null, ct);
                return;
            }
            if (command == "/start")
            {
                await sessions.Activate(user.Id, SessionMode.Author, ct);
                var author = await sessions.Get(user.Id, SessionMode.Author, ct);
                if (author is not null && await Resume(author, user, ct)) return;
                if (await sessions.Get(user.Id, SessionMode.Moderator, ct) is { } moderatorSession)
                {
                    await sessions.Activate(user.Id, SessionMode.Moderator, ct);
                    if (await Resume(moderatorSession, user, ct)) return;
                }
                await sessions.Activate(user.Id, SessionMode.Author, ct);
                await telegram.SendText(user.Id, T.Welcome(options.Value.ChatUrl), BuilderRenderer.Menu, ct);
                return;
            }
            if (message.Text == T.Buy || message.Text == "🛒 Куплю")
            {
                await Start(user, Intent.Buy, ct); return;
            }
            if (message.Text == T.Sell || message.Text == "💰 Продам")
            {
                await Start(user, Intent.Sell, ct); return;
            }
            if (message.Text == T.CreateListing)
            {
                await telegram.SendText(user.Id, T.ChooseIntent, BuilderRenderer.Menu, ct); return;
            }
            if (await moderation.TryHandleDeclineReason(update, ct)) return;
            var document = await sessions.GetDocument(user.Id, ct);
            var session = document.ActiveMode == SessionMode.Author ? document.Author : document.Moderator;
            session ??= document.Author ?? document.Moderator;
            if (session is null) { await telegram.SendText(user.Id, T.NoSession, BuilderRenderer.Menu, ct); return; }
            using var lease = await locks.Acquire(session.ListingId, ct);
            var listing = await listings.Get(session.ListingId, ct);
            if (!Usable(session, listing))
            {
                await Drop(session, ct);
                await telegram.SendText(user.Id, T.NoSession, BuilderRenderer.Menu, ct);
                return;
            }
            var working = session.Mode == SessionMode.Moderator ? session.WorkingCopy! : listing!;
            await SyncAuthorContact(session, working, user, ct);
            if (session.AppliedInputIds.Contains(update.Id))
            {
                await ui.Render(session, working, ct); return;
            }
            if (command == "/cancel")
            {
                session.Invalidate(); session.Confirmation = "cancel";
                await ui.Refresh(session, working, ct); return;
            }
            if (session.Confirmation is not null || session.CurrentStepId is FlowEngine.Review or FlowEngine.Changes)
            { session.Warning = T.InvalidInput; await ui.Render(session, working, ct); return; }
            var photo = message.Photo?.LastOrDefault();
            var isPhotoStep = session.CurrentStepId == "photos";
            var isContactStep = session.CurrentStepId == "contact";
            var oldWarning = session.Warning;
            // Telegram delivers each album photo as a separate update. A new upload
            // replaces the selection; only items from the same album are accumulated.
            if (photo is not null && isPhotoStep &&
                (message.MediaGroupId is null || session.MediaGroupId != message.MediaGroupId))
            {
                working.Photos.Clear();
                oldWarning = null;
            }
            engine.Apply(session, working, new(
                Text: message.Contact?.PhoneNumber ?? message.Text,
                Photo: photo is null ? null : new(photo.FileId, photo.FileUniqueId),
                ContactOwner: message.Contact is null ? null : message.Contact.UserId ?? -1));
            working.UpdatedAt = DateTimeOffset.UtcNow;
            if (session.Mode == SessionMode.Author) await listings.Save(working, ct);
            if (isContactStep && session.Warning is null && working.ContactPhone is { } phone &&
                working.ContactKind is ContactKind.Phone or ContactKind.TelegramAndPhone)
                await sessions.SaveContactPhone(session.UserId, phone, ct);
            session.AppliedInputIds.Add(update.Id);
            if (session.AppliedInputIds.Count > 128) session.AppliedInputIds.RemoveAt(0);
            await sessions.Save(session, ct);
            if (photo is not null && isPhotoStep)
            {
                if (session.MediaGroupId == message.MediaGroupId && oldWarning == T.PhotoLimit) session.Warning = oldWarning;
                session.MediaGroupId = message.MediaGroupId;
                session.PhotoBatchVersion++;
                await sessions.Save(session, ct);
                if (session.CurrentStepId == "photos") photos.Schedule(session);
                else await ui.Render(session, working, ct);
            }
            else await ui.Render(session, working, ct);
        }
        catch
        {
            processed = false;
            throw;
        }
        finally
        {
            if (processed && options.Value.DeleteUserMessages) await telegram.Delete(message.Chat.Id, message.Id, ct);
        }
    }
    private static bool Usable(BuilderSession session, Listing? listing) =>
        listing is not null && (session.Mode == SessionMode.Author
            ? listing.Status == ListingStatus.Draft && listing.AuthorUserId == session.UserId
            : listing.Status == ListingStatus.PendingApproval && listing.Operation == ModerationOperation.Editing &&
              listing.ModeratorId == session.UserId && session.WorkingCopy is not null);

    private async Task Drop(BuilderSession session, CancellationToken ct)
    {
        await ui.Cleanup(session, ct);
        await sessions.Delete(session.UserId, session.Mode, ct);
    }
    private async Task SyncAuthorContact(BuilderSession session, Listing listing, User user, CancellationToken ct)
    {
        if (session.Mode != SessionMode.Author || listing.AuthorUserId != user.Id) return;
        listing.AuthorUsername = user.Username;
        listing.ContactPhone ??= (await sessions.GetDocument(user.Id, ct)).ContactPhone;
    }
    private async Task<bool> Resume(BuilderSession session, User user, CancellationToken ct)
    {
        using var lease = await locks.Acquire(session.ListingId, ct);
        var listing = await listings.Get(session.ListingId, ct);
        if (!Usable(session, listing)) { await Drop(session, ct); return false; }
        var working = session.Mode == SessionMode.Moderator ? session.WorkingCopy! : listing!;
        await SyncAuthorContact(session, working, user, ct);
        if (session.Mode == SessionMode.Author) await listings.Save(working, ct);
        if (session.Mode == SessionMode.Author && (working.ContactKind is null || formatter.Validate(working) == T.ContactError) &&
            session.CurrentStepId is FlowEngine.Review or FlowEngine.Changes)
            engine.Change(session, working, "contact");
        session.Invalidate();
        session.RestartIntent = null;
        await ui.Refresh(session, working, ct, recreateProduct: true);
        return true;
    }
    private async Task Start(User user, Intent intent, CancellationToken ct)
    {
        var current = await sessions.Get(user.Id, SessionMode.Author, ct);
        if (current is not null)
        {
            using var lease = await locks.Acquire(current.ListingId, ct);
            var listing = await listings.Get(current.ListingId, ct);
            if (Usable(current, listing))
            {
                current.Invalidate(); current.Confirmation = "restart"; current.RestartIntent = intent;
                await sessions.Activate(user.Id, SessionMode.Author, ct);
                await ui.Render(current, listing!, ct); return;
            }
            await Drop(current, ct);
        }
        await Create(user, intent, ct);
    }
    private async Task Create(User user, Intent intent, CancellationToken ct)
    {
        var listing = new Listing { AuthorUserId = user.Id, AuthorUsername = user.Username,
            AuthorName = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Intent = intent };
        listing.ContactPhone = (await sessions.GetDocument(user.Id, ct)).ContactPhone;
        if (listing.AuthorName.Length > 80) listing.AuthorName = listing.AuthorName[..80];
        // Collision check before creation; actors have serialized execution but ids are random.
        while (await listings.Get(listing.Id, ct) is not null) listing.Id = Guid.NewGuid().ToString("N")[..8];
        var session = new BuilderSession { UserId = user.Id, ListingId = listing.Id };
        engine.Start(session, listing);
        await listings.Save(listing, ct);
        await sessions.Save(session, ct);
        await sessions.Activate(user.Id, SessionMode.Author, ct);
        await ui.Render(session, listing, ct);
    }
    private async Task HandleCallback(CallbackQuery callback, CancellationToken ct)
    {
        var answered = false;
        async Task Answer(string? message = null)
        { answered = true; await telegram.Answer(callback.Id, message, ct); }
        try
        {
            if (callback.Message is not { Chat.Type: ChatType.Private } message ||
                message.Chat.Id != callback.From.Id || !CallbackData.TryParse(callback.Data, out var parsed))
            { await Answer(T.Stale); return; }
            var data = parsed!;
            var adminAction = data.Action is "approve" or "decline" or "edit" or "reason" or "release";
            if ((adminAction || data.Mode == SessionMode.Moderator) && !moderation.IsAdmin(callback.From.Id))
            { await Answer(T.NotAdmin); return; }
            using var lease = await locks.Acquire(data.ListingId, ct);
            var listing = await listings.Get(data.ListingId, ct);
            if (listing is null) { await Answer(T.Stale); return; }
            if (adminAction)
            {
                if (!moderation.ValidAction(listing, data, callback.From.Id, message.Id)) { await Answer(T.Stale); return; }
                await Answer();
                await moderation.Act(listing, data, callback.From.Id, ct);
                return;
            }
            if (data.Action == "resubmit")
            {
                if (listing.AuthorUserId != callback.From.Id || listing.Status != ListingStatus.Declined ||
                    data.Nonce != ModerationService.Nonce(listing)) { await Answer(T.Stale); return; }
                var active = await sessions.Get(callback.From.Id, SessionMode.Author, ct);
                if (active is not null) { await Answer(T.Restart); return; }
                await Answer();
                listing.Status = ListingStatus.Draft; listing.Revision++; listing.Operation = ModerationOperation.None;
                listing.ModeratorId = null; listing.DeclineReason = null; listing.AuthorNotified = false;
                listing.ModerationCards.Clear(); listing.UpdatedAt = DateTimeOffset.UtcNow;
                await listings.Save(listing, ct);
                var reopened = new BuilderSession { UserId = callback.From.Id, ListingId = listing.Id, Currency = listing.Price?.Currency ?? Currency.UAH };
                engine.CompleteAll(reopened);
                if (listing.ContactKind is null) engine.Change(reopened, listing, "contact");
                await sessions.Save(reopened, ct);
                await sessions.Activate(callback.From.Id, SessionMode.Author, ct);
                await ui.Render(reopened, listing, ct); return;
            }
            var session = await sessions.Get(callback.From.Id, data.Mode, ct);
            if (session is null || session.ListingId != listing.Id || data.Nonce != session.Nonce ||
                session.BuilderMessageId != message.Id || !Usable(session, listing))
            { await Answer(T.Stale); return; }
            await Answer();
            await sessions.Activate(session.UserId, session.Mode, ct);
            var working = session.Mode == SessionMode.Moderator ? session.WorkingCopy! : listing;
            await SyncAuthorContact(session, working, callback.From, ct);
            switch (data.Action)
            {
                case "switch":
                    var otherMode = session.Mode == SessionMode.Author ? SessionMode.Moderator : SessionMode.Author;
                    var other = await sessions.Get(session.UserId, otherMode, ct);
                    if (other is null) { session.Warning = T.NoSession; break; }
                    // Do not acquire a second listing lock while holding this one.
                    await sessions.Activate(session.UserId, otherMode, ct);
                    other.LastRenderHash = null;
                    var otherListing = other.Mode == SessionMode.Moderator ? other.WorkingCopy : await listings.Get(other.ListingId, ct);
                    if (otherListing is not null) await ui.Render(other, otherListing, ct);
                    return;
                case "cancel": session.Invalidate(); session.Confirmation = "cancel"; break;
                case "no": session.Invalidate(); session.RestartIntent = null; break;
                case "confirm":
                    if (session.Confirmation is null) return;
                    if (session.Mode == SessionMode.Moderator) { await StopEdit(listing, session, ct); return; }
                    var restart = session.Confirmation == "restart" ? session.RestartIntent : null;
                    listing.Status = ListingStatus.Cancelled; listing.Revision++; listing.UpdatedAt = DateTimeOffset.UtcNow;
                    await listings.Save(listing, ct); await Drop(session, ct);
                    if (restart is { } intent) await Create(callback.From, intent, ct);
                    else await telegram.SendText(session.UserId, T.Cancelled, BuilderRenderer.Menu, ct);
                    return;
                case "back": engine.Back(session, working); break;
                case "skip": engine.Skip(session, working); break;
                case "keep": if (session.ChangeMode) engine.Keep(session, working); break;
                case "changes":
                    if (session.CurrentStepId != FlowEngine.Review) return;
                    session.Invalidate(); session.CurrentStepId = FlowEngine.Changes; break;
                case "change":
                    if (session.CurrentStepId != FlowEngine.Changes) return;
                    engine.Change(session, working, data.Arg); break;
                case "choose":
                    if (session.Confirmation is not null) return;
                    engine.Apply(session, working, new(Arg: data.Arg)); break;
                case "submit":
                    if (session.Mode != SessionMode.Author || session.CurrentStepId != FlowEngine.Review) return;
                    await Submit(listing, session, ct); return;
                case "publish":
                    if (session.Mode != SessionMode.Moderator || session.CurrentStepId is not (FlowEngine.Review or FlowEngine.Changes)) return;
                    await moderation.PublishEdit(listing, session, ct); return;
                case "stop":
                    if (session.Mode == SessionMode.Moderator) await StopEdit(listing, session, ct);
                    return;
                default: return;
            }
            working.UpdatedAt = DateTimeOffset.UtcNow;
            if (session.Mode == SessionMode.Author) await listings.Save(working, ct);
            await ui.Render(session, working, ct);
        }
        finally { if (!answered) await telegram.Answer(callback.Id, T.Error, ct); }
    }
    private async Task StopEdit(Listing listing, BuilderSession session, CancellationToken ct)
    {
        await Drop(session, ct);
        await moderation.Release(listing, ct);
        await telegram.SendText(session.UserId, T.EditCancelled, BuilderRenderer.Menu, ct);
    }
    private async Task Submit(Listing listing, BuilderSession session, CancellationToken ct)
    {
        if (listing.ContactKind is null)
        {
            engine.Change(session, listing, "contact");
            await ui.Render(session, listing, ct);
            return;
        }
        var error = formatter.Validate(listing);
        if (error is not null) { session.Warning = error; await ui.Render(session, listing, ct); return; }
        var pending = (await listings.All(ct)).Count(l => l.AuthorUserId == listing.AuthorUserId && l.Status == ListingStatus.PendingApproval);
        if (pending >= options.Value.MaxPendingPerUser)
        { session.Warning = T.PendingLimit; await ui.Render(session, listing, ct); return; }
        listing.Status = ListingStatus.PendingApproval; listing.Revision++; listing.UpdatedAt = DateTimeOffset.UtcNow;
        await listings.Save(listing, ct);
        await ui.Render(session, listing, ct);
        await ui.Cleanup(session, ct, keepPost: true);
        await sessions.Delete(session.UserId, session.Mode, ct);
        await telegram.SendText(listing.AuthorUserId, T.Submitted, BuilderRenderer.Menu, ct);
        await moderation.Deliver(listing, ct);
    }
}
