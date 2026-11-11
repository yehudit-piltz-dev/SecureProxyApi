using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SecureProxy.Api.Proxy;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

/// <summary>
/// The proxy with SQL Server unreachable (DESIGN-NOTES.md, section 8): it fails closed, forwards nothing,
/// reports itself not ready, and reveals nothing about the database in its responses.
/// </summary>
public sealed class DatabaseUnavailableTests : IClassFixture<DatabaseUnavailableTests.UnreachableDatabaseFactory>
{
    private readonly UnreachableDatabaseFactory factory;
    private readonly HttpClient client;

    public DatabaseUnavailableTests(UnreachableDatabaseFactory factory)
    {
        this.factory = factory;
        factory.Upstream.Reset();
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Proxy_request_fails_closed_and_is_never_forwarded()
    {
        var response = await client.PostAsync("/api/proxy/execute", new StringContent(
            """{"clientId":"customer.get","json":{"urlId":1}}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"code\":\"configuration_unavailable\"", body);
        Assert.DoesNotContain(UnreachableDatabaseFactory.ServerAddress, body);
        Assert.Empty(factory.Upstream.Requests);
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_without_details()
    {
        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body);
    }

    [Fact]
    public async Task Liveness_stays_healthy_so_the_process_is_not_restarted()
    {
        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The API pointed at a SQL Server address where nothing listens.</summary>
    public sealed class UnreachableDatabaseFactory : WebApplicationFactory<Program>
    {
        public const string ServerAddress = "127.0.0.1,1";

        public RecordingUpstreamHandler Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:ProxyDatabase",
                $"Server=tcp:{ServerAddress};Database=SecureProxy;User Id=nobody;Password=none;Connect Timeout=2;ConnectRetryCount=0;Encrypt=False");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(UpstreamForwarder.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
        }
    }
}
