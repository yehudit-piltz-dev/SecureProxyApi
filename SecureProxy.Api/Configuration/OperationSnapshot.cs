using System.Text.RegularExpressions;
using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Configuration;

/// <summary>
/// Immutable, validated copy of an operation's configuration, as cached and used by the proxy.
/// A request keeps the snapshot it started with, even if the configuration changes meanwhile.
/// </summary>
public sealed record OperationSnapshot
{
    public required int Id { get; init; }

    public required string Key { get; init; }

    public required bool IsEnabled { get; init; }

    public required HttpMethod HttpMethod { get; init; }

    public required string PathTemplate { get; init; }

    public required int MaxRequestBytes { get; init; }

    public required int MaxResponseBytes { get; init; }

    public required TimeSpan Timeout { get; init; }

    public required bool RequiresEncryption { get; init; }

    public EncryptionProfileSnapshot? EncryptionProfile { get; init; }

    public required bool AllowUnknownParameters { get; init; }

    public DestinationSnapshot? Destination { get; init; }

    /// <summary>Rules by parameter name (case-sensitive).</summary>
    public required IReadOnlyDictionary<string, ParameterRuleSnapshot> Parameters { get; init; }

    public required IReadOnlyList<HeaderRuleSnapshot> Headers { get; init; }

    /// <summary>
    /// Set when the stored configuration is unsafe or inconsistent. Such an operation is never forwarded.
    /// Internal only: never returned to callers.
    /// </summary>
    public string? ConfigurationError { get; init; }
}

public sealed record DestinationSnapshot(int Id, string Name, bool IsEnabled, Uri? BaseUri, TimeSpan Timeout);

public sealed record EncryptionProfileSnapshot(int Id, string Algorithm, string KeyReference, bool IsEnabled);

public sealed record ParameterRuleSnapshot(
    string Name,
    ParameterLocation Location,
    bool IsRequired,
    ParameterDataType DataType,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    Regex? Pattern);

public sealed record HeaderRuleSnapshot(string HeaderName, bool AllowSensitive);
