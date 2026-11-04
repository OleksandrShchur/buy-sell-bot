namespace BuySellBot.Infrastructure;

// Used by both update processing and photo debounce rendering.
public sealed class UserLocks
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task<IDisposable> Acquire(long userId, CancellationToken ct)
    {
        var gate = gates[(uint)userId.GetHashCode() % (uint)gates.Length];
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}
