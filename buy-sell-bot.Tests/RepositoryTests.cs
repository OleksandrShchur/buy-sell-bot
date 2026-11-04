using System.Text.Json;
using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using BuySellBot.Storage;
using Microsoft.Extensions.Options;

namespace BuySellBot.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public async Task AtomicConcurrentWritesLeaveValidJsonAndDetachedCache()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        await bot.Listings.Save(listing, TestContextToken);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async n =>
        {
            var copy = bot.Listing(); copy.Id = listing.Id; copy.Title = "Назва " + n;
            await bot.Listings.Save(copy, TestContextToken);
        }));
        var path = Path.Combine(bot.DirectoryPath, "listings", listing.Id + ".json");
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContextToken));
        Assert.Equal(listing.Id, parsed.RootElement.GetProperty("id").GetString());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        var first = (await bot.Listings.Get(listing.Id, TestContextToken))!;
        first.Title = "Не збережено";
        Assert.NotEqual(first.Title, (await bot.Listings.Get(listing.Id, TestContextToken))!.Title);
        var reloaded = new JsonListingRepository(Options.Create(new StorageOptions { Path = bot.DirectoryPath }));
        Assert.Equal((await bot.Listings.Get(listing.Id, TestContextToken))!.Title, (await reloaded.Get(listing.Id, TestContextToken))!.Title);
    }
    [Fact]
    public async Task AuthorAndModeratorSessionsShareAFileWithoutColliding()
    {
        using var bot = new BotFixture();
        await Task.WhenAll(bot.Sessions.Save(new() { UserId = 10, ListingId = "1234abcd" }, TestContextToken),
            bot.Sessions.Save(new() { UserId = 10, ListingId = "deadbeef", Mode = SessionMode.Moderator }, TestContextToken));
        Assert.Equal("1234abcd", (await bot.Sessions.Get(10, SessionMode.Author, TestContextToken))!.ListingId);
        Assert.Equal("deadbeef", (await bot.Sessions.Get(10, SessionMode.Moderator, TestContextToken))!.ListingId);
        await bot.Sessions.Delete(10, SessionMode.Author, TestContextToken);
        Assert.NotNull(await bot.Sessions.Get(10, SessionMode.Moderator, TestContextToken));
        Assert.True(File.Exists(Path.Combine(bot.DirectoryPath, "sessions", "10.json")));
    }
    private static CancellationToken TestContextToken => CancellationToken.None;
}
