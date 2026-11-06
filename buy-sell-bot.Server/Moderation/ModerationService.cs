using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using BuySellBot.Bot;
using BuySellBot.Domain;
using BuySellBot.Flow;
using BuySellBot.Infrastructure;
using BuySellBot.Publishing;
using BuySellBot.Storage;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Moderation;

public sealed class ModerationService(
    ITelegramGateway telegram, IListingRepository listings, ISessionRepository sessions,
    Publisher publisher, PostFormatter formatter, Catalog catalog, FlowEngine engine, BuilderUi ui, ListingLocks locks,
    IOptions<BotOptions> options, ILogger<ModerationService> logger)
{
    public bool IsAdmin(long userId) => options.Value.AdminIds.Contains(userId);
    public static string Nonce(Listing listing) => "r" + listing.Revision;
    private static string RenderHash(string text, InlineKeyboardMarkup keyboard) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text + JsonSerializer.Serialize(keyboard))));
    private static InlineKeyboardButton Button(string text, string action, Listing listing, string arg = "") =>
        InlineKeyboardButton.WithCallbackData(text, new CallbackData(action, listing.Id, Nonce(listing), arg, SessionMode.Moderator).ToString());

    public async Task Deliver(Listing listing, CancellationToken ct)
    {
        foreach (var admin in options.Value.AdminIds.Distinct())
        {
            if (listing.ModerationCards.Any(c => c.AdminId == admin)) continue;
            try
            {
                await telegram.SendPhotos(admin, listing.Photos, null, ct);
                var text = CardText(listing, admin);
                var keyboard = CardKeyboard(listing, admin);
                var id = await telegram.SendText(admin, text, keyboard, ct);
                listing.ModerationCards.Add(new(admin, id, RenderHash(text, keyboard)));
                await listings.Save(listing, ct);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 403)
            { logger.LogWarning("Admin {AdminId} must start or unblock the bot to receive moderation cards", admin); }
        }
    }
    private string CardText(Listing listing, long admin)
    {
        var author = $"<a href=\"tg://user?id={listing.AuthorUserId}\">{PostFormatter.Escape(listing.AuthorName)}</a>";
        if (listing.AuthorUsername is { Length: > 0 } username) author += " @" + PostFormatter.Escape(username);
        var category = catalog.Categories.FirstOrDefault(c => c.Id == listing.CategoryId)?.Title ?? listing.CategoryId;
        var status = listing.Status switch
        {
            ListingStatus.Published => T.PublishedStatus,
            ListingStatus.Declined => T.DeclinedStatus + ": " + PostFormatter.Escape(listing.DeclineReason),
            _ => listing.Operation switch
            {
                ModerationOperation.Editing => T.Moderating,
                ModerationOperation.Declining => listing.ModeratorId == admin ? T.ChooseReason : T.Declining,
                ModerationOperation.Publishing => T.Publishing,
                ModerationOperation.Uncertain => T.UncertainStatus, _ => ""
            }
        };
        return $"{T.AuthorField}: {author}\n{T.CategoryLabel}: {PostFormatter.Escape(category)}\n{T.IntentField}: {(listing.Intent switch { Intent.Buy => T.Buy, Intent.Sell => T.Sell, _ => T.CreateListing })}\n\n{formatter.Format(listing)}\n\n{status}";
    }
    private static InlineKeyboardMarkup CardKeyboard(Listing listing, long admin)
    {
        if (listing.Status != ListingStatus.PendingApproval) return new(Array.Empty<InlineKeyboardButton[]>());
        if (listing.Operation == ModerationOperation.Declining && listing.ModeratorId == admin)
            return new(T.DeclineReasons.Select((reason, n) => new[] { Button(reason, "reason", listing, n.ToString()) })
                .Append([Button(T.Back, "release", listing)]));
        if (listing.Operation != ModerationOperation.None) return new(Array.Empty<InlineKeyboardButton[]>());
        return new(new[] { new[] { Button(T.Approve, "approve", listing), Button(T.Decline, "decline", listing) }, new[] { Button(T.Edit, "edit", listing) } });
    }
    public async Task RefreshCards(Listing listing, CancellationToken ct)
    {
        for (var n = 0; n < listing.ModerationCards.Count; n++)
        {
            var card = listing.ModerationCards[n];
            var text = CardText(listing, card.AdminId);
            var keyboard = CardKeyboard(listing, card.AdminId);
            var hash = RenderHash(text, keyboard);
            if (hash == card.RenderHash) continue;
            try
            {
                var id = await telegram.EditText(card.AdminId, card.MessageId, text, keyboard, ct);
                listing.ModerationCards[n] = card with { MessageId = id, RenderHash = hash };
                await listings.Save(listing, ct);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
            { logger.LogWarning("Cannot update moderation card for {AdminId}: {Code}", card.AdminId, ex.ErrorCode); }
        }
    }
    public bool ValidAction(Listing listing, CallbackData data, long admin, int? cardMessageId)
    {
        if (!IsAdmin(admin) || listing.Status != ListingStatus.PendingApproval || data.Nonce != Nonce(listing) ||
            !listing.ModerationCards.Any(c => c.AdminId == admin && c.MessageId == cardMessageId)) return false;
        return data.Action switch
        {
            "reason" or "release" => listing.Operation == ModerationOperation.Declining && listing.ModeratorId == admin,
            "approve" or "decline" or "edit" => listing.Operation == ModerationOperation.None, _ => false
        };
    }
    public async Task Act(Listing listing, CallbackData data, long admin, CancellationToken ct)
    {
        if (data.Action == "release") { await Release(listing, ct); return; }
        if (data.Action == "reason")
        {
            if (!int.TryParse(data.Arg, out var index) || index < 0 || index >= T.DeclineReasons.Length) return;
            await CompleteDecline(listing, T.DeclineReasons[index], ct);
            return;
        }
        if (data.Action == "edit" && await sessions.Get(admin, SessionMode.Moderator, ct) is not null)
        {
            await telegram.Notify(admin, T.ModeratorSessionBusy, null, ct);
            return;
        }
        if (data.Action == "approve" && !PreparePublication(listing, out var warning))
        {
            await telegram.Notify(admin, PostFormatter.Escape(warning), null, ct);
            return;
        }
        listing.ModeratorId = admin; listing.Revision++; listing.UpdatedAt = DateTimeOffset.UtcNow;
        listing.Operation = data.Action == "decline" ? ModerationOperation.Declining : ModerationOperation.Editing;
        await listings.Save(listing, ct);
        await RefreshCards(listing, ct);
        if (data.Action == "decline") return;
        if (data.Action == "approve")
        {
            try
            {
                await publisher.Publish(listing, ct);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
            {
                await Release(listing, ct);
                logger.LogWarning("Telegram rejected publication of {ListingId}: {Details}", listing.Id, ExceptionDiagnostics.Format(ex, options.Value));
                await telegram.Notify(admin, T.PublicationTelegramRejected(ex.ErrorCode), null, ct);
                return;
            }
            catch
            {
                if (listing.Operation != ModerationOperation.Uncertain && listing.Status == ListingStatus.PendingApproval) await Release(listing, CancellationToken.None);
                else await RefreshCards(listing, CancellationToken.None);
                throw;
            }
            await RefreshCards(listing, ct);
            await NotifyAuthor(listing, ct);
            return;
        }
        var copy = JsonSerializer.Deserialize<Listing>(JsonSerializer.Serialize(listing, JsonFileStore<Listing>.Json), JsonFileStore<Listing>.Json)!;
        var session = new BuilderSession { UserId = admin, ListingId = listing.Id, Mode = SessionMode.Moderator, WorkingCopy = copy, Currency = listing.Price?.Currency ?? Currency.UAH };
        engine.CompleteAll(session);
        session.CurrentStepId = FlowEngine.Changes;
        session.ChangeMode = true;
        await sessions.Save(session, ct);
        await sessions.Activate(admin, SessionMode.Moderator, ct);
        await ui.Render(session, copy, ct);
    }
    public async Task<bool> TryHandleDeclineReason(Update update, CancellationToken ct)
    {
        if (update.Message is not { From: { } admin } message || !IsAdmin(admin.Id) ||
            message.Text?.TrimStart().StartsWith('/') == true) return false;

        var all = await listings.All(ct);
        // Resume a failed delivery against the original listing if Telegram retries this input.
        var target = all.FirstOrDefault(listing => listing.DeclineInputUpdateId == update.Id);
        if (target is null)
        {
            var pending = all.Where(listing => listing.Status == ListingStatus.PendingApproval &&
                listing.Operation == ModerationOperation.Declining && listing.ModeratorId == admin.Id).ToArray();
            if (message.ReplyToMessage is { } reply)
                target = pending.FirstOrDefault(listing => listing.ModerationCards.Any(card => card.AdminId == admin.Id && card.MessageId == reply.Id));
            else if (pending.Length == 1) target = pending[0];
            else if (pending.Length > 1)
            {
                await telegram.SendText(admin.Id, T.DeclineReplyRequired, null, ct);
                return true;
            }
        }
        if (target is null) return false;

        using var lease = await locks.Acquire(target.Id, ct);
        var current = await listings.Get(target.Id, ct);
        if (current is null) return true;
        if (current.DeclineInputUpdateId == update.Id)
        {
            if (current.Status == ListingStatus.Declined && current.ModeratorId == admin.Id)
            {
                await RefreshCards(current, ct);
                await NotifyAuthor(current, ct);
            }
            return true;
        }
        if (current.Status != ListingStatus.PendingApproval || current.Operation != ModerationOperation.Declining || current.ModeratorId != admin.Id)
        {
            await telegram.SendText(admin.Id, T.Stale, null, ct);
            return true;
        }
        var reason = message.Text?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500 || reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            await telegram.SendText(admin.Id, T.DeclineReasonError, null, ct);
            return true;
        }
        current.DeclineInputUpdateId = update.Id;
        await CompleteDecline(current, reason, ct);
        return true;
    }
    private async Task CompleteDecline(Listing listing, string reason, CancellationToken ct)
    {
        listing.Status = ListingStatus.Declined;
        listing.DeclineReason = reason;
        listing.Operation = ModerationOperation.None;
        listing.AuthorNotified = false;
        listing.Revision++; listing.UpdatedAt = DateTimeOffset.UtcNow;
        await listings.Save(listing, ct);
        await RefreshCards(listing, ct);
        await NotifyAuthor(listing, ct);
    }
    public async Task Release(Listing listing, CancellationToken ct)
    {
        listing.Operation = ModerationOperation.None; listing.ModeratorId = null; listing.Revision++;
        listing.UpdatedAt = DateTimeOffset.UtcNow;
        await listings.Save(listing, ct);
        await RefreshCards(listing, ct);
    }
    public async Task PublishEdit(Listing listing, BuilderSession session, CancellationToken ct)
    {
        var copy = session.WorkingCopy ?? throw new InvalidOperationException("Missing working copy.");
        if (!PreparePublication(copy, out var warning))
        {
            session.Warning = warning;
            await ui.Render(session, copy, ct);
            return;
        }
        copy.EditedByModerator = true;
        copy.Operation = ModerationOperation.Editing;
        // Preserve current delivery metadata, not a stale snapshot of other admin cards.
        copy.ModerationCards = listing.ModerationCards;
        copy.Revision = listing.Revision;
        try { await publisher.Publish(copy, ct); }
        catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
        {
            await listings.Save(listing, ct);
            logger.LogWarning("Telegram rejected edited publication of {ListingId}: {Details}", listing.Id, ExceptionDiagnostics.Format(ex, options.Value));
            session.Warning = T.PublicationTelegramRejected(ex.ErrorCode);
            await ui.Render(session, copy, ct);
            return;
        }
        catch
        {
            // A rejected API call must not commit the moderator's working copy.
            if (copy.Operation != ModerationOperation.Uncertain)
                await listings.Save(listing, CancellationToken.None);
            else await RefreshCards(copy, CancellationToken.None);
            throw;
        }
        await ui.Cleanup(session, ct, keepPost: true);
        await sessions.Delete(session.UserId, SessionMode.Moderator, ct);
        await telegram.Notify(session.UserId, T.PublishedStatus, BuilderRenderer.Menu, ct);
        await RefreshCards(copy, ct);
        await NotifyAuthor(copy, ct);
    }
    private bool PreparePublication(Listing listing, out string? warning)
    {
        try
        {
            publisher.EnsureReady(listing);
            warning = null;
            return true;
        }
        catch (PublicationBlockedException ex)
        {
            logger.LogWarning("Cannot publish listing {ListingId}: {Reason}", listing.Id, ex.Message);
            warning = ex.UserMessage;
            return false;
        }
    }
    public async Task NotifyAuthor(Listing listing, CancellationToken ct)
    {
        if (listing.AuthorNotified) return;
        if (listing.Status == ListingStatus.Published)
        {
            await telegram.Notify(listing.AuthorUserId, T.AuthorPublished(listing.EditedByModerator),
                new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl(T.OpenPost, listing.PostLink!)), ct);
        }
        else if (listing.Status == ListingStatus.Declined)
        {
            await telegram.Notify(listing.AuthorUserId, T.AuthorDeclined(PostFormatter.Escape(listing.DeclineReason ?? T.DeclineReasons[3])),
                new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData(T.Resubmit,
                    new CallbackData("resubmit", listing.Id, Nonce(listing)).ToString())), ct);
        }
        else return;
        listing.AuthorNotified = true;
        await listings.Save(listing, ct);
    }
}
