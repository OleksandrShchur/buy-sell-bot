using System.Text.RegularExpressions;

namespace BuySellBot.Infrastructure;

public static partial class ExceptionDiagnostics
{
    [GeneratedRegex(@"\b\d+:[A-Za-z0-9_-]{20,}")]
    private static partial Regex BotTokenPattern();

    public static string Format(Exception exception, BotOptions options)
    {
        var details = exception.ToString();
        foreach (var secret in new[] { options.Token, options.WebhookSecret })
            if (!string.IsNullOrEmpty(secret)) details = details.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return BotTokenPattern().Replace(details, "[REDACTED]");
    }
}
