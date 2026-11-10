using SecureProxy.Api.Configuration;
using SecureProxy.Api.Domain;

namespace SecureProxy.Tests.Unit;

/// <summary>Configuration checks the proxy runs every time it loads an operation (no database needed).</summary>
public sealed class OperationSnapshotFactoryTests
{
    [Fact]
    public void Valid_configuration_has_no_error()
    {
        var snapshot = OperationSnapshotFactory.Create(ValidOperation());

        Assert.Null(snapshot.ConfigurationError);
        Assert.Equal(new Uri("https://upstream.test/api/"), snapshot.Destination!.BaseUri);
    }

    [Fact]
    public void Base_url_gets_a_trailing_slash_so_paths_are_appended()
    {
        var operation = ValidOperation();
        operation.Destination!.BaseUrl = "https://upstream.test/api";

        Assert.Equal("https://upstream.test/api/", OperationSnapshotFactory.Create(operation).Destination!.BaseUri!.AbsoluteUri);
    }

    [Fact]
    public void Operation_timeout_overrides_destination_timeout()
    {
        var operation = ValidOperation();
        operation.TimeoutMs = 750;

        Assert.Equal(TimeSpan.FromMilliseconds(750), OperationSnapshotFactory.Create(operation).Timeout);
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    [InlineData("OPTIONS")]
    public void Disallowed_method_is_a_configuration_error(string method)
    {
        var operation = ValidOperation();
        operation.HttpMethod = method;

        Assert.NotNull(OperationSnapshotFactory.Create(operation).ConfigurationError);
    }

    [Theory]
    [InlineData("ftp://upstream.test/")]
    [InlineData("https://user:pass@upstream.test/")]
    [InlineData("https://upstream.test/?token=1")]
    [InlineData("/relative/path")]
    public void Unsafe_base_url_is_a_configuration_error(string baseUrl)
    {
        var operation = ValidOperation();
        operation.Destination!.BaseUrl = baseUrl;

        Assert.NotNull(OperationSnapshotFactory.Create(operation).ConfigurationError);
    }

    [Fact]
    public void Body_parameter_on_get_is_a_configuration_error()
    {
        var operation = ValidOperation();
        operation.ParameterRules.Add(new ParameterRule { Name = "note", Location = ParameterLocation.Body, DataType = ParameterDataType.String });

        Assert.NotNull(OperationSnapshotFactory.Create(operation).ConfigurationError);
    }

    [Fact]
    public void Missing_url_id_rule_is_a_configuration_error()
    {
        var operation = ValidOperation();
        operation.PathTemplate = "customers";
        operation.ParameterRules.RemoveAll(r => r.Name == "urlId");

        Assert.Contains("urlId", OperationSnapshotFactory.Create(operation).ConfigurationError);
    }

    [Fact]
    public void Encryption_without_a_profile_is_a_configuration_error()
    {
        var operation = ValidOperation();
        operation.RequiresEncryption = true;

        Assert.NotNull(OperationSnapshotFactory.Create(operation).ConfigurationError);
    }

    [Fact]
    public void Invalid_regular_expression_is_a_configuration_error()
    {
        var operation = ValidOperation();
        operation.ParameterRules.Add(new ParameterRule { Name = "name", Location = ParameterLocation.Query, DataType = ParameterDataType.String, Pattern = "([" });

        Assert.NotNull(OperationSnapshotFactory.Create(operation).ConfigurationError);
    }

    private static Operation ValidOperation() => new()
    {
        Key = "customer.get",
        HttpMethod = "GET",
        PathTemplate = "customers/{urlId}",
        IsEnabled = true,
        MaxRequestBytes = 1024,
        MaxResponseBytes = 1024,
        Destination = new Destination { Name = "customers", BaseUrl = "https://upstream.test/api/", IsEnabled = true, TimeoutMs = 5000 },
        ParameterRules =
        [
            new ParameterRule { Name = "urlId", Location = ParameterLocation.Path, IsRequired = true, DataType = ParameterDataType.Integer },
        ],
    };
}
