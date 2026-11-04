namespace BuySellBot.Domain;

public enum Intent { Buy, Sell, BuyOrSell }
public enum ContactKind { Telegram, Phone }
public enum ListingStatus { Draft, PendingApproval, Published, Declined, Cancelled }
public enum PriceKind { Fixed, Negotiable, Free, Exchange }
public enum Currency { UAH, USD, EUR }
public enum SessionMode { Author, Moderator }
public enum ModerationOperation { None, Declining, Editing, Publishing, Uncertain }

public sealed record Price(PriceKind Kind, decimal? Amount = null, decimal? AmountTo = null, Currency Currency = Currency.UAH);
public sealed record ListingPhoto(string FileId, string FileUniqueId);
public sealed record ModerationCard(long AdminId, int MessageId, string? RenderHash = null);

public sealed class Listing
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public long AuthorUserId { get; set; }
    public string? AuthorUsername { get; set; }
    public string AuthorName { get; set; } = "";
    public Intent Intent { get; set; } = Intent.BuyOrSell;
    public string CategoryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Condition { get; set; }
    public Price? Price { get; set; }
    public List<ListingPhoto> Photos { get; set; } = [];
    public string Location { get; set; } = "";
    public string? Description { get; set; }
    public string? ContactPhone { get; set; }
    public ContactKind? ContactKind { get; set; }
    public ListingStatus Status { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long? ModeratorId { get; set; }
    public string? DeclineReason { get; set; }
    public List<int> PublishedMessageIds { get; set; } = [];
    public List<ModerationCard> ModerationCards { get; set; } = [];
    public ModerationOperation Operation { get; set; }
    public bool EditedByModerator { get; set; }
    public bool AuthorNotified { get; set; }
    public string? PostLink { get; set; }
}

public sealed class BuilderSession
{
    public long UserId { get; set; }
    public string ListingId { get; set; } = "";
    public SessionMode Mode { get; set; }
    public string CurrentStepId { get; set; } = "";
    public bool ChangeMode { get; set; }
    public int? BuilderMessageId { get; set; }
    public List<int> ExtraMessageIds { get; set; } = [];
    public string Nonce { get; set; } = NewNonce();
    public HashSet<string> CompletedSteps { get; set; } = [];
    public Currency Currency { get; set; }
    public string? Warning { get; set; }
    public string? Confirmation { get; set; }
    public Intent? RestartIntent { get; set; }
    public Listing? WorkingCopy { get; set; }
    public string? LastRenderHash { get; set; }
    public long PhotoBatchVersion { get; set; }
    public string? MediaGroupId { get; set; }
    public Listing? ChangeSnapshot { get; set; }
    public List<int> AppliedInputIds { get; set; } = [];
    public List<int> ProductMessageIds { get; set; } = [];
    public List<string> ProductPhotoIds { get; set; } = [];
    public string? LastProductHash { get; set; }
    public bool ReplyKeyboardHidden { get; set; }
    public bool ContactPhoneRequested { get; set; }
    public static string NewNonce() => Guid.NewGuid().ToString("N")[..8];
    public void Invalidate() { Nonce = NewNonce(); Warning = null; Confirmation = null; }
}

public sealed class SessionDocument
{
    public long UserId { get; set; }
    public SessionMode ActiveMode { get; set; }
    public BuilderSession? Author { get; set; }
    public BuilderSession? Moderator { get; set; }
}
