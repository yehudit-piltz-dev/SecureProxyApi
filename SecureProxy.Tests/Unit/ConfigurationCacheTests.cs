using Microsoft.Extensions.Caching.Memory;
using SecureProxy.Api.Configuration;

namespace SecureProxy.Tests.Unit;

/// <summary>The consistency model of cached configuration (see DESIGN-NOTES.md).</summary>
public sealed class ConfigurationCacheTests
{
    [Fact]
    public async Task Cached_value_is_reused_until_invalidated()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new ConfigurationCache(memory);
        var loads = 0;
        Task<string?> Load() => Task.FromResult<string?>($"version-{++loads}");

        Assert.Equal("version-1", await cache.GetOrLoadAsync("k", TimeSpan.FromMinutes(5), Load));
        Assert.Equal("version-1", await cache.GetOrLoadAsync("k", TimeSpan.FromMinutes(5), Load));

        cache.InvalidateAll();

        Assert.Equal("version-2", await cache.GetOrLoadAsync("k", TimeSpan.FromMinutes(5), Load));
    }

    [Fact]
    public async Task Invalidation_during_a_load_evicts_the_value_being_loaded()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new ConfigurationCache(memory);
        var loads = 0;

        // The configuration changes while the first load is reading the old version.
        await cache.GetOrLoadAsync("k", TimeSpan.FromMinutes(5), () =>
        {
            loads++;
            cache.InvalidateAll();
            return Task.FromResult<string?>("stale");
        });

        var next = await cache.GetOrLoadAsync("k", TimeSpan.FromMinutes(5), () =>
        {
            loads++;
            return Task.FromResult<string?>("fresh");
        });

        Assert.Equal("fresh", next);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Misses_are_not_cached()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new ConfigurationCache(memory);
        var loads = 0;

        await cache.GetOrLoadAsync<string>("missing", TimeSpan.FromMinutes(5), () => { loads++; return Task.FromResult<string?>(null); });
        await cache.GetOrLoadAsync<string>("missing", TimeSpan.FromMinutes(5), () => { loads++; return Task.FromResult<string?>(null); });

        Assert.Equal(2, loads);
    }
}
