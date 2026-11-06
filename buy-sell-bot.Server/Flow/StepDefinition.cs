using BuySellBot.Domain;

namespace BuySellBot.Flow;

public sealed record StepInput(string? Text = null, string? Arg = null, ListingPhoto? Photo = null, long? ContactOwner = null);
public sealed record StepResult(bool Complete, string? Error = null);
public sealed record StepChoice(string Label, string Argument);
public sealed record StepDefinition(
    string Id,
    Func<Listing, string> Label,
    Func<Listing, string> Prompt,
    bool Optional,
    Func<Listing, bool> AutoComplete,
    Func<Listing, BuilderSession, StepInput, StepResult> Apply,
    Action<Listing> Clear,
    Func<Listing, IReadOnlyList<StepChoice>> Choices);
