using System.Globalization;
using System.Text.RegularExpressions;
using BuySellBot.Domain;

namespace BuySellBot.Flow;

public static partial class PriceParser
{
    [GeneratedRegex(@"^\s*(?<a>\d+(?:[.,]\d{1,2})?)\s*(?:[-–—]\s*(?<b>\d+(?:[.,]\d{1,2})?))?\s*(?<c>грн|uah|usd|eur|₴|\$|€)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    public static bool TryParse(string? text, Currency currency, out Price? price)
    {
        price = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;
        // Only spaces BETWEEN digits are thousands separators.
        text = Regex.Replace(text, @"(?<=\d)[ \u00a0\u202f]+(?=\d)", "");
        var match = Pattern().Match(text);
        if (!match.Success || !Amount(match.Groups["a"].Value, out var amount)) return false;
        decimal? upper = null;
        if (match.Groups["b"].Success)
        {
            if (!Amount(match.Groups["b"].Value, out var parsed) || parsed < amount) return false;
            upper = parsed;
        }
        currency = match.Groups["c"].Value.ToLowerInvariant() switch
        {
            "$" or "usd" => Currency.USD, "€" or "eur" => Currency.EUR,
            "грн" or "uah" or "₴" => Currency.UAH, _ => currency
        };
        price = new(PriceKind.Fixed, amount, upper, currency);
        return true;
    }
    private static bool Amount(string text, out decimal value) =>
        decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) &&
        value is > 0 and <= 999999999m;
}
