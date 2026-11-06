using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Flow;

public sealed class StepCatalog
{
    public IReadOnlyList<StepDefinition> Steps { get; }
    public StepCatalog(Catalog catalog)
    {
        Steps =
        [
            new("category", _ => T.CategoryLabel, _ => T.CategoryPrompt, false, _ => false,
                (l, _, i) =>
                {
                    if (!int.TryParse(i.Arg, out var index) || index < 0 || index >= catalog.Categories.Count) return new(false, T.InvalidInput);
                    l.CategoryId = catalog.Categories[index].Id; return new(true);
                }, l => l.CategoryId = "", _ => catalog.Categories.Select((c, n) => new StepChoice(c.Title, n.ToString())).ToArray()),
            new("title", _ => T.TitleLabel, _ => T.TitlePrompt, false, _ => false,
                (l, _, i) => SetText(i.Text, 3, 60, T.TitleError, value => l.Title = value),
                l => l.Title = "", _ => []),
            new("condition", _ => T.ConditionLabel, _ => T.SharedConditionPrompt, true, _ => false,
                (l, _, i) =>
                {
                    if (!int.TryParse(i.Arg, out var index) || index < 0 || index >= T.Conditions.Length) return new(false, T.InvalidInput);
                    l.Condition = T.Conditions[index]; return new(true);
                }, l => l.Condition = null, _ => T.Conditions.Select((c, n) => new StepChoice(c, n.ToString())).ToArray()),
            new("price", _ => T.PriceOrBudgetLabel, _ => T.SharedPricePrompt, false, _ => false,
                (l, s, i) =>
                {
                    if (i.Arg is "UAH" or "USD" or "EUR")
                    {
                        s.Currency = Enum.Parse<Currency>(i.Arg);
                        if (l.Price is not null) l.Price = l.Price with { Currency = s.Currency };
                        return new(false);
                    }
                    if (i.Arg is "neg" or "free" or "ex")
                    {
                        l.Price = new(i.Arg switch { "free" => PriceKind.Free, "ex" => PriceKind.Exchange, _ => PriceKind.Negotiable }, Currency: s.Currency);
                        return new(true);
                    }
                    if (!PriceParser.TryParse(i.Text, s.Currency, out var price)) return new(false, T.PriceError);
                    l.Price = price; s.Currency = price!.Currency; return new(true);
                }, l => l.Price = null, _ => [new(T.Negotiable, "neg"), new(T.Free, "free"), new(T.Exchange, "ex")]),
            new("photos", _ => T.PhotosLabel, _ => T.PhotoPrompt, true, _ => false,
                (l, _, i) =>
                {
                    if (i.Arg == "done" && l.Photos.Count > 0) return new(true);
                    if (i.Photo is null) return new(false, T.PhotoExpected);
                    if (l.Photos.Any(p => p.FileUniqueId == i.Photo.FileUniqueId)) return new(false);
                    if (l.Photos.Count >= 10) return new(false, T.PhotoLimit);
                    l.Photos.Add(i.Photo); return new(l.Photos.Count == 10);
                }, l => l.Photos.Clear(), l => l.Photos.Count is > 0 and < 10 ? [new(T.PhotoDone, "done")] : []),
            new("location", _ => T.LocationLabel, _ => T.LocationPrompt, false, _ => false,
                (l, _, i) =>
                {
                    var value = i.Text;
                    if (int.TryParse(i.Arg, out var index) && index >= 0 && index < catalog.Locations.Count) value = catalog.Locations[index];
                    return SetText(value, 2, 60, T.LocationError, text => l.Location = text);
                }, l => l.Location = "", _ => catalog.Locations.Select((c, n) => new StepChoice(c, n.ToString())).ToArray()),
            new("description", _ => T.DescriptionLabel, _ => T.DescriptionPrompt, true, _ => false,
                (l, _, i) => SetText(i.Text, 1, 500, T.DescriptionError, value => l.Description = value),
                l => l.Description = null, _ => []),
            new("contact", _ => T.ContactLabel, _ => T.ContactPrompt, false, _ => false,
                (l, s, i) =>
                {
                    if (s.Mode != SessionMode.Author || s.UserId != l.AuthorUserId) return new(false, T.ContactConsentRequired);
                    if (i.Arg == "share")
                    {
                        var hasUsername = !string.IsNullOrWhiteSpace(l.AuthorUsername);
                        var hasPhone = UkrainianPhoneParser.TryParse(l.ContactPhone, out var savedPhone);
                        if (!hasUsername && !hasPhone) return new(false, T.ContactError);
                        l.ContactPhone = savedPhone;
                        l.ContactKind = hasUsername && hasPhone ? ContactKind.TelegramAndPhone
                            : hasUsername ? ContactKind.Telegram : ContactKind.Phone;
                        return new(true);
                    }
                    if (i.Arg is not null) return new(false, T.ContactError);
                    if (i.ContactOwner is not null && i.ContactOwner != l.AuthorUserId) return new(false, T.ContactError);
                    if (!UkrainianPhoneParser.TryParse(i.Text, out var number)) return new(false, T.ContactError);
                    l.ContactPhone = number; l.ContactKind = ContactKind.Phone; return new(true);
                }, l => { l.ContactPhone = null; l.ContactKind = null; }, l =>
                    !string.IsNullOrWhiteSpace(l.AuthorUsername) || UkrainianPhoneParser.TryParse(l.ContactPhone, out _)
                        ? [new(T.ShareContact, "share")] : [])
        ];
    }
    private static StepResult SetText(string? raw, int min, int max, string error, Action<string> assign)
    {
        var text = raw?.Trim();
        if (text is null || text.Length < min || text.Length > max || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return new(false, error);
        assign(text); return new(true);
    }
}
