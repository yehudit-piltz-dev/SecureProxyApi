using System.Net;
using SecureProxy.Api.Domain;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class ParameterTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    // Brief 12.7: missing required parameters are rejected.
    [Fact]
    public async Task Missing_required_parameter_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync(o =>
            o.ParameterRules.Single(r => r.Name == "name").IsRequired = true);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "missing_parameter");
        Assert.Equal("name", result.Json.GetProperty("parameter").GetString());
        AssertNotForwarded();
    }

    [Fact]
    public async Task Missing_url_id_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { includeOrders = true }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "missing_url_id");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Null_required_parameter_counts_as_missing()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync($$$"""{"clientId":"{{{operation.Key}}}","json":{"urlId":null}}""");

        AssertProxyError(result, HttpStatusCode.BadRequest, "missing_parameter");
        AssertNotForwarded();
    }

    // Brief 12.8: unknown parameters are handled according to the operation policy.
    [Fact]
    public async Task Unknown_parameter_is_rejected_by_default()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1, isAdmin = true }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "unknown_parameter");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Unknown_parameter_is_accepted_but_not_forwarded_when_the_operation_allows_it()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.AllowUnknownParameters = true);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1, isAdmin = true }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        var forwarded = AssertForwardedOnce();
        Assert.Equal("https://upstream.test/api/customers/1", forwarded.Uri.AbsoluteUri);
        Assert.DoesNotContain("isAdmin", forwarded.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Duplicate_parameter_names_are_rejected()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync($$$"""{"clientId":"{{{operation.Key}}}","json":{"urlId":1,"urlId":2}}""");

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_parameters");
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("\"42\"")]          // a string, not a number
    [InlineData("4.5")]             // not an integer
    [InlineData("0")]               // below MinValue
    [InlineData("true")]
    [InlineData("[1]")]
    [InlineData("{\"id\":1}")]
    public async Task Value_of_the_wrong_type_or_out_of_range_is_rejected(string urlId)
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteRawAsync($$$"""{"clientId":"{{{operation.Key}}}","json":{"urlId":{{{urlId}}}}}""");

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_parameter");
        Assert.Equal("urlId", result.Json.GetProperty("parameter").GetString());
        AssertNotForwarded();
    }

    [Fact]
    public async Task String_longer_than_max_length_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1, name = new string('a', 51) }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_parameter");
        AssertNotForwarded();
    }

    [Fact]
    public async Task String_that_does_not_match_the_pattern_is_rejected()
    {
        var operation = await Fixture.CreateOperationAsync(o =>
            o.ParameterRules.Single(r => r.Name == "name").Pattern = "[A-Za-z ]+");

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1, name = "Robert'); DROP TABLE x;--" }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_parameter");
        AssertNotForwarded();
    }

    // Brief 12.9: path traversal attempts are rejected.
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../admin")]
    [InlineData("..\\admin")]
    [InlineData("a/../../admin")]
    [InlineData("%2e%2e")]
    [InlineData("%2e%2e%2fadmin")]
    [InlineData("reports/2026")]
    [InlineData("name?admin=true")]
    [InlineData("name#fragment")]
    public async Task Path_traversal_attempts_are_rejected(string value)
    {
        var operation = await Fixture.CreateFileOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = value }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_path_parameter");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Safe_path_value_is_escaped_into_a_single_segment()
    {
        var operation = await Fixture.CreateFileOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = "report 2026.pdf" }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        Assert.Equal("https://upstream.test/api/files/report%202026.pdf", AssertForwardedOnce().Uri.AbsoluteUri);
    }
}

internal static class FileOperationExtensions
{
    /// <summary>An operation <c>GET files/{urlId}</c> whose path parameter is a free string, the case path traversal targets.</summary>
    public static Task<TestOperation> CreateFileOperationAsync(this ProxyTestFixture fixture) =>
        fixture.CreateOperationAsync(o =>
        {
            o.PathTemplate = "files/{urlId}";
            o.ParameterRules =
            [
                new() { Name = "urlId", Location = ParameterLocation.Path, IsRequired = true, DataType = ParameterDataType.String, MaxLength = 100 },
            ];
        });
}
