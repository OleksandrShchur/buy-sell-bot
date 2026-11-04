using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using BuySellBot.Storage;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Bot;

public sealed class BuilderUi(ITelegramGateway telegram, BuilderRenderer renderer, ISessionRepository sessions)
{
    public async Task Cleanup(BuilderSession session, CancellationToken ct, bool keepPost = false)
    {
        if (session.BuilderMessageId is { } id) await telegram.Delete(session.UserId, id, ct);
        foreach (var extra in session.ExtraMessageIds) await telegram.Delete(session.UserId, extra, ct);
        if (!keepPost)
        {
            foreach (var product in session.ProductMessageIds) await telegram.Delete(session.UserId, product, ct);
            session.ProductMessageIds.Clear();
            session.ProductPhotoIds.Clear();
            session.LastProductHash = null;
        }
        session.BuilderMessageId = null;
        session.ExtraMessageIds.Clear();
        session.LastRenderHash = null;
        await sessions.Save(session, ct);
    }

    public async Task Render(BuilderSession session, Listing listing, CancellationToken ct)
    {
        // Upgrade sessions created by the old combined builder without touching their draft data.
        if (session.ProductMessageIds.Count == 0 && session.LastProductHash is null)
        {
            if (session.BuilderMessageId is not null) await ResetActions(session, ct);
            foreach (var extra in session.ExtraMessageIds) await telegram.Delete(session.UserId, extra, ct);
            session.ExtraMessageIds.Clear();
        }
        if (session.ProductMessageIds.Count > 0 && !session.ReplyKeyboardHidden)
            await HideReplyKeyboard(session, ct);

        var other = await sessions.Get(session.UserId, session.Mode == SessionMode.Author ? SessionMode.Moderator : SessionMode.Author, ct);
        var view = renderer.Render(listing, session, other is not null);
        var relocated = await UpdateProduct(session, listing, view.ProductText, ct);
        if (relocated && session.BuilderMessageId is not null) await ResetActions(session, ct);
        // A recreated album must be followed by a fresh actions message, with fresh callbacks.
        view = renderer.Render(listing, session, other is not null);
        var hash = Hash(view.ActionsText + JsonSerializer.Serialize(view.Keyboard));
        if (session.LastRenderHash != hash || session.BuilderMessageId is null)
        {
            session.BuilderMessageId = await telegram.EditText(session.UserId, session.BuilderMessageId, view.ActionsText, view.Keyboard, ct);
            session.LastRenderHash = hash;
        }
        await sessions.Save(session, ct);
    }

    private async Task<bool> UpdateProduct(BuilderSession session, Listing listing, string text, CancellationToken ct)
    {
        var photoIds = listing.Photos.Select(photo => photo.FileId).ToList();
        var hash = Hash(text);
        if (session.ProductMessageIds.Count == 0)
        {
            await SendProduct(session, listing, text, ct);
            session.ProductPhotoIds = photoIds;
            session.LastProductHash = hash;
            await sessions.Save(session, ct);
            return true;
        }

        var photosChanged = !session.ProductPhotoIds.SequenceEqual(photoIds);
        if (!photosChanged && session.LastProductHash == hash) return false;
        var firstId = session.ProductMessageIds[0];
        if (photosChanged && listing.Photos.Count == 1 && session.ProductMessageIds.Count == 1)
        {
            if (await telegram.EditPhoto(session.UserId, firstId, listing.Photos[0], text, ct) is not null)
            {
                session.ProductPhotoIds = photoIds; session.LastProductHash = hash;
                await sessions.Save(session, ct);
                return false;
            }
        }
        else if (!photosChanged)
        {
            if (listing.Photos.Count == 0)
            {
                var id = await telegram.EditText(session.UserId, firstId, text, null, ct);
                session.ProductMessageIds[0] = id; session.LastProductHash = hash;
                await sessions.Save(session, ct);
                if (id != firstId) await telegram.Delete(session.UserId, firstId, ct);
                return id != firstId;
            }
            if (await telegram.EditCaption(session.UserId, firstId, text, ct) is not null)
            {
                session.LastProductHash = hash;
                await sessions.Save(session, ct);
                return false;
            }
        }

        // Telegram cannot append items to an existing album. Keep the old post until its replacement exists.
        var oldIds = session.ProductMessageIds.ToArray();
        await SendProduct(session, listing, text, ct);
        session.ProductPhotoIds = photoIds;
        session.LastProductHash = hash;
        await sessions.Save(session, ct);
        foreach (var oldId in oldIds) await telegram.Delete(session.UserId, oldId, ct);
        return true;
    }

    private async Task SendProduct(BuilderSession session, Listing listing, string text, CancellationToken ct)
    {
        if (listing.Photos.Count == 0)
        {
            session.ProductMessageIds = [await telegram.SendText(session.UserId, text, new ReplyKeyboardRemove(), ct)];
            session.ReplyKeyboardHidden = true;
        }
        else
        {
            if (!session.ReplyKeyboardHidden) await HideReplyKeyboard(session, ct);
            session.ProductMessageIds = (await telegram.SendPhotos(session.UserId, listing.Photos, text, ct)).ToList();
        }
    }

    private async Task HideReplyKeyboard(BuilderSession session, CancellationToken ct)
    {
        var id = await telegram.SendText(session.UserId, T.BuilderStarted, new ReplyKeyboardRemove(), ct);
        await telegram.Delete(session.UserId, id, ct);
        session.ReplyKeyboardHidden = true;
        await sessions.Save(session, ct);
    }

    private async Task ResetActions(BuilderSession session, CancellationToken ct)
    {
        if (session.BuilderMessageId is { } id) await telegram.Delete(session.UserId, id, ct);
        session.BuilderMessageId = null;
        session.LastRenderHash = null;
        session.Nonce = BuilderSession.NewNonce();
        await sessions.Save(session, ct);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
