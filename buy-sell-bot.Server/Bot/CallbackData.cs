using System.Text;
using System.Text.RegularExpressions;
using BuySellBot.Domain;

namespace BuySellBot.Bot;

public sealed record CallbackData(string Action, string ListingId, string Nonce, string Arg = "", SessionMode Mode = SessionMode.Author)
{
    public override string ToString()
    {
        var value = $"v1|{Action}|{ListingId}|{Nonce}|{Arg}|{(Mode == SessionMode.Author ? "a" : "m")}";
        if (!ValidParts(Action, ListingId, Nonce, Arg) || Encoding.UTF8.GetByteCount(value) > 64)
            throw new ArgumentException("Callback data must use safe fields and fit in 64 UTF-8 bytes.");
        return value;
    }
    private static bool ValidParts(params string[] parts) =>
        parts.All(p => Regex.IsMatch(p, "^[a-zA-Z0-9_-]*$")) && parts[0].Length > 0 &&
        Regex.IsMatch(parts[1], "^[a-f0-9]{8}$") && parts[2].Length is > 0 and <= 10;
    public static bool TryParse(string? value, out CallbackData? data)
    {
        data = null;
        if (value is null || Encoding.UTF8.GetByteCount(value) > 64) return false;
        var parts = value.Split('|');
        if (parts.Length != 6 || parts[0] != "v1" || parts[5] is not ("a" or "m") ||
            !ValidParts(parts[1], parts[2], parts[3], parts[4])) return false;
        data = new(parts[1], parts[2], parts[3], parts[4], parts[5] == "a" ? SessionMode.Author : SessionMode.Moderator);
        return true;
    }
}
