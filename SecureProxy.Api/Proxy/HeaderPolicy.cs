namespace SecureProxy.Api.Proxy;

/// <summary>Fixed header rules that configuration cannot override.</summary>
public static class HeaderPolicy
{
    public const string CorrelationIdHeader = "X-Correlation-ID";

    /// <summary>
    /// Never forwarded, even when configured: hop-by-hop headers (RFC 9110 section 7.6.1),
    /// headers the proxy sets itself, and headers that would let a caller spoof routing or identity.
    /// </summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Proxy-Authenticate", "Proxy-Authorization",
        "Host", "Content-Length", "Content-Type", "Expect",
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP",
        CorrelationIdHeader,
    };

    /// <summary>Forwarded only when the header rule sets <c>AllowSensitive</c>.</summary>
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "X-Api-Key", "X-Auth-Token",
    };

    /// <summary>Upstream response headers passed back to the caller. Everything else, including Location and Set-Cookie, is dropped.</summary>
    private static readonly HashSet<string> AllowedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Language", "Cache-Control", "ETag", "Last-Modified", "Expires", "Retry-After",
    };

    public static bool IsForbidden(string name) => Forbidden.Contains(name);

    public static bool IsSensitive(string name) => Sensitive.Contains(name);

    public static bool IsAllowedResponseHeader(string name) => AllowedResponseHeaders.Contains(name);
}
