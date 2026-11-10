using System.Net;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class EnvelopeTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Content_type_other_than_json_is_rejected(string contentType)
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync($$$"""{"clientId":"{{{operation.Key}}}","json":{"urlId":1}}""", contentType);

        AssertProxyError(result, HttpStatusCode.UnsupportedMediaType, "invalid_content_type");
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("""{"json":{"urlId":1}}""")]
    public async Task Malformed_envelope_is_rejected(string body)
    {
        var result = await ExecuteRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, result.Response.StatusCode);
        Assert.Equal("proxy", result.Source);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("clientId")]
    [InlineData("ClientId")]   // differs only in case: some parsers would read this one instead
    public async Task Duplicate_envelope_field_is_rejected(string secondName)
    {
        var operation = await Fixture.CreateOperationAsync();
        var other = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync(
            $$$"""{"clientId":"{{{operation.Key}}}","{{{secondName}}}":"{{{other.Key}}}","json":{"urlId":1}}""");

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_envelope");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Envelope_with_both_plain_and_encrypted_parameters_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync(
            $$$"""{"clientId":"{{{operation.Key}}}","json":{"urlId":1},"encryptedJson":{"nonce":"","ciphertext":"","tag":""}}""");

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_envelope");
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("customer get")]
    [InlineData("../customer.get")]
    [InlineData("customer.get;drop")]
    [InlineData("")]
    public async Task Malformed_client_id_is_rejected(string clientId)
    {
        var result = await ExecuteAsync(Envelope(clientId, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_client_id");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_larger_than_the_operation_limit_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.MaxRequestBytes = 200);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1, name = new string('a', 300) }));

        AssertProxyError(result, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_larger_than_the_global_envelope_limit_is_rejected_before_parsing()
    {
        // Default global limit: 64 KB.
        var result = await ExecuteRawAsync("{\"clientId\":\"x\",\"json\":{\"pad\":\"" + new string('a', 70 * 1024) + "\"}}");

        AssertProxyError(result, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Deeply_nested_json_is_rejected()
    {
        var body = "{\"clientId\":\"x\",\"json\":" + new string('[', 100) + new string(']', 100) + "}";

        var result = await ExecuteRawAsync(body);

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_envelope");
        AssertNotForwarded();
    }
}
