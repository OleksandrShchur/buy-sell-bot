using System.Net;
using System.Text.RegularExpressions;
using BuySellBot.Domain;
using BuySellBot.Flow;
using BuySellBot.Infrastructure;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Publishing;

public sealed class PostFormatter(Catalog catalog)
{
    public static string Escape(string? value) => WebUtility.HtmlEncode(value ?? "");
    public static int VisibleLength(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Length;
    public string Format(Listing listing)
    {
        var category = catalog.Categories.FirstOrDefault(c => c.Id == listing.CategoryId);
        var hashtag = Regex.Replace(category?.Title.ToLowerInvariant() ?? listing.CategoryId, @"[^\p{L}\p{N}]+", "_").Trim('_');
        if (hashtag.Length > 32) hashtag = hashtag[..32];
        var lines = new List<string>
        {
            (listing.Intent switch { Intent.Sell => T.SellTag, Intent.Buy => T.BuyTag, _ => T.ListingTag }) +
                (hashtag.Length > 0 ? " #" + Escape(hashtag) : "")
        };
        if (!string.IsNullOrEmpty(listing.Title)) lines.Add($"<b>{Escape(listing.Title)}</b>");
        if (listing.Price is not null)
            lines.Add($"{T.PriceOrBudgetField}: {FormatPrice(listing.Price)}");
        if (!string.IsNullOrEmpty(listing.Condition)) lines.Add($"{T.ConditionField}: {Escape(listing.Condition)}");
        if (!string.IsNullOrEmpty(listing.Location)) lines.Add($"{T.LocationField}: {Escape(listing.Location)}");
        if (!string.IsNullOrEmpty(listing.Description)) lines.Add($"{T.DescriptionField}: {Escape(listing.Description)}");
        if (listing.ContactKind is { } kind)
        {
            var profile = !string.IsNullOrEmpty(listing.AuthorUsername) ? "@" + Escape(listing.AuthorUsername)
                : $"<a href=\"tg://user?id={listing.AuthorUserId}\">{Escape(listing.AuthorName)}</a>";
            var contact = kind == ContactKind.TelegramAndPhone ? $"{profile}, {Escape(listing.ContactPhone)}"
                : kind == ContactKind.Phone ? Escape(listing.ContactPhone)
                : profile;
            lines.Add($"{T.ContactField}: {contact}");
        }
        return string.Join("\n", lines);
    }
    public static string FormatPrice(Price? price)
    {
        if (price is null) return T.Empty;
        return price.Kind switch
        {
            PriceKind.Negotiable => T.Negotiable, PriceKind.Free => T.Free, PriceKind.Exchange => T.Exchange,
            _ => $"{T.Money(price.Amount ?? 0)}{(price.AmountTo is { } upper ? "–" + T.Money(upper) : "")} {T.CurrencySymbol(price.Currency)}"
        };
    }
    public string? Validate(Listing listing)
    {
        if (!catalog.Categories.Any(c => c.Id == listing.CategoryId) ||
            listing.Title.Trim().Length is < 3 or > 60 || listing.Location.Trim().Length is < 2 or > 60 ||
            listing.AuthorUserId <= 0 || listing.AuthorName.Length > 80 ||
            listing.Description?.Length > 500 || listing.Photos.Count > 10 ||
            listing.Photos.Select(p => p.FileUniqueId).Distinct().Count() != listing.Photos.Count ||
            listing.Photos.Any(p => string.IsNullOrEmpty(p.FileId) || string.IsNullOrEmpty(p.FileUniqueId)) ||
            listing.Price is null)
            return T.MissingFields;
        if (listing.ContactKind is null) return T.ContactConsentRequired;
        if (listing.ContactKind is ContactKind.Phone or ContactKind.TelegramAndPhone &&
            !UkrainianPhoneParser.TryParse(listing.ContactPhone, out _))
            return T.ContactError;
        if (listing.ContactKind is ContactKind.Telegram or ContactKind.TelegramAndPhone && string.IsNullOrWhiteSpace(listing.AuthorUsername))
            return T.ContactError;
        if (listing.Condition is not null && !T.Conditions.Contains(listing.Condition)) return T.MissingFields;
        if (listing.Price.Kind == PriceKind.Fixed &&
            (listing.Price.Amount is not (> 0 and <= 999999999m) ||
            listing.Price.AmountTo is { } upper && (upper < listing.Price.Amount || upper > 999999999m)))
            return T.PriceError;
        return VisibleLength(Format(listing)) > 1024 ? T.TooLong : null;
    }
}
