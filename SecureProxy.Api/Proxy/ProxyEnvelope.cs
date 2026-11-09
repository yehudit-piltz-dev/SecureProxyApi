using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// The parsed request body of <c>POST /api/proxy/execute</c>:
/// <code>{ "clientId": "customer.get", "json": { "urlId": 42 } }</code>
/// or, for operations that require encryption,
/// <code>{ "clientId": "customer.update", "encryptedJson": { "nonce": "...", "ciphertext": "...", "tag": "..." } }</code>
/// No other field is accepted, so a caller cannot send a URL, method, path or header override.
/// </summary>
public sealed class ProxyEnvelope : IDisposable
{
    private readonly JsonDocument document;

    private ProxyEnvelope(JsonDocument document, string clientId, JsonElement? json, JsonElement? encryptedJson, int bodyLength)
    {
        this.document = document;
        ClientId = clientId;
        Json = json;
        EncryptedJson = encryptedJson;
        BodyLength = bodyLength;
    }

    public string ClientId { get; }

    /// <summary>Plain parameters. Valid only while the envelope is not disposed.</summary>
    public JsonElement? Json { get; }

    /// <summary>Encrypted parameters. Valid only while the envelope is not disposed.</summary>
    public JsonElement? EncryptedJson { get; }

    public int BodyLength { get; }

    public void Dispose() => document.Dispose();

    /// <summary>Reads and validates the envelope. Throws <see cref="ProxyException"/> when it is not acceptable.</summary>
    public static async Task<ProxyEnvelope> ReadAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(request.ContentType))
        {
            throw new ProxyException(ProxyError.InvalidContentType);
        }

        var body = await ReadBoundedAsync(request, maxBytes, cancellationToken);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw new ProxyException(ProxyError.InvalidEnvelope);
        }

        try
        {
            return Parse(document, body.Length);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static ProxyEnvelope Parse(JsonDocument document, int bodyLength)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProxyException(ProxyError.InvalidEnvelope);
        }

        string? clientId = null;
        JsonElement? json = null;
        JsonElement? encrypted = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in root.EnumerateObject())
        {
            // Case-insensitive, so "clientId" and "ClientId" cannot both be sent and read differently by different parsers.
            if (!seen.Add(property.Name))
            {
                throw new ProxyException(ProxyError.InvalidEnvelope);
            }

            switch (property.Name)
            {
                case "clientId" when property.Value.ValueKind == JsonValueKind.String:
                    clientId = property.Value.GetString();
                    break;
                case "clientId":
                    throw new ProxyException(ProxyError.InvalidClientId);
                case "json" when property.Value.ValueKind == JsonValueKind.Object:
                    json = property.Value;
                    break;
                case "json":
                    throw new ProxyException(ProxyError.InvalidParameters);
                case "encryptedJson" when property.Value.ValueKind == JsonValueKind.Object:
                    encrypted = property.Value;
                    break;
                case "encryptedJson":
                    throw new ProxyException(ProxyError.InvalidEncryptedPayload);
                default:
                    // Covers "url", "method", "path", "headers", "destination" and any other override attempt.
                    throw new ProxyException(ProxyError.UnknownEnvelopeField);
            }
        }

        if (clientId is null || !OperationKeyFormat.IsValid(clientId))
        {
            throw new ProxyException(ProxyError.InvalidClientId);
        }

        if (json is null == encrypted is null)
        {
            // Exactly one of "json" and "encryptedJson" must be present.
            throw new ProxyException(ProxyError.InvalidEnvelope);
        }

        return new ProxyEnvelope(document, clientId, json, encrypted, bodyLength);
    }

    private static bool IsJsonContentType(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && (mediaType.Charset.Length == 0 || mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes, failing as soon as the limit is exceeded.</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            throw new ProxyException(ProxyError.RequestTooLarge);
        }

        // Also make the server stop reading past the limit, for chunked requests without Content-Length.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxBytes + 1L;
        }

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new ProxyException(ProxyError.RequestTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new ProxyException(ProxyError.RequestTooLarge);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}

/// <summary>Format of operation keys such as <c>customer.get</c>.</summary>
public static partial class OperationKeyFormat
{
    public const int MaxLength = 100;

    public static bool IsValid(string key) => key.Length <= MaxLength && Regex().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$")]
    private static partial Regex Regex();
}
