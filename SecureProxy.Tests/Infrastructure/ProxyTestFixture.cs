using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureProxy.Api.Data;
using SecureProxy.Api.Domain;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Tests.Infrastructure;

/// <summary>
/// Runs the real API in memory against a real, temporary SQL Server database (LocalDB by default),
/// with every destination replaced by <see cref="RecordingUpstreamHandler"/>.
/// Set <c>SECUREPROXY_TEST_SQL</c> to use another SQL Server; a unique database is created and dropped per run.
/// </summary>
public sealed class ProxyTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminKey = "test-admin-key-5f3c9a";
    public const string KeyReference = "test-key";
    public const string UpstreamBaseUrl = "https://upstream.test/api/";

    /// <summary>Fixed test key. Real keys never appear in source code.</summary>
    public static readonly byte[] EncryptionKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private readonly string connectionString;
    private int sequence;

    public ProxyTestFixture()
    {
        var server = Environment.GetEnvironmentVariable("SECUREPROXY_TEST_SQL")
                     ?? @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True";
        connectionString = new SqlConnectionStringBuilder(server)
        {
            InitialCatalog = $"SecureProxyTests_{Guid.NewGuid():N}",
        }.ConnectionString;
    }

    public RecordingUpstreamHandler Upstream { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": no automatic migration or sample data; the fixture prepares the database itself.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ProxyDatabase", connectionString);
        builder.UseSetting("Admin:ApiKey", AdminKey);
        builder.UseSetting($"EncryptionKeys:{KeyReference}", Convert.ToBase64String(EncryptionKey));
        builder.UseSetting("Proxy:RateLimitPermits", "100000");
        builder.UseSetting("Proxy:AdminRateLimitPermits", "100000");

        builder.ConfigureTestServices(services =>
            services.AddHttpClient(UpstreamForwarder.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Upstream)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
    }

    public async Task InitializeAsync()
    {
        await WithDbAsync(async db =>
        {
            // Applying the real migrations also tests them.
            await db.Database.MigrateAsync();

            // Tests change configuration directly in the database, so caching is off by default.
            // The cache itself is covered by ConfigurationCacheTests.
            db.OperationalSettings.Add(new OperationalSetting { Key = OperationalSettingKeys.ConfigurationCacheSeconds, Value = "0" });
            await db.SaveChangesAsync();
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await WithDbAsync(db => db.Database.EnsureDeletedAsync());
        await DisposeAsync();
    }

    public HttpClient CreateProxyClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task WithDbAsync(Func<ProxyDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ProxyDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<ProxyDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ProxyDbContext>());
    }

    /// <summary>
    /// Creates a destination and an enabled GET operation <c>customers/{urlId}</c> with rules for
    /// urlId (path, integer, required), includeOrders (query, boolean) and name (query, string, max 50),
    /// and the Accept-Language header allowlisted. <paramref name="configure"/> adjusts it before saving.
    /// </summary>
    public async Task<TestOperation> CreateOperationAsync(Action<Operation>? configure = null, Action<Destination>? configureDestination = null)
    {
        var n = Interlocked.Increment(ref sequence);
        var destination = new Destination { Name = $"destination-{n}", BaseUrl = UpstreamBaseUrl, IsEnabled = true, TimeoutMs = 5000 };
        configureDestination?.Invoke(destination);

        var operation = new Operation
        {
            Key = $"test.operation-{n}",
            HttpMethod = "GET",
            PathTemplate = "customers/{urlId}",
            IsEnabled = true,
            MaxRequestBytes = 4096,
            MaxResponseBytes = 65536,
            Destination = destination,
            ParameterRules =
            [
                new() { Name = "urlId", Location = ParameterLocation.Path, IsRequired = true, DataType = ParameterDataType.Integer, MinValue = 1, MaxValue = int.MaxValue },
                new() { Name = "includeOrders", Location = ParameterLocation.Query, DataType = ParameterDataType.Boolean },
                new() { Name = "name", Location = ParameterLocation.Query, DataType = ParameterDataType.String, MaxLength = 50 },
            ],
            HeaderRules = [new() { HeaderName = "Accept-Language" }],
        };
        configure?.Invoke(operation);

        await WithDbAsync(async db =>
        {
            db.Operations.Add(operation);
            await db.SaveChangesAsync();
        });

        return new TestOperation(operation.Id, operation.Key, destination.Id);
    }

    /// <summary>Creates an operation <c>PUT customers/{urlId}</c> that requires AES-GCM encryption with the test key.</summary>
    public Task<TestOperation> CreateEncryptedOperationAsync(string keyReference = KeyReference) =>
        CreateOperationAsync(o =>
        {
            o.HttpMethod = "PUT";
            o.RequiresEncryption = true;
            o.EncryptionProfile = new EncryptionProfile
            {
                Name = "profile-" + o.Key,
                Algorithm = "AES-256-GCM",
                KeyReference = keyReference,
                IsEnabled = true,
            };
            o.ParameterRules =
            [
                new() { Name = "urlId", Location = ParameterLocation.Path, IsRequired = true, DataType = ParameterDataType.Integer, MinValue = 1, MaxValue = int.MaxValue },
                new() { Name = "email", Location = ParameterLocation.Body, IsRequired = true, DataType = ParameterDataType.String, MaxLength = 254, Pattern = @"[^@\s]+@[^@\s]+\.[^@\s]+" },
            ];
        });

    /// <summary>Waits until the request's audit record has its final outcome. The record is completed just after the response.</summary>
    public async Task<RequestLog> WaitForAuditAsync(string correlationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var log = await WithDbAsync(db => db.RequestLogs.AsNoTracking().SingleOrDefaultAsync(l => l.CorrelationId == correlationId));
            if (log is { Outcome: not ExecutionOutcome.InProgress })
            {
                return log;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No completed audit record for {correlationId}.");
            }

            await Task.Delay(50);
        }
    }
}

public sealed record TestOperation(int Id, string Key, int DestinationId);

[CollectionDefinition(Name)]
public sealed class ProxyCollection : ICollectionFixture<ProxyTestFixture>
{
    public const string Name = "proxy";
}
