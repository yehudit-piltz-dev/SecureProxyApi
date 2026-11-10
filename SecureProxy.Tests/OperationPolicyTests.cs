using System.Net;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class OperationPolicyTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    // Brief 12.2: an unknown operation is rejected.
    [Fact]
    public async Task Unknown_operation_is_rejected()
    {
        var result = await ExecuteAsync(Envelope("no.such-operation", new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.NotFound, "unknown_operation");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Operation_key_is_matched_exactly_including_case()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key.ToUpperInvariant(), new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.NotFound, "unknown_operation");
        AssertNotForwarded();
    }

    // Brief 12.3: a disabled operation is rejected.
    [Fact]
    public async Task Disabled_operation_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.IsEnabled = false);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.Forbidden, "operation_disabled");
        AssertNotForwarded();
    }

    // Brief 12.4: a disabled destination is rejected.
    [Fact]
    public async Task Disabled_destination_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync(configureDestination: d => d.IsEnabled = false);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.Forbidden, "destination_unavailable");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Disabling_an_operation_takes_effect_for_the_next_request()
    {
        var operation = await Fixture.CreateOperationAsync();
        var before = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));
        Assert.Equal(HttpStatusCode.OK, before.Response.StatusCode);
        Upstream.Reset();

        await Fixture.WithDbAsync(db => db.Operations.Where(o => o.Id == operation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsEnabled, false)));

        var after = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));
        AssertProxyError(after, HttpStatusCode.Forbidden, "operation_disabled");
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("/customers/{urlId}")]
    [InlineData("../customers/{urlId}")]
    [InlineData("customers/{urlId}/..")]
    [InlineData("https://evil.test/{urlId}")]
    [InlineData("customers//{urlId}")]
    [InlineData("customers/{other}")]
    public async Task Operation_with_an_unsafe_path_template_is_never_forwarded(string template)
    {
        var operation = await Fixture.CreateOperationAsync(o => o.PathTemplate = template);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.InternalServerError, "operation_misconfigured");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Destination_with_credentials_in_its_base_url_is_never_forwarded()
    {
        var operation = await Fixture.CreateOperationAsync(configureDestination: d => d.BaseUrl = "https://user:secret@upstream.test/api/");

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.InternalServerError, "operation_misconfigured");
        Assert.DoesNotContain("secret", result.Body);
        AssertNotForwarded();
    }
}
