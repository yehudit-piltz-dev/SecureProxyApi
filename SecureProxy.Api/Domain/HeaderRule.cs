namespace SecureProxy.Api.Domain;

/// <summary>
/// Allowlist entry: an incoming header that may be forwarded for an operation.
/// Headers without a rule are never forwarded. Hop-by-hop headers are never forwarded,
/// even with a rule.
/// </summary>
public sealed class HeaderRule : IConfigurationEntity
{
    public int Id { get; set; }

    public int OperationId { get; set; }

    public required string HeaderName { get; set; }

    /// <summary>
    /// Must be true to forward a sensitive header such as Authorization or Cookie.
    /// This makes forwarding credentials an explicit, reviewable decision.
    /// </summary>
    public bool AllowSensitive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
