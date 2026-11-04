using BuySellBot.Domain;
using BuySellBot.Infrastructure;
using Microsoft.Extensions.Options;

namespace BuySellBot.Storage;

public interface IListingRepository
{
    Task<Listing?> Get(string id, CancellationToken ct);
    Task<IReadOnlyList<Listing>> All(CancellationToken ct);
    Task Save(Listing listing, CancellationToken ct);
    Task Delete(string id, CancellationToken ct);
}
public interface ISessionRepository
{
    Task<BuilderSession?> Get(long userId, SessionMode mode, CancellationToken ct);
    Task<SessionDocument> GetDocument(long userId, CancellationToken ct);
    Task<IReadOnlyList<SessionDocument>> All(CancellationToken ct);
    Task Save(BuilderSession session, CancellationToken ct);
    Task Delete(long userId, SessionMode mode, CancellationToken ct);
    Task Activate(long userId, SessionMode mode, CancellationToken ct);
}

public sealed class JsonListingRepository(IOptions<StorageOptions> options) : IListingRepository
{
    private readonly JsonFileStore<Listing> store = new(Path.Combine(options.Value.Path, "listings"));
    public Task<Listing?> Get(string id, CancellationToken ct) => store.Get(id, ct);
    public Task<IReadOnlyList<Listing>> All(CancellationToken ct) => store.All(ct);
    public Task Save(Listing listing, CancellationToken ct) => store.Save(listing.Id, listing, ct);
    public Task Delete(string id, CancellationToken ct) => store.Delete(id, ct);
}

public sealed class JsonSessionRepository(IOptions<StorageOptions> options) : ISessionRepository
{
    private readonly JsonFileStore<SessionDocument> store = new(Path.Combine(options.Value.Path, "sessions"));
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<SessionDocument> GetDocument(long userId, CancellationToken ct) =>
        await store.Get(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct) ?? new() { UserId = userId };
    public async Task<BuilderSession?> Get(long userId, SessionMode mode, CancellationToken ct)
    {
        var document = await GetDocument(userId, ct);
        return mode == SessionMode.Author ? document.Author : document.Moderator;
    }
    public Task<IReadOnlyList<SessionDocument>> All(CancellationToken ct) => store.All(ct);
    private async Task Mutate(long userId, Action<SessionDocument> mutate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await GetDocument(userId, ct);
            mutate(document);
            await store.Save(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), document, ct);
        }
        finally { gate.Release(); }
    }
    public Task Save(BuilderSession session, CancellationToken ct) => Mutate(session.UserId, document =>
    {
        if (session.Mode == SessionMode.Author) document.Author = session; else document.Moderator = session;
    }, ct);
    public Task Delete(long userId, SessionMode mode, CancellationToken ct) => Mutate(userId, document =>
    {
        if (mode == SessionMode.Author) document.Author = null; else document.Moderator = null;
        if (document.ActiveMode == mode) document.ActiveMode = mode == SessionMode.Author ? SessionMode.Moderator : SessionMode.Author;
    }, ct);
    public Task Activate(long userId, SessionMode mode, CancellationToken ct) =>
        Mutate(userId, document => document.ActiveMode = mode, ct);
}

public sealed class ListingLocks
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task<IDisposable> Acquire(string id, CancellationToken ct)
    {
        var gate = gates[(uint)StringComparer.Ordinal.GetHashCode(id) % (uint)gates.Length];
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}
