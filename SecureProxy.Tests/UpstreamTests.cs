using System.Net;
using System.Text;
using SecureProxy.Api.Domain;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class UpstreamTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    // Brief 9.13: upstream timeout is handled correctly.
    [Fact]
    public async Task Upstream_timeout_returns_504_and_cancels_the_upstream_call()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.TimeoutMs = 300);
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Upstream.Responder = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => upstreamCancelled.TrySetResult());
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return RecordingUpstreamHandler.Json(HttpStatusCode.OK, "{}");
        };

        var started = DateTime.UtcNow;
        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.GatewayTimeout, "upstream_timeout");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "The configured 300 ms timeout was not applied.");
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var audit = await Fixture.WaitForAuditAsync(result.CorrelationId);
        Assert.Equal(ExecutionOutcome.UpstreamTimeout, audit.Outcome);
        Assert.True(audit.Forwarded);
    }

    [Fact]
    public async Task Operation_timeout_overrides_the_destination_timeout()
    {
        // Destination allows 30 s, the operation only 300 ms.
        var operation = await Fixture.CreateOperationAsync(o => o.TimeoutMs = 300, d => d.TimeoutMs = 30_000);
        Upstream.Responder = async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return RecordingUpstreamHandler.Json(HttpStatusCode.OK, "{}");
        };

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.GatewayTimeout, "upstream_timeout");
    }

    // Brief 9.14: upstream HTTP error responses are handled correctly.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Upstream_client_error_is_passed_through_and_marked_as_upstream(HttpStatusCode status)
    {
        var operation = await Fixture.CreateOperationAsync();
        Upstream.Responder = (_, _) => Task.FromResult(RecordingUpstreamHandler.Json(status, """{"error":"customer not found"}"""));

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        Assert.Equal(status, result.Response.StatusCode);
        Assert.Equal("upstream", result.Source);
        Assert.Equal("""{"error":"customer not found"}""", result.Body);

        var audit = await Fixture.WaitForAuditAsync(result.CorrelationId);
        Assert.Equal(ExecutionOutcome.UpstreamError, audit.Outcome);
        Assert.Equal((int)status, audit.UpstreamStatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Upstream_server_error_becomes_502_without_leaking_the_upstream_body(HttpStatusCode status)
    {
        var operation = await Fixture.CreateOperationAsync();
        Upstream.Responder = (_, _) => Task.FromResult(RecordingUpstreamHandler.Json(
            status, """{"detail":"SqlException at db01.internal.corp: login failed for sa"}"""));

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.BadGateway, "upstream_error");
        Assert.Equal((int)status, result.Json.GetProperty("upstreamStatus").GetInt32());
        Assert.DoesNotContain("db01", result.Body);
        Assert.DoesNotContain("SqlException", result.Body);
    }

    [Fact]
    public async Task Upstream_connection_failure_returns_502()
    {
        var operation = await Fixture.CreateOperationAsync();
        Upstream.Responder = (_, _) => throw new HttpRequestException("No connection could be made: upstream.internal:443");

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.BadGateway, "upstream_unavailable");
        Assert.DoesNotContain("upstream.internal", result.Body);

        var audit = await Fixture.WaitForAuditAsync(result.CorrelationId);
        Assert.Equal(ExecutionOutcome.UpstreamUnavailable, audit.Outcome);
    }

    [Fact]
    public async Task Upstream_response_larger_than_the_limit_is_not_returned()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.MaxResponseBytes = 100);
        Upstream.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', 1000))),
        });

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.BadGateway, "upstream_response_too_large");
    }

    // Brief 9.15: cancellation is propagated.
    [Fact]
    public async Task Caller_cancellation_is_propagated_to_the_upstream_call()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.TimeoutMs = 30_000);
        var upstreamReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Upstream.Responder = async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => upstreamCancelled.TrySetResult());
            upstreamReceived.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return RecordingUpstreamHandler.Json(HttpStatusCode.OK, "{}");
        };

        var correlationId = "test-" + Guid.NewGuid().ToString("N");
        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, ProxyPath)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(Envelope(operation.Key, new { urlId = 1 })), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Correlation-ID", correlationId);

        var call = Client.SendAsync(request, caller.Token);
        await upstreamReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var audit = await Fixture.WaitForAuditAsync(correlationId);
        Assert.Equal(ExecutionOutcome.CallerCancelled, audit.Outcome);
    }
}
