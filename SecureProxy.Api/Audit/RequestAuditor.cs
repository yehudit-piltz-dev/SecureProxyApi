using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Data;
using SecureProxy.Api.Domain;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Audit;

/// <summary>
/// Writes the request log according to the audit policy:
/// <list type="bullet">
/// <item>Before forwarding, the record is inserted with outcome <c>InProgress</c>. This write is mandatory:
/// if it fails, the request is not forwarded (503 <c>audit_unavailable</c>). Every request that reached a
/// destination therefore has an audit record.</item>
/// <item>The final outcome is written best-effort. Rejected requests, which never reached a destination,
/// are also recorded best-effort. When the database is unavailable, the record goes to the structured log instead.</item>
/// </list>
/// </summary>
public sealed class RequestAuditor(ProxyDbContext db, ILogger<RequestAuditor> logger)
{
    /// <summary>Mandatory pre-forwarding write. Throws <see cref="ProxyException"/> when it fails.</summary>
    public async Task RecordForwardingAsync(RequestLog record)
    {
        try
        {
            db.RequestLogs.Add(record);

            // Not the caller's token: an audit write is not abandoned halfway because the caller disconnected.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException or InvalidOperationException or TimeoutException)
        {
            db.Entry(record).State = EntityState.Detached;
            logger.LogError(ex, "Audit record could not be written; request {CorrelationId} was not forwarded", record.CorrelationId);
            throw new ProxyException(ProxyError.AuditUnavailable);
        }
    }

    /// <summary>Best-effort write of the final outcome.</summary>
    public async Task CompleteAsync(RequestLog record)
    {
        try
        {
            if (db.Entry(record).State == EntityState.Detached)
            {
                db.RequestLogs.Add(record);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException or InvalidOperationException or TimeoutException)
        {
            db.Entry(record).State = EntityState.Detached;
            logger.LogError(
                ex,
                "Audit record could not be written: {CorrelationId} {OperationKey} {Outcome} {Reason} {ProxyStatusCode} {UpstreamStatusCode} {Forwarded} {StartedAt} {DurationMs}",
                record.CorrelationId, record.OperationKey, record.Outcome, record.Reason, record.ProxyStatusCode,
                record.UpstreamStatusCode, record.Forwarded, record.StartedAt, record.DurationMs);
        }
    }
}
