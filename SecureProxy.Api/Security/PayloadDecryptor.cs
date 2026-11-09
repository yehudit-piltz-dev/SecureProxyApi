using System.Security.Cryptography;
using System.Text.Json;
using SecureProxy.Api.Configuration;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Security;

/// <summary>Decrypts <c>encryptedJson</c> with the key of the operation's encryption profile.</summary>
public sealed class PayloadDecryptor(IKeySource keySource, ILogger<PayloadDecryptor> logger)
{
    /// <summary>
    /// Returns the plaintext bytes. The caller owns them and must zero them after use.
    /// Throws <see cref="ProxyException"/> for malformed or unauthenticated payloads.
    /// </summary>
    public async Task<byte[]> DecryptAsync(OperationSnapshot operation, JsonElement encrypted, CancellationToken cancellationToken)
    {
        var (nonce, ciphertext, tag) = ReadEnvelope(encrypted);
        if (ciphertext.Length > operation.MaxRequestBytes)
        {
            throw new ProxyException(ProxyError.RequestTooLarge);
        }

        var profile = operation.EncryptionProfile;
        if (profile is not { IsEnabled: true })
        {
            logger.LogWarning("Encryption profile of operation {OperationKey} is missing or disabled", operation.Key);
            throw new ProxyException(ProxyError.EncryptionUnavailable);
        }

        var key = await keySource.GetKeyAsync(profile.KeyReference, cancellationToken);
        if (key is null || key.Length != AesGcmPayload.KeySize)
        {
            // Logs the reference name only, never key material.
            logger.LogError("Key {KeyReference} of encryption profile {ProfileId} is missing or not a 256-bit key", profile.KeyReference, profile.Id);
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            throw new ProxyException(ProxyError.EncryptionUnavailable);
        }

        try
        {
            return AesGcmPayload.Decrypt(key, nonce, ciphertext, tag, AesGcmPayload.AssociatedData(operation.Key))
                   ?? throw new ProxyException(ProxyError.InvalidEncryptedPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) ReadEnvelope(JsonElement encrypted)
    {
        byte[]? nonce = null, ciphertext = null, tag = null;
        foreach (var property in encrypted.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? Base64(property.Value.GetString()!) : null;
            switch (property.Name)
            {
                case "nonce" when nonce is null:
                    nonce = value;
                    break;
                case "ciphertext" when ciphertext is null:
                    ciphertext = value;
                    break;
                case "tag" when tag is null:
                    tag = value;
                    break;
                default:
                    // Unknown or duplicate field.
                    throw new ProxyException(ProxyError.InvalidEncryptedPayload);
            }

            if (value is null)
            {
                throw new ProxyException(ProxyError.InvalidEncryptedPayload);
            }
        }

        if (nonce is not { Length: AesGcmPayload.NonceSize } || tag is not { Length: AesGcmPayload.TagSize } || ciphertext is null)
        {
            throw new ProxyException(ProxyError.InvalidEncryptedPayload);
        }

        return (nonce, ciphertext, tag);
    }

    private static byte[]? Base64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
