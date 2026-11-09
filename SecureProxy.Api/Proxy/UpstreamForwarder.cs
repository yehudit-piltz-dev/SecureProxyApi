using System.Buffers;
using SecureProxy.Api.Configuration;
using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Sends the validated request to the destination and streams the response back to the caller.
/// No retries: a request that may have reached the destination is never sent twice (see DESIGN-NOTES.md).
/// </summary>
public sealed class UpstreamForwarder(IHttpClientFactory httpClientFactory, ILogger<UpstreamForwarder> logger)
{
    public const string HttpClientName = "upstream";

    public async Task ForwardAsync(HttpContext context, OperationSnapshot operation, HttpRequestMessage request, RequestLog audit)
    {
        var callerToken = context.RequestAborted;
        using var timeout = new CancellationTokenSource(operation.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            audit.Forwarded = true;

            // ResponseHeadersRead: the body is streamed, not buffered in memory.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            audit.UpstreamStatusCode = (int)response.StatusCode;

            if (audit.UpstreamStatusCode >= 500)
            {
                // A 5xx body can expose upstream internals, so it is replaced by a proxy error that keeps the status.
                throw new ProxyException(ProxyError.UpstreamServerError, upstreamStatusCode: audit.UpstreamStatusCode);
            }

            if (response.Content.Headers.ContentLength > operation.MaxResponseBytes)
            {
                throw new ProxyException(ProxyError.UpstreamResponseTooLarge);
            }

            await CopyResponseAsync(context, operation, response, audit, linked.Token);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw; // The caller went away; the executor records it.
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Operation {OperationKey} timed out after {TimeoutMs} ms", operation.Key, operation.Timeout.TotalMilliseconds);
            throw new ProxyException(ProxyError.UpstreamTimeout);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Operation {OperationKey} could not reach its destination", operation.Key);
            throw new ProxyException(ProxyError.UpstreamUnavailable);
        }
    }

    private static async Task CopyResponseAsync(
        HttpContext context, OperationSnapshot operation, HttpResponseMessage response, RequestLog audit, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        context.Response.StatusCode = status;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (HeaderPolicy.IsAllowedResponseHeader(header.Key))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        context.Response.Headers[ProblemWriter.ResponseSourceHeader] = "upstream";
        audit.ProxyStatusCode = status;
        audit.Outcome = status >= 400 ? ExecutionOutcome.UpstreamError : ExecutionOutcome.Success;
        audit.Reason = status >= 400 ? "upstream_status" : null;

        await using var upstreamBody = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await upstreamBody.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > operation.MaxResponseBytes)
                {
                    // Without Content-Length the limit can only be detected mid-stream. The status line is
                    // already sent, so the connection is aborted to make the truncation visible to the caller.
                    throw new ProxyException(ProxyError.UpstreamResponseTooLarge);
                }

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
