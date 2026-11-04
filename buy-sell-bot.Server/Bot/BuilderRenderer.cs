using BuySellBot.Domain;
using BuySellBot.Flow;
using BuySellBot.Publishing;
using Telegram.Bot.Types.ReplyMarkups;
using T = BuySellBot.Texts.Texts;

namespace BuySellBot.Bot;

public sealed record BuilderView(string ProductText, string ActionsText, InlineKeyboardMarkup Keyboard);
public sealed class BuilderRenderer(FlowEngine engine, PostFormatter formatter)
{
    public static ReplyKeyboardMarkup Menu => new(new KeyboardButton(T.CreateListing)) { ResizeKeyboard = true, OneTimeKeyboard = true };
    public BuilderView Render(Listing listing, BuilderSession session, bool hasOtherSession = false)
    {
        var rows = new List<InlineKeyboardButton[]>();
        InlineKeyboardButton Button(string label, string action, string arg = "") =>
            InlineKeyboardButton.WithCallbackData(label, new CallbackData(action, listing.Id, session.Nonce, arg, session.Mode).ToString());
        var preview = string.IsNullOrEmpty(listing.Title) && string.IsNullOrEmpty(listing.CategoryId) ? T.PostPlaceholder : formatter.Format(listing);
        var progress = T.Progress(ProgressCalculator.CurrentStep(session, engine.Steps), engine.Steps.Count);
        string prompt;
        if (session.Confirmation is { } confirmation)
        {
            prompt = confirmation == "restart" ? T.Restart : T.CancelQuestion;
            rows.Add([Button(T.Yes, "confirm"), Button(T.No, "no")]);
        }
        else if (session.CurrentStepId is FlowEngine.Review or FlowEngine.Changes)
        {
            var moderator = session.Mode == SessionMode.Moderator;
            if (session.CurrentStepId == FlowEngine.Changes)
            {
                prompt = T.ChangesQuestion;
                foreach (var group in engine.Steps.Where(s => !s.AutoComplete(listing) && (session.Mode == SessionMode.Author || s.Id != "contact")).Chunk(2))
                    rows.Add(group.Select(s => Button(s.Label(listing), "change", s.Id)).ToArray());
                rows.Add([Button(T.Back, "back")]);
                if (moderator) rows.Add([Button(T.Publish, "publish"), Button(T.StopEdit, "stop")]);
            }
            else
            {
                prompt = T.ReviewQuestion;
                rows.Add([Button(moderator ? T.Publish : T.Submit, moderator ? "publish" : "submit")]);
                rows.Add([Button(moderator ? T.ChangeMore : T.Change, "changes")]);
                rows.Add([Button(moderator ? T.StopEdit : T.Cancel, moderator ? "stop" : "cancel")]);
            }
        }
        else
        {
            var step = engine.Current(session) ?? throw new InvalidOperationException("Unknown step.");
            prompt = step.Prompt(listing);
            if (step.Id == "contact" && session.ContactPhoneRequested) prompt = T.PhonePrompt;
            foreach (var group in step.Choices(listing).Chunk(2))
                rows.Add(group.Select(c => Button(c.Label, "choose", c.Argument)).ToArray());
            if (step.Id == "price")
                rows.Add(Enum.GetValues<Currency>().Select(c => Button((session.Currency == c ? T.Selected : "") + T.CurrencySymbol(c), "choose", c.ToString())).ToArray());
            if (step.Optional) rows.Add([Button(T.Skip, "skip")]);
            if (session.ChangeMode) rows.Add([Button(T.Keep, "keep")]);
            rows.Add(session.ChangeMode || step.Id != engine.Steps[0].Id
                ? [Button(T.Back, "back"), Button(T.Cancel, "cancel")]
                : [Button(T.Cancel, "cancel")]);
        }
        if (hasOtherSession) rows.Add([Button(session.Mode == SessionMode.Author ? T.ModeratorSession : T.OwnSession, "switch")]);
        var warning = session.Warning is null ? "" : "\n" + T.Warning(PostFormatter.Escape(session.Warning));
        var count = session.CurrentStepId == "photos" && listing.Photos.Count > 0 ? "\n" + T.PhotoCount(listing.Photos.Count) : "";
        return new(preview, $"{T.BuilderHeader}\n{progress}\n\n{prompt}{count}{warning}", new(rows));
    }
}
