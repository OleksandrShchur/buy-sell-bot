using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuySellBot.Infrastructure;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace BuySellBot.Api;

public static class Endpoints
{
    public static void MapBotEndpoints(this WebApplication app)
    {
        app.MapMethods("/health", [HttpMethods.Get, HttpMethods.Head],
            IResult (HttpRequest request) => HttpMethods.IsHead(request.Method)
                ? Results.Ok()
                : Results.Ok(new { status = "ok" }));

        app.MapPost("/api/telegram/webhook", async (HttpRequest request, UpdateInbox inbox, IOptions<BotOptions> options, CancellationToken ct) =>
        {
            var supplied = request.Headers["X-Telegram-Bot-Api-Secret-Token"];
            if (supplied.Count != 1 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0] ?? "")),
                SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.WebhookSecret))))
                return Results.Unauthorized();
            Update? update;
            try { update = await JsonSerializer.DeserializeAsync<Update>(request.Body, UpdateInbox.TelegramJson, ct); }
            catch (JsonException) { return Results.BadRequest(); }
            if (update is null || update.Id < 0) return Results.BadRequest();
            return await inbox.Accept(update, ct) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });
    }
}
