using System.Text.RegularExpressions;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Infrastructure;

/// <summary>
/// Gives every request a correlation ID: the caller's <c>X-Correlation-ID</c> when it is well-formed,
/// otherwise a new one. It becomes <see cref="HttpContext.TraceIdentifier"/>, is returned in the response,
/// sent to the destination, written to the audit log and added to every log entry's scope.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderPolicy.CorrelationIdHeader].ToString();
        var correlationId = AllowedFormat().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderPolicy.CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    /// <summary>Restrictive, so the value is safe to echo in headers and logs.</summary>
    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex AllowedFormat();
}
