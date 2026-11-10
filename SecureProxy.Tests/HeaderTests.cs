using System.Net;
using SecureProxy.Api.Proxy;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class HeaderTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    // Brief 9.10: disallowed headers are not forwarded.
    [Fact]
    public async Task Only_allowlisted_headers_are_forwarded()
    {
        var operation = await Fixture.CreateOperationAsync(o =>
        {
            o.HeaderRules.Add(new() { HeaderName = "X-Tenant-Id" });

            // A sensitive header in the allowlist without AllowSensitive (possible by editing the
            // database directly; the admin API refuses it) is still not forwarded.
            o.HeaderRules.Add(new() { HeaderName = "Authorization", AllowSensitive = false });
            o.HeaderRules.Add(new() { HeaderName = "X-Forwarded-For" });
        });

        await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }), request =>
        {
            request.Headers.Add("Accept-Language", "he-IL");
            request.Headers.Add("X-Tenant-Id", "tenant-7");
            request.Headers.Add("Authorization", "Bearer caller-token");
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.Add("X-Secret", "do-not-forward");
            request.Headers.Add("X-Forwarded-For", "10.0.0.1");
        });

        var forwarded = AssertForwardedOnce();
        Assert.Equal("he-IL", forwarded.Headers["Accept-Language"]);
        Assert.Equal("tenant-7", forwarded.Headers["X-Tenant-Id"]);
        Assert.False(forwarded.Headers.ContainsKey("Authorization"));
        Assert.False(forwarded.Headers.ContainsKey("Cookie"));
        Assert.False(forwarded.Headers.ContainsKey("X-Secret"));
        Assert.False(forwarded.Headers.ContainsKey("X-Forwarded-For"));
    }

    [Fact]
    public async Task Malformed_correlation_id_is_replaced_and_never_forwarded()
    {
        // Even with a rule listing the header, the caller's value is not copied: the proxy sets it.
        var operation = await Fixture.CreateOperationAsync(o => o.HeaderRules.Add(new() { HeaderName = HeaderPolicy.CorrelationIdHeader }));

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }), request =>
        {
            request.Headers.Remove(HeaderPolicy.CorrelationIdHeader);
            request.Headers.Add(HeaderPolicy.CorrelationIdHeader, "bad id; drop table");
        });

        var issued = result.Response.Headers.GetValues(HeaderPolicy.CorrelationIdHeader).Single();
        Assert.NotEqual("bad id; drop table", issued);
        Assert.Equal(issued, AssertForwardedOnce().Headers[HeaderPolicy.CorrelationIdHeader]);
    }

    [Fact]
    public async Task Sensitive_header_is_forwarded_only_when_explicitly_allowed()
    {
        var operation = await Fixture.CreateOperationAsync(o =>
            o.HeaderRules.Add(new() { HeaderName = "Authorization", AllowSensitive = true }));

        await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }), request =>
            request.Headers.Add("Authorization", "Bearer caller-token"));

        Assert.Equal("Bearer caller-token", AssertForwardedOnce().Headers["Authorization"]);
    }

    [Fact]
    public async Task Upstream_response_headers_are_filtered()
    {
        var operation = await Fixture.CreateOperationAsync();
        Upstream.Responder = (_, _) =>
        {
            var response = RecordingUpstreamHandler.Json(HttpStatusCode.OK, "{}");
            response.Headers.Add("Set-Cookie", "upstream-session=1");
            response.Headers.Add("X-Internal-Host", "db01.corp");
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        };

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        Assert.False(result.Response.Headers.Contains("Set-Cookie"));
        Assert.False(result.Response.Headers.Contains("X-Internal-Host"));
        Assert.Equal("\"v1\"", result.Response.Headers.ETag?.Tag);
    }
}
