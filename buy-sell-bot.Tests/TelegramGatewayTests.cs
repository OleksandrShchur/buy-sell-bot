using System.Net;
using System.Text;
using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace BuySellBot.Tests;

public sealed class TelegramGatewayTests
{
    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> responsesQueue = new(responses);
        public List<(string Path, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath.Split('/').Last(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            var body = responsesQueue.Dequeue();
            using var json = System.Text.Json.JsonDocument.Parse(body);
            var status = json.RootElement.TryGetProperty("error_code", out var code) ? (HttpStatusCode)code.GetInt32() : HttpStatusCode.OK;
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private const string Message = "{\"ok\":true,\"result\":{\"message_id\":7,\"date\":0,\"chat\":{\"id\":10,\"type\":\"private\"},\"text\":\"ok\"}}";
    private const string Success = "{\"ok\":true,\"result\":true}";
    private static TelegramGateway Gateway(HttpClient http) => new(new TelegramBotClient(
        new TelegramBotClientOptions("123456:abcdefghijklmnopqrstuvwxyz123456") { RetryCount = 0, RetryThreshold = 0 }, http),
        Options.Create(new BotOptions()), NullLogger<TelegramGateway>.Instance);

    [Fact]
    public async Task ProductTextCanBeConvertedToAPhotoAndItsCaptionEdited()
    {
        using var handler = new ScriptedHandler(Message, Message);
        using var http = new HttpClient(handler);
        var gateway = Gateway(http);
        Assert.Equal(7, await gateway.EditPhoto(10, 7, new("file1", "unique1"), "<b>Товар</b>", CancellationToken.None));
        Assert.Equal(7, await gateway.EditCaption(10, 7, "<b>Новий опис</b>", CancellationToken.None));
        Assert.Equal(new[] { "editMessageMedia", "editMessageCaption" }, handler.Requests.Select(request => request.Path));
        Assert.Contains("file1", handler.Requests[0].Body);
        Assert.Contains("caption", handler.Requests[0].Body);
        Assert.Contains("HTML", handler.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingMediaMessageReportsThatTheProductMustBeRecreated()
    {
        using var handler = new ScriptedHandler("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message to edit not found\"}");
        using var http = new HttpClient(handler);
        Assert.Null(await Gateway(http).EditCaption(10, 7, "Текст", CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal("editMessageCaption", handler.Requests[0].Path);
    }

    [Fact]
    public async Task MissingBuilderMessageIsReplaced()
    {
        using var handler = new ScriptedHandler("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message to edit not found\"}", Message);
        using var http = new HttpClient(handler);
        Assert.Equal(7, await Gateway(http).EditText(10, 2, "Текст", null, CancellationToken.None));
        Assert.Equal(new[] { "editMessageText", "sendMessage" }, handler.Requests.Select(r => r.Path));
        foreach (var request in handler.Requests)
        {
            using var body = System.Text.Json.JsonDocument.Parse(request.Body);
            Assert.True(body.RootElement.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
        }
    }

    [Fact]
    public async Task UnchangedMessageIsNotReplaced()
    {
        using var handler = new ScriptedHandler("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is not modified\"}");
        using var http = new HttpClient(handler);
        Assert.Equal(2, await Gateway(http).EditText(10, 2, "Текст", null, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitUsesRetryAfter()
    {
        using var handler = new ScriptedHandler("{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\",\"parameters\":{\"retry_after\":1}}", Message);
        using var http = new HttpClient(handler);
        Assert.Equal(7, await Gateway(http).SendText(10, "Текст", null, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ForbiddenNotificationsAndOldDeletesAreHandled()
    {
        using var handler = new ScriptedHandler(
            "{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot was blocked by the user\"}",
            "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message can't be deleted\"}");
        using var http = new HttpClient(handler);
        var gateway = Gateway(http);
        Assert.True(await gateway.Notify(10, "Текст", null, CancellationToken.None));
        await gateway.Delete(10, 2, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SinglePhotoAndAlbumUseCorrectMethodsAndCaptionOnFirstItem()
    {
        const string album = "{\"ok\":true,\"result\":[{\"message_id\":8,\"date\":0,\"chat\":{\"id\":10,\"type\":\"private\"}},{\"message_id\":9,\"date\":0,\"chat\":{\"id\":10,\"type\":\"private\"}}]}";
        using var handler = new ScriptedHandler(Message, album);
        using var http = new HttpClient(handler);
        var gateway = Gateway(http);
        var photos = new ListingPhoto[] { new("file1", "unique1"), new("file2", "unique2") };
        Assert.Single(await gateway.SendPhotos(10, [photos[0]], "<b>Текст</b>", CancellationToken.None, 42));
        Assert.Equal(2, (await gateway.SendPhotos(10, photos, "<b>Текст</b>", CancellationToken.None, 42)).Count);
        Assert.Equal(new[] { "sendPhoto", "sendMediaGroup" }, handler.Requests.Select(r => r.Path));
        Assert.Contains("message_thread_id", handler.Requests[1].Body);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(handler.Requests[1].Body, "caption"));
        Assert.DoesNotContain("reply_markup", handler.Requests[1].Body);
    }

    [Fact]
    public async Task ConfigurationRegistersWebhookAndUkrainianCommands()
    {
        using var handler = new ScriptedHandler(Success, Success);
        using var http = new HttpClient(handler);
        await Gateway(http).Configure(CancellationToken.None);
        Assert.Equal(new[] { "setMyCommands", "setWebhook" }, handler.Requests.Select(r => r.Path));
        Assert.Contains("callback_query", handler.Requests[1].Body);
        Assert.Contains("message", handler.Requests[1].Body);
    }
}
