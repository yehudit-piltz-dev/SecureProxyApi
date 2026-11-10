using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace SecureProxy.Api.Infrastructure;

/// <summary>Bounds the number of requests executing against each destination at the same time.</summary>
public sealed class DestinationConcurrencyLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<int> limiter;

    public DestinationConcurrencyLimiter(IOptions<ProxyOptions> options)
    {
        var permits = options.Value.MaxConcurrentRequestsPerDestination;
        limiter = PartitionedRateLimiter.Create<int, int>(destinationId =>
            RateLimitPartition.GetConcurrencyLimiter(destinationId, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = permits,
                QueueLimit = 0,
            }));
    }

    /// <summary>Returns a lease that must be disposed when the request completes. Check <see cref="RateLimitLease.IsAcquired"/>.</summary>
    public RateLimitLease TryAcquire(int destinationId) => limiter.AttemptAcquire(destinationId);

    public void Dispose() => limiter.Dispose();
}
