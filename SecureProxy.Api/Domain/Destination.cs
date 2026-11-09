namespace SecureProxy.Api.Domain;

/// <summary>A configured upstream API. Callers never supply or override any of these values.</summary>
public sealed class Destination : IConfigurationEntity
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Absolute http(s) URL. Operations append their relative path to it.</summary>
    public required string BaseUrl { get; set; }

    public bool IsEnabled { get; set; }

    /// <summary>Default upstream timeout for the destination's operations.</summary>
    public int TimeoutMs { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SQL Server rowversion used for optimistic concurrency on configuration updates.</summary>
    [NotInHistory]
    public byte[] RowVersion { get; set; } = [];

    public List<Operation> Operations { get; set; } = [];
}
