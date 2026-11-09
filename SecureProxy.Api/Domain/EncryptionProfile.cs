namespace SecureProxy.Api.Domain;

/// <summary>
/// Metadata about an encryption key. The key itself is never stored in the database:
/// <see cref="KeyReference"/> names the secret in the key source.
/// </summary>
public sealed class EncryptionProfile : IConfigurationEntity
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Only <c>AES-256-GCM</c> is supported.</summary>
    public required string Algorithm { get; set; }

    /// <summary>Name of the secret in the key source, for example <c>customer-v1</c>.</summary>
    public required string KeyReference { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
