using BuySellBot.Api;
using BuySellBot.Bot;
using BuySellBot.Flow;
using BuySellBot.Infrastructure;
using BuySellBot.Moderation;
using BuySellBot.Publishing;
using BuySellBot.Storage;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.AddOptions<BotOptions>().BindConfiguration("Bot").ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BotOptions>, BotOptionsValidator>();
builder.Services.AddOptions<StorageOptions>().BindConfiguration("Storage")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Path), "Storage:Path is required.").ValidateOnStart();
var catalog = new Catalog
{
    Categories = builder.Configuration.GetSection("Categories").Get<List<CategoryOptions>>() ?? [],
    Locations = builder.Configuration.GetSection("Locations").Get<List<string>>() ?? []
};
if (catalog.Categories.Count == 0 || catalog.Categories.Select(c => c.Id).Distinct().Count() != catalog.Categories.Count ||
    catalog.Categories.Any(c => !System.Text.RegularExpressions.Regex.IsMatch(c.Id, "^[a-z0-9_-]{1,32}$") ||
        c.Title.Length is < 1 or > 80 || c.TopicId < 0) ||
    catalog.Locations.Any(l => l.Length is < 2 or > 60) || catalog.Categories.Count > 40 || catalog.Locations.Count > 40)
    throw new InvalidOperationException("Configure 1-40 unique categories (safe ids, title <= 80, TopicId >= 0) and at most 40 locations (2-60 chars).");
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(new TelegramBotClientOptions(
    sp.GetRequiredService<IOptions<BotOptions>>().Value.Token) { RetryThreshold = 0, RetryCount = 0 }));
builder.Services.AddSingleton<ITelegramGateway, TelegramGateway>();
builder.Services.AddSingleton<IListingRepository, JsonListingRepository>();
builder.Services.AddSingleton<ISessionRepository, JsonSessionRepository>();
builder.Services.AddSingleton<ListingLocks>();
builder.Services.AddSingleton<UserLocks>();
builder.Services.AddSingleton<StepCatalog>();
builder.Services.AddSingleton<FlowEngine>();
builder.Services.AddSingleton<PostFormatter>();
builder.Services.AddSingleton<BuilderRenderer>();
builder.Services.AddSingleton<BuilderUi>();
builder.Services.AddSingleton<Publisher>();
builder.Services.AddSingleton<ModerationService>();
builder.Services.AddSingleton<PhotoDebouncer>();
builder.Services.AddSingleton<UpdateRouter>();
builder.Services.AddSingleton<UpdateInbox>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PhotoDebouncer>());
builder.Services.AddHostedService<UpdateProcessor>();
builder.Services.AddHostedService<WebhookRegistrationService>();
var app = builder.Build();
_ = app.Services.GetRequiredService<IListingRepository>();
_ = app.Services.GetRequiredService<ISessionRepository>();
app.MapBotEndpoints();
app.Run();

public partial class Program;
