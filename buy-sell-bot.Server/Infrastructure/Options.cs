using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace BuySellBot.Infrastructure;

public sealed class BotOptions
{
    public string Token { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public long ChatId { get; set; }
    public string ChatUrl { get; set; } = "";
    public long[] AdminIds { get; set; } = [];
    public bool DeleteUserMessages { get; set; } = true;
    public int MaxPendingPerUser { get; set; } = 3;
    public int DraftTtlDays { get; set; } = 7;
}

public sealed class StorageOptions { public string Path { get; set; } = "./data"; }
public sealed record CategoryOptions
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int TopicId { get; init; }
}
public sealed class Catalog
{
    public List<CategoryOptions> Categories { get; set; } = [];
    public List<string> Locations { get; set; } = [];
}

public sealed class BotOptionsValidator : IValidateOptions<BotOptions>
{
    public ValidateOptionsResult Validate(string? name, BotOptions value)
    {
        var errors = new List<string>();
        if (!Regex.IsMatch(value.Token, @"^\d+:[A-Za-z0-9_-]{20,}$")) errors.Add("Bot:Token must be a BotFather token.");
        if (!Uri.TryCreate(value.WebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/api/telegram/webhook" || !string.IsNullOrEmpty(uri.Query))
            errors.Add("Bot:WebhookUrl must be https://<public-host>/api/telegram/webhook.");
        if (!Regex.IsMatch(value.WebhookSecret, @"^[A-Za-z0-9_-]{16,256}$")) errors.Add("Bot:WebhookSecret must contain 16-256 letters, digits, underscores or hyphens.");
        if (value.ChatId != 0 && value.ChatId >= -1000000000000L) errors.Add("Bot:ChatId must be a -100… supergroup id (or 0 during topic discovery).");
        if (!string.IsNullOrEmpty(value.ChatUrl) &&
            (!Uri.TryCreate(value.ChatUrl, UriKind.Absolute, out var chatUrl) || chatUrl.Scheme != "https"))
            errors.Add("Bot:ChatUrl must be an HTTPS link to the community chat.");
        if (value.AdminIds.Length == 0 || value.AdminIds.Any(id => id <= 0)) errors.Add("Bot:AdminIds must contain positive Telegram user ids.");
        if (value.MaxPendingPerUser < 1 || value.DraftTtlDays < 1) errors.Add("Pending limit and draft TTL must be positive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
