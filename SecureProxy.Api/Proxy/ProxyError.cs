using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// A failure the proxy reports to the caller. <see cref="Code"/> is stable and machine-readable;
/// <see cref="Title"/> is safe to show publicly and never contains internal details.
/// </summary>
public sealed record ProxyError(string Code, int StatusCode, string Title, ExecutionOutcome Outcome)
{
    /// <summary>Not sent to the caller (the connection is gone); recorded in the audit log only.</summary>
    public const int ClientClosedRequest = 499;

    // Envelope
    public static readonly ProxyError MethodNotAllowed = Rejected("method_not_allowed", 405, "Only POST is supported.");
    public static readonly ProxyError InvalidContentType = Rejected("invalid_content_type", 415, "Content type must be application/json.");
    public static readonly ProxyError RequestTooLarge = Rejected("request_too_large", 413, "The request body is too large.");
    public static readonly ProxyError InvalidEnvelope = Rejected("invalid_envelope", 400, "The request body is not a valid request envelope.");
    public static readonly ProxyError UnknownEnvelopeField = Rejected("unknown_envelope_field", 400, "The request envelope contains a field that is not allowed.");
    public static readonly ProxyError InvalidClientId = Rejected("invalid_client_id", 400, "clientId is missing or invalid.");

    // Operation and destination
    public static readonly ProxyError UnknownOperation = Rejected("unknown_operation", 404, "The requested operation does not exist.");
    public static readonly ProxyError OperationDisabled = Rejected("operation_disabled", 403, "The requested operation is disabled.");
    public static readonly ProxyError DestinationUnavailable = Rejected("destination_unavailable", 403, "The destination of the requested operation is not available.");
    public static readonly ProxyError OperationMisconfigured = Rejected("operation_misconfigured", 500, "The requested operation cannot be executed.");

    // Encryption
    public static readonly ProxyError EncryptionRequired = Rejected("encryption_required", 400, "This operation requires an encrypted payload.");
    public static readonly ProxyError EncryptionNotAllowed = Rejected("encryption_not_allowed", 400, "This operation does not accept an encrypted payload.");
    public static readonly ProxyError InvalidEncryptedPayload = Rejected("invalid_encrypted_payload", 400, "The encrypted payload is invalid.");
    public static readonly ProxyError EncryptionUnavailable = Rejected("encryption_unavailable", 500, "The operation cannot be executed.");

    // Parameters
    public static readonly ProxyError InvalidParameters = Rejected("invalid_parameters", 400, "Parameters must be a JSON object.");
    public static readonly ProxyError MissingUrlId = Rejected("missing_url_id", 400, "Parameters must contain urlId.");
    public static readonly ProxyError MissingParameter = Rejected("missing_parameter", 400, "A required parameter is missing.");
    public static readonly ProxyError UnknownParameter = Rejected("unknown_parameter", 400, "A parameter is not allowed for this operation.");
    public static readonly ProxyError InvalidParameter = Rejected("invalid_parameter", 400, "A parameter value is invalid.");
    public static readonly ProxyError InvalidPathParameter = Rejected("invalid_path_parameter", 400, "A path parameter contains characters that are not allowed.");

    // Proxy infrastructure
    public static readonly ProxyError ConfigurationUnavailable = Rejected("configuration_unavailable", 503, "The proxy configuration is temporarily unavailable.");
    public static readonly ProxyError AuditUnavailable = Rejected("audit_unavailable", 503, "The request cannot be audited and was not forwarded.");
    public static readonly ProxyError RateLimited = Rejected("rate_limited", 429, "Too many requests.");
    public static readonly ProxyError DestinationBusy = Rejected("destination_busy", 503, "The destination is handling too many requests. Retry later.");
    public static readonly ProxyError InternalError = new("internal_error", 500, "An unexpected error occurred.", ExecutionOutcome.ProxyError);
    public static readonly ProxyError CallerCancelled = new("caller_cancelled", ClientClosedRequest, "The caller cancelled the request.", ExecutionOutcome.CallerCancelled);

    // Upstream
    public static readonly ProxyError UpstreamTimeout = new("upstream_timeout", 504, "The destination did not respond in time.", ExecutionOutcome.UpstreamTimeout);
    public static readonly ProxyError UpstreamUnavailable = new("upstream_unavailable", 502, "The destination could not be reached.", ExecutionOutcome.UpstreamUnavailable);
    public static readonly ProxyError UpstreamServerError = new("upstream_error", 502, "The destination returned a server error.", ExecutionOutcome.UpstreamError);
    public static readonly ProxyError UpstreamResponseTooLarge = new("upstream_response_too_large", 502, "The destination response is too large.", ExecutionOutcome.ProxyError);

    private static ProxyError Rejected(string code, int status, string title) => new(code, status, title, ExecutionOutcome.Rejected);
}

/// <summary>Stops processing and reports <see cref="Error"/> to the caller.</summary>
public sealed class ProxyException(ProxyError error, string? parameter = null, int? upstreamStatusCode = null)
    : Exception(error.Code)
{
    public ProxyError Error { get; } = error;

    /// <summary>Name of the offending parameter. Only configured names are reported, never caller input.</summary>
    public string? Parameter { get; } = parameter;

    public int? UpstreamStatusCode { get; } = upstreamStatusCode;
}
