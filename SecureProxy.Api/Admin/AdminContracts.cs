using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Admin;

public sealed record DestinationRequest(string Name, string BaseUrl, bool IsEnabled, int TimeoutMs);

public sealed record DestinationResponse(
    int Id, string Name, string BaseUrl, bool IsEnabled, int TimeoutMs, DateTime CreatedAt, DateTime UpdatedAt, string Version)
{
    public static DestinationResponse From(Destination d) =>
        new(d.Id, d.Name, d.BaseUrl, d.IsEnabled, d.TimeoutMs, d.CreatedAt, d.UpdatedAt, ConfigurationVersion.Format(d.RowVersion));
}

public sealed record ParameterRuleRequest(
    string Name,
    ParameterLocation Location,
    bool IsRequired,
    ParameterDataType DataType,
    int? MaxLength = null,
    decimal? MinValue = null,
    decimal? MaxValue = null,
    string? Pattern = null);

public sealed record HeaderRuleRequest(string HeaderName, bool AllowSensitive = false);

/// <summary>
/// Full operation configuration. A PUT replaces the rules: rules missing from the request are deleted.
/// </summary>
public sealed record OperationRequest(
    string Key,
    int DestinationId,
    string HttpMethod,
    string PathTemplate,
    bool IsEnabled,
    int MaxRequestBytes,
    int MaxResponseBytes,
    int? TimeoutMs,
    bool RequiresEncryption,
    int? EncryptionProfileId,
    bool AllowUnknownParameters,
    IReadOnlyList<ParameterRuleRequest> ParameterRules,
    IReadOnlyList<HeaderRuleRequest> HeaderRules);

public sealed record OperationResponse(
    int Id,
    string Key,
    int DestinationId,
    string HttpMethod,
    string PathTemplate,
    bool IsEnabled,
    int MaxRequestBytes,
    int MaxResponseBytes,
    int? TimeoutMs,
    bool RequiresEncryption,
    int? EncryptionProfileId,
    bool AllowUnknownParameters,
    IReadOnlyList<ParameterRuleRequest> ParameterRules,
    IReadOnlyList<HeaderRuleRequest> HeaderRules,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Version)
{
    public static OperationResponse From(Operation o) => new(
        o.Id, o.Key, o.DestinationId, o.HttpMethod, o.PathTemplate, o.IsEnabled, o.MaxRequestBytes, o.MaxResponseBytes,
        o.TimeoutMs, o.RequiresEncryption, o.EncryptionProfileId, o.AllowUnknownParameters,
        o.ParameterRules.OrderBy(r => r.Id)
            .Select(r => new ParameterRuleRequest(r.Name, r.Location, r.IsRequired, r.DataType, r.MaxLength, r.MinValue, r.MaxValue, r.Pattern))
            .ToList(),
        o.HeaderRules.OrderBy(r => r.Id).Select(r => new HeaderRuleRequest(r.HeaderName, r.AllowSensitive)).ToList(),
        o.CreatedAt, o.UpdatedAt, ConfigurationVersion.Format(o.RowVersion));
}

/// <summary>
/// Converts between the SQL Server rowversion and the opaque version string used in ETag / If-Match.
/// </summary>
public static class ConfigurationVersion
{
    public static string Format(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static string ToETag(byte[] rowVersion) => $"\"{Format(rowVersion)}\"";

    /// <summary>Accepts a quoted ETag (<c>"AAAAAAAAB9E="</c>) or the bare version string.</summary>
    public static bool TryParse(string? ifMatch, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return false;
        }

        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            return false; // Weak validators cannot guarantee the exact version.
        }

        try
        {
            rowVersion = Convert.FromBase64String(value.Trim('"'));
            return rowVersion.Length == 8;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
