using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Infrastructure;

/// <summary>Fixed-window rate limits per client IP address for the proxy and admin endpoints.</summary>
public static class RateLimitPolicies
{
    public const string Proxy = "proxy";
    public const string Admin = "admin";

    public static IServiceCollection AddProxyRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                // Rate-limited requests are logged but not audited: writing a database row for each
                // request of a flood would turn the limiter into a way to overload the database.
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RateLimitPolicies));
                logger.LogWarning("Rate limit exceeded for {Path}", context.HttpContext.Request.Path);

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                await ProblemWriter.WriteAsync(context.HttpContext, ProxyError.RateLimited);
            };

            options.AddPolicy(Proxy, http => FixedWindowPerClient(http, o => (o.RateLimitPermits, o.RateLimitWindowSeconds)));
            options.AddPolicy(Admin, http => FixedWindowPerClient(http, o => (o.AdminRateLimitPermits, o.AdminRateLimitWindowSeconds)));
        });

        return services;
    }

    /// <remarks>
    /// Partitioned by the connection's remote address. Behind a load balancer this is the balancer's
    /// address unless forwarded headers from trusted proxies are configured (see DESIGN-NOTES.md).
    /// </remarks>
    private static RateLimitPartition<string> FixedWindowPerClient(HttpContext http, Func<ProxyOptions, (int Permits, int WindowSeconds)> select)
    {
        var (permits, windowSeconds) = select(http.RequestServices.GetRequiredService<IOptions<ProxyOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0,
            });
    }
}
