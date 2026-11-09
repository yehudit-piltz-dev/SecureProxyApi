using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;

namespace SecureProxy.Api.Configuration;

/// <summary>Finds operations by key, returning cached snapshots.</summary>
public sealed class OperationConfigurationProvider(
    ProxyDbContext db,
    ConfigurationCache cache,
    OperationalSettingsProvider settings)
{
    public async Task<OperationSnapshot?> FindAsync(string operationKey, CancellationToken cancellationToken)
    {
        var cacheSeconds = await settings.GetConfigurationCacheSecondsAsync(cancellationToken);
        if (cacheSeconds == 0)
        {
            return await LoadAsync(operationKey, cancellationToken);
        }

        return await cache.GetOrLoadAsync(
            "operation:" + operationKey,
            TimeSpan.FromSeconds(cacheSeconds),
            () => LoadAsync(operationKey, cancellationToken));
    }

    private async Task<OperationSnapshot?> LoadAsync(string operationKey, CancellationToken cancellationToken)
    {
        var operation = await db.Operations
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.Destination)
            .Include(o => o.EncryptionProfile)
            .Include(o => o.ParameterRules)
            .Include(o => o.HeaderRules)
            .SingleOrDefaultAsync(o => o.Key == operationKey, cancellationToken);

        // SQL Server compares case-insensitively by default. Keys are matched exactly,
        // so "Customer.Get" is unknown rather than an alias of "customer.get".
        if (operation is null || !string.Equals(operation.Key, operationKey, StringComparison.Ordinal))
        {
            return null;
        }

        return OperationSnapshotFactory.Create(operation);
    }
}
