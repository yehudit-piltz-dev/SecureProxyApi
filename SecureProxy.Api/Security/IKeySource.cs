namespace SecureProxy.Api.Security;

/// <summary>
/// Supplies encryption keys by reference. The database stores only the reference
/// (<see cref="Domain.EncryptionProfile.KeyReference"/>); the key itself comes from here.
/// A production implementation would read from a secret manager such as Azure Key Vault.
/// </summary>
public interface IKeySource
{
    /// <summary>Returns a new copy of the key, or null when no such key exists. The caller must zero it after use.</summary>
    ValueTask<byte[]?> GetKeyAsync(string keyReference, CancellationToken cancellationToken);
}

/// <summary>
/// Reads base64 keys from configuration section <c>EncryptionKeys</c>, which locally comes from
/// user secrets or environment variables (<c>EncryptionKeys__customer-v1</c>), never from appsettings.json.
/// </summary>
public sealed class ConfigurationKeySource(IConfiguration configuration) : IKeySource
{
    public const string SectionName = "EncryptionKeys";

    public ValueTask<byte[]?> GetKeyAsync(string keyReference, CancellationToken cancellationToken)
    {
        var value = configuration.GetSection(SectionName)[keyReference];
        if (string.IsNullOrEmpty(value))
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        try
        {
            return ValueTask.FromResult<byte[]?>(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }
    }
}
