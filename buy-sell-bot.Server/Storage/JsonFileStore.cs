using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuySellBot.Storage;

// Detached snapshots keep a caller's mutations out of the shared cache.
public sealed class JsonFileStore<T> where T : class
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string directory;
    private readonly ConcurrentDictionary<string, byte[]> cache = new();
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();

    public JsonFileStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        foreach (var file in Directory.EnumerateFiles(this.directory, "*.json"))
        {
            var bytes = File.ReadAllBytes(file);
            _ = JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException($"Invalid JSON document: {file}");
            cache[Path.GetFileNameWithoutExtension(file)] = bytes;
        }
    }
    private static void Validate(string key)
    {
        if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid storage key.", nameof(key));
    }
    private SemaphoreSlim Gate(string key) => gates[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)gates.Length];
    public Task<T?> Get(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Validate(key);
        return Task.FromResult(cache.TryGetValue(key, out var bytes) ? JsonSerializer.Deserialize<T>(bytes, Json) : null);
    }
    public Task<IReadOnlyList<T>> All(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<T>>(cache.Values.Select(bytes => JsonSerializer.Deserialize<T>(bytes, Json)!).ToArray());
    }
    public async Task Save(string key, T value, CancellationToken ct)
    {
        Validate(key);
        var gate = Gate(key);
        await gate.WaitAsync(ct);
        var temp = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Path.Combine(directory, key + ".json"), overwrite: true);
            cache[key] = bytes;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); gate.Release(); }
    }
    public async Task Delete(string key, CancellationToken ct)
    {
        Validate(key);
        var gate = Gate(key);
        await gate.WaitAsync(ct);
        try { File.Delete(Path.Combine(directory, key + ".json")); cache.TryRemove(key, out _); }
        finally { gate.Release(); }
    }
}
