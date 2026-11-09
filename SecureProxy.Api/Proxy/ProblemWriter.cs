using System.Text.Json;

namespace SecureProxy.Api.Proxy;

/// <summary>Writes proxy-generated errors as RFC 9457 problem details.</summary>
public static class ProblemWriter
{
    /// <summary>Tells the caller whether the response came from the destination or from the proxy itself.</summary>
    public const string ResponseSourceHeader = "X-Proxy-Response-Source";

    public static async Task WriteAsync(HttpContext context, ProxyError error, string? parameter = null, int? upstreamStatusCode = null)
    {
        var problem = new Dictionary<string, object?>
        {
            ["type"] = "https://secureproxy.local/errors/" + error.Code,
            ["title"] = error.Title,
            ["status"] = error.StatusCode,
            ["code"] = error.Code,
            ["correlationId"] = context.TraceIdentifier,
        };
        if (parameter is not null)
        {
            problem["parameter"] = parameter;
        }

        if (upstreamStatusCode is not null)
        {
            problem["upstreamStatus"] = upstreamStatusCode;
        }

        context.Response.StatusCode = error.StatusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers[ResponseSourceHeader] = "proxy";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem), CancellationToken.None);
    }
}
