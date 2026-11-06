using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using BuySellBot.Storage;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Publishing;

public sealed class Publisher(
    ITelegramGateway telegram, IListingRepository listings, PostFormatter formatter,
    Catalog catalog, IOptions<BotOptions> options, ILogger<Publisher> logger)
{
    public void EnsureReady(Listing listing)
    {
        var error = formatter.Validate(listing);
        if (error is not null)
            throw new PublicationBlockedException($"Listing {listing.Id} failed publication validation: {error}", T.PublicationValidationFailed(error));
        if (options.Value.ChatId == 0)
            throw new PublicationBlockedException("Bot:ChatId is 0. Set the supergroup id and restart the bot.", T.PublicationChatMissing);
        var category = catalog.Categories.Single(c => c.Id == listing.CategoryId);
        if (category.TopicId <= 0)
        {
            var index = catalog.Categories.IndexOf(category);
            throw new PublicationBlockedException($"Categories:{index}:TopicId is not configured for category '{category.Id}'.", T.PublicationTopicMissing(category.Title, index));
        }
    }
    // Caller holds ListingLocks for the entire operation.
    public async Task Publish(Listing listing, CancellationToken ct)
    {
        if (listing.Status == ListingStatus.Published) return;
        if (listing.Status != ListingStatus.PendingApproval || listing.Operation is ModerationOperation.Publishing or ModerationOperation.Uncertain)
            throw new InvalidOperationException("Listing is not publishable.");
        EnsureReady(listing);
        var topic = catalog.Categories.Single(c => c.Id == listing.CategoryId).TopicId;
        var previous = listing.Operation;
        listing.Operation = ModerationOperation.Publishing;
        listing.UpdatedAt = DateTimeOffset.UtcNow;
        await listings.Save(listing, ct);
        IReadOnlyList<int> ids;
        try
        {
            var text = formatter.Format(listing);
            ids = listing.Photos.Count > 0
                ? await telegram.SendPhotos(options.Value.ChatId, listing.Photos, text, ct, topic)
                : [await telegram.SendText(options.Value.ChatId, text, null, ct, topic)];
        }
        catch (ApiRequestException ex) when (ex.ErrorCode is >= 400 and < 500)
        {
            listing.Operation = previous;
            await listings.Save(listing, CancellationToken.None);
            throw;
        }
        catch
        {
            // A transport failure may occur AFTER Telegram accepted the post. Never blindly retry.
            listing.Operation = ModerationOperation.Uncertain;
            await listings.Save(listing, CancellationToken.None);
            logger.LogError("Publication outcome unknown for {ListingId}; inspect the target topic before recovery", listing.Id);
            throw;
        }
        listing.PublishedMessageIds = ids.ToList();
        listing.PostLink = BuildLink(options.Value.ChatId, topic, ids[0]);
        listing.Status = ListingStatus.Published;
        listing.Operation = ModerationOperation.None;
        listing.Revision++;
        listing.UpdatedAt = DateTimeOffset.UtcNow;
        await listings.Save(listing, ct);
    }
    public static string BuildLink(long chatId, int topicId, int messageId)
    {
        var text = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!text.StartsWith("-100", StringComparison.Ordinal)) throw new ArgumentException("Expected a supergroup id.", nameof(chatId));
        return $"https://t.me/c/{text[4..]}/{topicId}/{messageId}";
    }
}
