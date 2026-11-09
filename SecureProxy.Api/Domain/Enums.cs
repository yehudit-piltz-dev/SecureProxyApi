namespace SecureProxy.Api.Domain;

/// <summary>Where a validated parameter is placed in the upstream request.</summary>
public enum ParameterLocation
{
    Path,
    Query,
    Body,
}

/// <summary>The type a parameter value must have before it is forwarded.</summary>
public enum ParameterDataType
{
    String,
    Integer,
    Decimal,
    Boolean,
    Guid,
}

/// <summary>Outcome category recorded in the request log for every execution attempt.</summary>
public enum ExecutionOutcome
{
    /// <summary>The audit row was written before forwarding and the request has not completed yet.</summary>
    InProgress,

    /// <summary>The upstream returned a non-error (1xx-3xx) response.</summary>
    Success,

    /// <summary>The proxy rejected the request before forwarding it.</summary>
    Rejected,

    /// <summary>The upstream returned a 4xx or 5xx response.</summary>
    UpstreamError,

    /// <summary>The upstream did not respond within the configured timeout.</summary>
    UpstreamTimeout,

    /// <summary>The proxy could not connect to the upstream.</summary>
    UpstreamUnavailable,

    /// <summary>The caller aborted the request while it was executing.</summary>
    CallerCancelled,

    /// <summary>An unexpected failure inside the proxy.</summary>
    ProxyError,
}

/// <summary>Kind of change recorded in the configuration history.</summary>
public enum ConfigurationChangeType
{
    Added,
    Modified,
    Deleted,
}
