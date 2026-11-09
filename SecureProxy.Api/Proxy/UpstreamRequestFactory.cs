using System.Net.Http.Headers;
using System.Text;
using SecureProxy.Api.Configuration;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Builds the upstream request from trusted configuration and validated parameters only.
/// Nothing the caller sent is used except validated parameter values and allowlisted headers.
/// </summary>
public static class UpstreamRequestFactory
{
    public static HttpRequestMessage Create(
        OperationSnapshot operation,
        ValidatedParameters parameters,
        IHeaderDictionary incomingHeaders,
        string correlationId)
    {
        var baseUri = operation.Destination?.BaseUri ?? throw new ProxyException(ProxyError.OperationMisconfigured);
        var uri = BuildUri(baseUri, operation.PathTemplate, parameters);

        var request = new HttpRequestMessage(operation.HttpMethod, uri);
        if (parameters.JsonBody is { } body)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        CopyAllowedHeaders(operation, incomingHeaders, request);
        request.Headers.TryAddWithoutValidation(HeaderPolicy.CorrelationIdHeader, correlationId);
        return request;
    }

    private static Uri BuildUri(Uri baseUri, string pathTemplate, ValidatedParameters parameters)
    {
        var path = new StringBuilder();
        foreach (var segment in pathTemplate.Split('/'))
        {
            if (path.Length > 0)
            {
                path.Append('/');
            }

            // The snapshot guarantees each placeholder is a whole segment with a matching path parameter.
            path.Append(segment.StartsWith('{')
                ? Uri.EscapeDataString(parameters.Path[segment[1..^1]])
                : segment);
        }

        if (parameters.Query.Count > 0)
        {
            path.Append('?');
            path.AppendJoin('&', parameters.Query.Select(q => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value)));
        }

        var uri = new Uri(baseUri, path.ToString());

        // Defence in depth: whatever happened above, the result must stay under the configured base URL.
        if (uri.Scheme != baseUri.Scheme
            || !string.Equals(uri.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            || uri.Port != baseUri.Port
            || !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new ProxyException(ProxyError.InvalidPathParameter);
        }

        return uri;
    }

    private static void CopyAllowedHeaders(OperationSnapshot operation, IHeaderDictionary incoming, HttpRequestMessage request)
    {
        // Headers named in the Connection header are hop-by-hop for this request (RFC 9110 section 7.6.1).
        var connectionScoped = incoming.Connection
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in operation.Headers)
        {
            if (HeaderPolicy.IsForbidden(rule.HeaderName)
                || connectionScoped.Contains(rule.HeaderName)
                || (HeaderPolicy.IsSensitive(rule.HeaderName) && !rule.AllowSensitive))
            {
                continue;
            }

            if (incoming.TryGetValue(rule.HeaderName, out var values))
            {
                request.Headers.TryAddWithoutValidation(rule.HeaderName, (IEnumerable<string?>)values);
            }
        }
    }
}
