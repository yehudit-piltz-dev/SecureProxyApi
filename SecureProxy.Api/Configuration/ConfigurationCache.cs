using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace SecureProxy.Api.Configuration;

/// <summary>
/// Cache of configuration snapshots for this process. Entries expire after the configured lifetime,
/// and <see cref="InvalidateAll"/> evicts every entry at once after a configuration change.
/// </summary>
public sealed class ConfigurationCache(IMemoryCache cache)
{
    private CancellationTokenSource generation = new();

    public async Task<T?> GetOrLoadAsync<T>(string key, TimeSpan lifetime, Func<Task<T?>> load)
        where T : class
    {
        if (cache.TryGetValue(key, out T? cached))
        {
            return cached;
        }

        // Take the token before loading, so an invalidation that happens during the load
        // also evicts the value being loaded.
        var token = new CancellationChangeToken(Volatile.Read(ref generation).Token);
        var value = await load();

        // Misses are not cached: otherwise random keys would fill the cache.
        if (value is not null)
        {
            using var entry = cache.CreateEntry(key);
            entry.Value = value;
            entry.AbsoluteExpirationRelativeToNow = lifetime;
            entry.AddExpirationToken(token);
        }

        return value;
    }

    /// <summary>Evicts every cached snapshot. New requests then load the current configuration.</summary>
    public void InvalidateAll()
    {
        var previous = Interlocked.Exchange(ref generation, new CancellationTokenSource());
        previous.Cancel();
    }
}
