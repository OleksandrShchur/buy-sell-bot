using System.Net;
using System.Text;
using BuySellBot.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuySellBot.Tests;

public sealed class WebhookTests
{
    [Fact]
    public async Task SecretIsRequiredAndDuplicateUpdatesAreQueuedOnce()
    {
        using var data = new BotFixture();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bot:Token"] = "123456:abcdefghijklmnopqrstuvwxyz123456",
                ["Bot:WebhookUrl"] = "https://example.test/api/telegram/webhook",
                ["Bot:WebhookSecret"] = "test-secret-12345678",
                ["Bot:ChatId"] = "-1001234567890",
                ["Bot:AdminIds:0"] = "100",
                ["Storage:Path"] = data.DirectoryPath
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
                services.RemoveAll<ITelegramGateway>();
                services.AddSingleton<ITelegramGateway>(data.Telegram);
            });
        });
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/health");
        using var head = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        const string json = "{\"update_id\":123,\"message\":{\"message_id\":1,\"date\":0,\"chat\":{\"id\":10,\"type\":\"private\"},\"from\":{\"id\":10,\"is_bot\":false,\"first_name\":\"Тест\"},\"text\":\"/start\"}}";
        using var missing = await client.PostAsync("/api/telegram/webhook", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        client.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", "test-secret-12345678");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/telegram/webhook", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/telegram/webhook", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        var inbox = factory.Services.GetRequiredService<UpdateInbox>();
        Assert.True(inbox.Channel.Reader.TryRead(out var update));
        Assert.Equal(123, update!.Id);
        Assert.False(inbox.Channel.Reader.TryRead(out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/telegram/webhook", new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);
        await inbox.Finish(123, true, CancellationToken.None);
        var restarted = new UpdateInbox(Microsoft.Extensions.Options.Options.Create(new StorageOptions { Path = data.DirectoryPath }));
        await restarted.Recover(CancellationToken.None);
        Assert.False(restarted.Channel.Reader.TryRead(out _));
    }
}
