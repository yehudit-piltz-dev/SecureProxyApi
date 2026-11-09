using System.Security.Cryptography;
using System.Text;

namespace SecureProxy.Api.Security;

/// <summary>
/// AES-256-GCM format of <c>encryptedJson</c>: base64 <c>nonce</c> (12 bytes), <c>ciphertext</c> and <c>tag</c> (16 bytes).
/// The plaintext is the UTF-8 JSON parameters object.
/// </summary>
public static class AesGcmPayload
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>
    /// Associated data binds the ciphertext to one operation: a payload encrypted for
    /// <c>customer.update</c> fails authentication if replayed against another operation.
    /// </summary>
    public static byte[] AssociatedData(string operationKey) => Encoding.UTF8.GetBytes("SecureProxy/v1/" + operationKey);

    /// <summary>Returns the plaintext, or null when the tag does not authenticate the data.</summary>
    public static byte[]? Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] associatedData)
    {
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        catch (CryptographicException)
        {
            // AuthenticationTagMismatchException derives from CryptographicException.
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
    }

    /// <summary>Used by tests and by the local encryption tool to produce valid payloads.</summary>
    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return (nonce, ciphertext, tag);
    }
}
