using BuySellBot.Domain;
using BuySellBot.Flow;

namespace BuySellBot.Tests;

public sealed class FlowTests
{
    [Fact]
    public void SkippedStepsCountAndContactRequiresAnExplicitChoice()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        listing.ContactKind = null;
        var session = new BuilderSession { UserId = listing.AuthorUserId };
        bot.Engine.Start(session, listing);
        Assert.Equal(0, ProgressCalculator.Completed(session, bot.Engine.Steps));
        bot.Engine.Apply(session, listing, new(Arg: "0"));
        bot.Engine.Apply(session, listing, new(Text: "Назва"));
        bot.Engine.Skip(session, listing);
        Assert.Equal(3, ProgressCalculator.Completed(session, bot.Engine.Steps));
        bot.Engine.Apply(session, listing, new(Text: "500"));
        bot.Engine.Skip(session, listing);
        bot.Engine.Apply(session, listing, new(Text: "Львів"));
        bot.Engine.Skip(session, listing);
        Assert.Equal("contact", session.CurrentStepId);
        Assert.Null(listing.ContactKind);
        bot.Engine.Apply(session, listing, new(Arg: "telegram"));
        Assert.Equal(FlowEngine.Review, session.CurrentStepId);
        Assert.Equal(8, ProgressCalculator.Completed(session, bot.Engine.Steps));
        Assert.Contains("contact", session.CompletedSteps);
    }

    [Fact]
    public void BackSkipAndChangeReturnToCorrectScreen()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession();
        bot.Engine.Start(session, listing);
        bot.Engine.Skip(session, listing);
        Assert.Equal("category", session.CurrentStepId);
        Assert.NotNull(session.Warning);
        bot.Engine.Apply(session, listing, new(Arg: "0"));
        bot.Engine.Back(session, listing);
        Assert.Equal("category", session.CurrentStepId);
        Assert.Empty(session.CompletedSteps);
        bot.Engine.CompleteAll(session);
        Assert.True(bot.Engine.Change(session, listing, "title"));
        Assert.Equal(7, ProgressCalculator.Completed(session, bot.Engine.Steps));
        bot.Engine.Apply(session, listing, new(Text: "Нова назва"));
        Assert.Equal(FlowEngine.Review, session.CurrentStepId);
        Assert.Equal("Нова назва", listing.Title);
        Assert.False(session.ChangeMode);
    }

    [Fact]
    public void InvalidInputAndRequiredContactDoNotAdvance()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing(); listing.AuthorUsername = null;
        var session = new BuilderSession { UserId = listing.AuthorUserId, CurrentStepId = "title" };
        bot.Engine.Apply(session, listing, new(Text: "ab"));
        Assert.Equal("title", session.CurrentStepId);
        session.CurrentStepId = "contact";
        bot.Engine.Apply(session, listing, new(Text: "+380501234567", ContactOwner: 999));
        Assert.NotNull(session.Warning);
        bot.Engine.Apply(session, listing, new(Text: "+380 (50) 123-45-67", ContactOwner: listing.AuthorUserId));
        Assert.Equal("+380501234567", listing.ContactPhone);
        Assert.Equal(FlowEngine.Review, session.CurrentStepId);
    }

    [Fact]
    public void PhotosAreDeduplicatedAndCapped()
    {
        using var bot = new BotFixture();
        var listing = bot.Listing();
        var session = new BuilderSession { CurrentStepId = "photos" };
        for (var n = 0; n < 11; n++) bot.Engine.Apply(session, listing, new(Photo: new($"file{n}", $"unique{n}")));
        Assert.Equal(10, listing.Photos.Count);
        Assert.NotNull(session.Warning);
        bot.Engine.Apply(session, listing, new(Photo: new("different-size", "unique0")));
        Assert.Equal(10, listing.Photos.Count);
        bot.Engine.Apply(session, listing, new(Arg: "done"));
        Assert.Equal("location", session.CurrentStepId);
    }
}
