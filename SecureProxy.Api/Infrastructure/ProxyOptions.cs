namespace SecureProxy.Api.Infrastructure;

/// <summary>
/// Per-process protection limits, bound from the <c>Proxy</c> configuration section.
/// They are read at startup; runtime-tunable policy lives in the OperationalSettings table instead.
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>Proxy requests allowed per client IP address in each window.</summary>
    public int RateLimitPermits { get; set; } = 100;

    public int RateLimitWindowSeconds { get; set; } = 10;

    /// <summary>Admin requests allowed per client IP address in each window. Low, to slow down key guessing.</summary>
    public int AdminRateLimitPermits { get; set; } = 30;

    public int AdminRateLimitWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Requests executing against one destination at the same time. Extra requests are rejected with
    /// 503 instead of queuing, so a slow destination cannot use up the proxy's threads and connections.
    /// </summary>
    public int MaxConcurrentRequestsPerDestination { get; set; } = 50;
}
