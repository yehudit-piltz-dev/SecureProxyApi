using System.Text.Json;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Shape of the request envelope, for the OpenAPI document only. The proxy itself parses the body
/// with <see cref="ProxyEnvelope"/>, which rejects any field not listed here.
/// </summary>
/// <param name="ClientId">Operation key, for example <c>customer.get</c>.</param>
/// <param name="Json">Parameters object; must contain <c>urlId</c>. Used when the operation does not require encryption.</param>
/// <param name="EncryptedJson">AES-256-GCM encrypted parameters. Used when the operation requires encryption.</param>
public sealed record ProxyEnvelopeDocument(string ClientId, JsonElement? Json, EncryptedPayloadDocument? EncryptedJson);

/// <param name="Nonce">Base64, 12 bytes.</param>
/// <param name="Ciphertext">Base64 of the encrypted UTF-8 JSON parameters object.</param>
/// <param name="Tag">Base64, 16 bytes.</param>
public sealed record EncryptedPayloadDocument(string Nonce, string Ciphertext, string Tag);
