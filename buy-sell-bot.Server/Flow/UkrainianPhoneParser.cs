using System.Text.RegularExpressions;

namespace BuySellBot.Flow;

public static class UkrainianPhoneParser
{
    public static bool TryParse(string? input, out string? phone)
    {
        phone = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 128) return false;
        // Accept domestic, international, and country-code-free input with common separators.
        var compact = Regex.Replace(input, @"[\s().\p{Pd}]", "");
        var match = Regex.Match(compact, @"\A(?:(?:\+|00)?380|0)?([1-9][0-9]{8})\z");
        if (!match.Success) return false;
        phone = "+380" + match.Groups[1].Value;
        return true;
    }
}
