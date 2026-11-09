using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Data;

/// <summary>
/// Creates sample configuration for local development. Runs only in the Development
/// environment and only when the database has no destinations yet.
/// </summary>
public static class DevelopmentDataSeeder
{
    public static async Task SeedAsync(ProxyDbContext db, string customersBaseUrl, CancellationToken cancellationToken = default)
    {
        if (await db.Destinations.AnyAsync(cancellationToken))
        {
            return;
        }

        var profile = new EncryptionProfile
        {
            Name = "customer-payload",
            Algorithm = "AES-256-GCM",
            KeyReference = "customer-v1",
            IsEnabled = true,
        };

        var customers = new Destination
        {
            Name = "customers-api",
            BaseUrl = customersBaseUrl,
            IsEnabled = true,
            TimeoutMs = 5000,
        };

        customers.Operations.Add(new Operation
        {
            Key = "customer.get",
            HttpMethod = "GET",
            PathTemplate = "customers/{urlId}",
            IsEnabled = true,
            MaxRequestBytes = 4 * 1024,
            MaxResponseBytes = 256 * 1024,
            ParameterRules =
            [
                new() { Name = "urlId", Location = ParameterLocation.Path, IsRequired = true, DataType = ParameterDataType.Integer, MinValue = 1, MaxValue = int.MaxValue },
                new() { Name = "includeOrders", Location = ParameterLocation.Query, IsRequired = false, DataType = ParameterDataType.Boolean },
            ],
            HeaderRules =
            [
                new() { HeaderName = "Accept-Language" },
            ],
        });

        customers.Operations.Add(new Operation
        {
            Key = "customer.search",
            HttpMethod = "GET",
            PathTemplate = "customers",
            IsEnabled = true,
            MaxRequestBytes = 4 * 1024,
            MaxResponseBytes = 1024 * 1024,
            ParameterRules =
            [
                new() { Name = "urlId", Location = ParameterLocation.Query, IsRequired = true, DataType = ParameterDataType.String, MaxLength = 50, Pattern = "^[A-Za-z0-9-]+$" },
                new() { Name = "name", Location = ParameterLocation.Query, IsRequired = true, DataType = ParameterDataType.String, MaxLength = 100 },
                new() { Name = "page", Location = ParameterLocation.Query, IsRequired = false, DataType = ParameterDataType.Integer, MinValue = 1, MaxValue = 1000 },
            ],
        });

        customers.Operations.Add(new Operation
        {
            Key = "customer.update",
            HttpMethod = "PUT",
            PathTemplate = "customers/{urlId}",
            IsEnabled = true,
            MaxRequestBytes = 16 * 1024,
            MaxResponseBytes = 64 * 1024,
            RequiresEncryption = true,
            EncryptionProfile = profile,
            ParameterRules =
            [
                new() { Name = "urlId", Location = ParameterLocation.Path, IsRequired = true, DataType = ParameterDataType.Integer, MinValue = 1, MaxValue = int.MaxValue },
                new() { Name = "email", Location = ParameterLocation.Body, IsRequired = true, DataType = ParameterDataType.String, MaxLength = 254, Pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$" },
                new() { Name = "phone", Location = ParameterLocation.Body, IsRequired = false, DataType = ParameterDataType.String, MaxLength = 20, Pattern = @"^\+?[0-9 -]+$" },
            ],
        });

        db.Destinations.Add(customers);
        db.OperationalSettings.AddRange(
            new OperationalSetting
            {
                Key = OperationalSettingKeys.ConfigurationCacheSeconds,
                Value = "30",
                Description = "Lifetime of cached operation and destination configuration.",
            },
            new OperationalSetting
            {
                Key = OperationalSettingKeys.MaxEnvelopeBytes,
                Value = (64 * 1024).ToString(),
                Description = "Upper bound for any request envelope, checked before the operation is resolved.",
            });

        await db.SaveChangesAsync(cancellationToken);
    }
}
