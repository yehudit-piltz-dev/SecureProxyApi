namespace SecureProxy.Api.Domain;

/// <summary>
/// An explicitly allowed action. The HTTP method, relative path and destination
/// come only from this trusted configuration.
/// </summary>
public sealed class Operation : IConfigurationEntity
{
    public int Id { get; set; }

    /// <summary>Unique key the caller sends as <c>clientId</c>, for example <c>customer.get</c>.</summary>
    public required string Key { get; set; }

    public int DestinationId { get; set; }

    public Destination? Destination { get; set; }

    public required string HttpMethod { get; set; }

    /// <summary>Path relative to the destination base URL, with <c>{name}</c> placeholders for path parameters.</summary>
    public required string PathTemplate { get; set; }

    public bool IsEnabled { get; set; }

    /// <summary>Maximum size of the incoming request body, in bytes.</summary>
    public int MaxRequestBytes { get; set; }

    /// <summary>Maximum size of the upstream response body, in bytes.</summary>
    public int MaxResponseBytes { get; set; }

    /// <summary>Overrides the destination timeout when set.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>When true, the caller must send the parameters as an AES-GCM encrypted payload.</summary>
    public bool RequiresEncryption { get; set; }

    public int? EncryptionProfileId { get; set; }

    public EncryptionProfile? EncryptionProfile { get; set; }

    /// <summary>When false (the default), parameters without a rule are rejected.</summary>
    public bool AllowUnknownParameters { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SQL Server rowversion used for optimistic concurrency on configuration updates.</summary>
    [NotInHistory]
    public byte[] RowVersion { get; set; } = [];

    public List<ParameterRule> ParameterRules { get; set; } = [];

    public List<HeaderRule> HeaderRules { get; set; } = [];
}
