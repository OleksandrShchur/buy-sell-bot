using BuySellBot.Domain;
using BuySellBot.Storage;
using System.Text.Json;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Flow;

public sealed class FlowEngine(StepCatalog catalog)
{
    public const string Review = "review", Changes = "changes";
    public IReadOnlyList<StepDefinition> Steps => catalog.Steps;
    public StepDefinition? Current(BuilderSession session) => Steps.FirstOrDefault(s => s.Id == session.CurrentStepId);
    public void Start(BuilderSession session, Listing listing)
    {
        session.CurrentStepId = Steps[0].Id;
        AutoComplete(session, listing);
    }
    public void CompleteAll(BuilderSession session)
    {
        session.CompletedSteps = Steps.Select(s => s.Id).ToHashSet();
        session.CurrentStepId = Review;
    }
    public void Apply(BuilderSession session, Listing listing, StepInput input)
    {
        var step = Current(session);
        if (step is null) return;
        var result = step.Apply(listing, session, input);
        session.Warning = result.Error;
        if (result.Complete) Advance(session, listing);
    }
    public void Skip(BuilderSession session, Listing listing)
    {
        var step = Current(session);
        if (step?.Optional != true) { session.Warning = T.InvalidInput; return; }
        step.Clear(listing);
        Advance(session, listing);
    }
    private void Advance(BuilderSession session, Listing listing)
    {
        session.CompletedSteps.Add(session.CurrentStepId);
        session.ContactPhoneRequested = false;
        if (session.ChangeMode)
        {
            session.ChangeMode = false;
            session.ChangeSnapshot = null;
            session.CurrentStepId = Review;
        }
        else
        {
            var index = Steps.ToList().FindIndex(s => s.Id == session.CurrentStepId);
            session.CurrentStepId = index + 1 < Steps.Count ? Steps[index + 1].Id : Review;
        }
        session.Invalidate();
        AutoComplete(session, listing);
    }
    private void AutoComplete(BuilderSession session, Listing listing)
    {
        while (Current(session) is { } step && step.AutoComplete(listing))
        {
            session.CompletedSteps.Add(step.Id);
            var index = Steps.ToList().FindIndex(s => s.Id == step.Id);
            session.CurrentStepId = index + 1 < Steps.Count ? Steps[index + 1].Id : Review;
        }
    }
    public void Back(BuilderSession session, Listing listing)
    {
        session.Invalidate();
        if (session.ChangeMode) { Keep(session, listing); return; }
        if (session.CurrentStepId == Changes) { session.CurrentStepId = Review; return; }
        var index = session.CurrentStepId == Review ? Steps.Count : Steps.ToList().FindIndex(s => s.Id == session.CurrentStepId);
        do { index--; } while (index >= 0 && (Steps[index].AutoComplete(listing) || session.Mode == SessionMode.Moderator && Steps[index].Id == "contact"));
        if (index < 0) return;
        session.CurrentStepId = Steps[index].Id;
        foreach (var step in Steps.Skip(index)) session.CompletedSteps.Remove(step.Id);
    }
    public bool Change(BuilderSession session, Listing listing, string id)
    {
        var step = Steps.FirstOrDefault(s => s.Id == id);
        if (step is null || step.AutoComplete(listing) || session.Mode == SessionMode.Moderator && id == "contact") return false;
        session.Invalidate();
        session.ContactPhoneRequested = false;
        session.ChangeSnapshot = JsonSerializer.Deserialize<Listing>(
            JsonSerializer.Serialize(listing, JsonFileStore<Listing>.Json), JsonFileStore<Listing>.Json);
        session.CurrentStepId = id;
        session.ChangeMode = true;
        return true;
    }
    public void Keep(BuilderSession session, Listing listing)
    {
        if (session.ChangeSnapshot is { } original)
        {
            listing.CategoryId = original.CategoryId;
            listing.Title = original.Title;
            listing.Condition = original.Condition;
            listing.Price = original.Price;
            listing.Photos = original.Photos;
            listing.Location = original.Location;
            listing.Description = original.Description;
            listing.ContactPhone = original.ContactPhone;
            listing.ContactKind = original.ContactKind;
            session.Currency = original.Price?.Currency ?? Currency.UAH;
        }
        session.ChangeSnapshot = null;
        session.ContactPhoneRequested = false;
        session.Invalidate(); session.ChangeMode = false; session.CurrentStepId = Review;
    }
}

public static class ProgressCalculator
{
    public static int CurrentStep(BuilderSession session, IReadOnlyList<StepDefinition> steps)
    {
        var index = steps.ToList().FindIndex(step => step.Id == session.CurrentStepId);
        return index < 0 ? steps.Count : index + 1;
    }
    public static int Completed(BuilderSession session, IReadOnlyList<StepDefinition> steps) =>
        session.CurrentStepId is FlowEngine.Review or FlowEngine.Changes ? steps.Count :
        session.ChangeMode ? Math.Max(0, steps.Count - 1) :
        steps.Count(s => session.CompletedSteps.Contains(s.Id));
}
