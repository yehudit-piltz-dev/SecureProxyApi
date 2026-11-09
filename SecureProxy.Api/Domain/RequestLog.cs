namespace SecureProxy.Api.Domain;

/// <summary>
/// Audit record of one execution attempt. Holds metadata only: never payloads,
/// headers, credentials or keys.
/// </summary>
/// <remarks>
/// Operation and destination are stored as plain values, without foreign keys,
/// so the audit trail survives when configuration is changed or deleted.
/// </remarks>
public sealed class RequestLog
{
    public long Id { get; set; }

    public required string CorrelationId { get; set; }

    /// <summary>The key the caller sent, if any. Stored even when no such operation exists.</summary>
    public string? OperationKey { get; set; }

    public int? OperationId { get; set; }

    public int? DestinationId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public long? DurationMs { get; set; }

    /// <summary>Whether the request was sent to the upstream.</summary>
    public bool Forwarded { get; set; }

    public int? UpstreamStatusCode { get; set; }

    /// <summary>The status code the proxy returned to the caller.</summary>
    public int? ProxyStatusCode { get; set; }

    public ExecutionOutcome Outcome { get; set; }

    /// <summary>Machine-readable rejection or failure code, for example <c>operation_disabled</c>.</summary>
    public string? Reason { get; set; }
}
