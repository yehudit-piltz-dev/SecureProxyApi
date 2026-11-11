using System.Net;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class HealthTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    [Fact]
    public async Task Liveness_reports_healthy()
    {
        var response = await Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_reports_healthy_when_the_database_is_reachable()
    {
        var response = await Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
