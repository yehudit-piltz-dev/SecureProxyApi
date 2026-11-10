using System.Net;
using SecureProxy.Api.Proxy;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class ForwardingTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    // Brief 12.1: a valid configured operation is forwarded.
    [Fact]
    public async Task Valid_operation_is_forwarded_to_the_configured_destination()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 42, includeOrders = true }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        Assert.Equal("upstream", result.Source);
        Assert.Contains("\"source\":\"upstream\"", result.Body);

        var forwarded = AssertForwardedOnce();
        Assert.Equal(HttpMethod.Get, forwarded.Method);
        Assert.Equal("https://upstream.test/api/customers/42?includeOrders=true", forwarded.Uri.AbsoluteUri);
        Assert.Equal(result.CorrelationId, forwarded.Headers[HeaderPolicy.CorrelationIdHeader]);
        Assert.Null(forwarded.Body);
    }

    [Fact]
    public async Task Body_parameters_are_sent_as_json_with_only_validated_fields()
    {
        var operation = await Fixture.CreateOperationAsync(o =>
        {
            o.HttpMethod = "POST";
            o.PathTemplate = "customers/{urlId}/notes";
            o.ParameterRules.Add(new() { Name = "note", Location = Api.Domain.ParameterLocation.Body, IsRequired = true, DataType = Api.Domain.ParameterDataType.String, MaxLength = 200 });
        });

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 7, note = "call back" }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        var forwarded = AssertForwardedOnce();
        Assert.Equal(HttpMethod.Post, forwarded.Method);
        Assert.Equal("https://upstream.test/api/customers/7/notes", forwarded.Uri.AbsoluteUri);
        Assert.Equal("""{"note":"call back"}""", forwarded.Body);
        Assert.StartsWith("application/json", forwarded.Headers["Content-Type"]);
    }

    // Brief 12.5: a caller-supplied destination URL cannot override configuration.
    [Theory]
    [InlineData("url", "\"https://evil.test/steal\"")]
    [InlineData("destinationUrl", "\"https://evil.test/steal\"")]
    [InlineData("baseUrl", "\"https://evil.test/\"")]
    [InlineData("path", "\"/admin/delete-all\"")]
    [InlineData("method", "\"DELETE\"")]
    [InlineData("headers", "{\"Authorization\":\"Bearer x\"}")]
    public async Task Envelope_fields_that_try_to_override_configuration_are_rejected(string field, string value)
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync($$$"""{"clientId":"{{{operation.Key}}}","{{{field}}}":{{{value}}},"json":{"urlId":1}}""");

        AssertProxyError(result, HttpStatusCode.BadRequest, "unknown_envelope_field");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Url_sent_as_a_parameter_is_rejected_as_unknown()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1, url = "https://evil.test/" }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "unknown_parameter");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Url_inside_a_query_value_stays_a_value_and_cannot_change_the_destination()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1, name = "x&host=evil.test#https://evil.test/" }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        var forwarded = AssertForwardedOnce();
        Assert.Equal("upstream.test", forwarded.Uri.Host);
        Assert.Equal("/api/customers/1", forwarded.Uri.AbsolutePath);
        Assert.Equal("?name=x%26host%3Devil.test%23https%3A%2F%2Fevil.test%2F", forwarded.Uri.Query);
    }

    // Brief 12.6: an invalid or disallowed HTTP method is rejected.
    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Calling_the_proxy_with_a_method_other_than_post_is_rejected(string method)
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync(
            $$$"""{"clientId":"{{{operation.Key}}}","json":{"urlId":1}}""", method: new HttpMethod(method));

        AssertProxyError(result, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
        Assert.Contains("POST", result.Response.Content.Headers.Allow);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    [InlineData("HEAD")]
    [InlineData("FETCH")]
    public async Task Operation_configured_with_a_disallowed_method_is_never_forwarded(string method)
    {
        // Written directly to the database, bypassing the admin API's validation:
        // the proxy re-validates configuration on load and fails closed.
        var operation = await Fixture.CreateOperationAsync(o => o.HttpMethod = method);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.InternalServerError, "operation_misconfigured");
        AssertNotForwarded();
    }

    [Fact]
    public async Task The_upstream_method_comes_from_configuration()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.HttpMethod = "DELETE");

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 5 }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        Assert.Equal(HttpMethod.Delete, AssertForwardedOnce().Method);
    }

    [Fact]
    public async Task Redirects_from_the_destination_are_not_followed_and_location_is_not_exposed()
    {
        var operation = await Fixture.CreateOperationAsync();
        Upstream.Responder = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://internal-host.corp/secret");
            return Task.FromResult(response);
        };

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        Assert.Equal(HttpStatusCode.Found, result.Response.StatusCode);
        Assert.Null(result.Response.Headers.Location);
        AssertForwardedOnce();
    }
}
