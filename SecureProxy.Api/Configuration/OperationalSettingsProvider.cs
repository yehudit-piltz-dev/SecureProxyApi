using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Configuration;

/// <summary>Reads operational settings from the database through the configuration cache.</summary>
public sealed class OperationalSettingsProvider(ProxyDbContext db, ConfigurationCache cache)
{
    /// <summary>Settings are re-read at least this often, independently of the configuration cache lifetime.</summary>
    private static readonly TimeSpan SettingsLifetime = TimeSpan.FromSeconds(60);

    public async Task<int> GetIntAsync(string key, int defaultValue, int min, int max, CancellationToken cancellationToken)
    {
        var settings = await cache.GetOrLoadAsync("settings", SettingsLifetime, async () =>
            await db.OperationalSettings.AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.Ordinal, cancellationToken));

        // An invalid or out-of-range value falls back to the default instead of disabling a limit.
        return settings!.TryGetValue(key, out var raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value >= min && value <= max
            ? value
            : defaultValue;
    }

    public Task<int> GetConfigurationCacheSecondsAsync(CancellationToken cancellationToken) =>
        GetIntAsync(OperationalSettingKeys.ConfigurationCacheSeconds, defaultValue: 30, min: 0, max: 3600, cancellationToken);

    public Task<int> GetMaxEnvelopeBytesAsync(CancellationToken cancellationToken) =>
        GetIntAsync(OperationalSettingKeys.MaxEnvelopeBytes, defaultValue: 64 * 1024, min: 1024, max: 1024 * 1024, cancellationToken);
}
