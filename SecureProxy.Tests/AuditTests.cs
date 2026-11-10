using System.Net;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Domain;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

/// <summary>
/// Audit policy (see DESIGN-NOTES.md): every execution attempt that reaches the endpoint gets one
/// request log record; a forwarded request is recorded before it is sent.
/// </summary>
public sealed class AuditTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    // Brief 12.17: audit records are created for successful and rejected requests.
    [Fact]
    public async Task Successful_request_is_audited()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 42, name = "Dana Levi" }));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        var audit = await Fixture.WaitForAuditAsync(result.CorrelationId);
        Assert.Equal(ExecutionOutcome.Success, audit.Outcome);
        Assert.Equal(operation.Key, audit.OperationKey);
        Assert.Equal(operation.Id, audit.OperationId);
        Assert.Equal(operation.DestinationId, audit.DestinationId);
        Assert.True(audit.Forwarded);
        Assert.Equal(200, audit.UpstreamStatusCode);
        Assert.Equal(200, audit.ProxyStatusCode);
        Assert.NotNull(audit.CompletedAt);
        Assert.NotNull(audit.DurationMs);
    }

    [Fact]
    public async Task Rejected_request_is_audited_as_not_forwarded()
    {
        var operation = await Fixture.CreateOperationAsync(o => o.IsEnabled = false);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));

        var audit = await Fixture.WaitForAuditAsync(result.CorrelationId);
        Assert.Equal(ExecutionOutcome.Rejected, audit.Outcome);
        Assert.Equal("operation_disabled", audit.Reason);
        Assert.False(audit.Forwarded);
        Assert.Null(audit.UpstreamStatusCode);
        Assert.Equal(403, audit.ProxyStatusCode);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_for_an_unknown_operation_is_audited_with_the_requested_key()
    {
        var result = await ExecuteAsync(Envelope("unknown.operation", new { urlId = 1 }));

        var audit = await Fixture.WaitForAuditAsync(result.CorrelationId);
        Assert.Equal(ExecutionOutcome.Rejected, audit.Outcome);
        Assert.Equal("unknown_operation", audit.Reason);
        Assert.Equal("unknown.operation", audit.OperationKey);
        Assert.Null(audit.OperationId);
    }

    [Fact]
    public async Task Malformed_request_is_audited_too()
    {
        var result = await ExecuteRawAsync("{ not json");

        var audit = await Fixture.WaitForAuditAsync(result.CorrelationId);
        Assert.Equal(ExecutionOutcome.Rejected, audit.Outcome);
        Assert.Equal("invalid_envelope", audit.Reason);
    }

    [Fact]
    public async Task Audit_record_contains_no_parameter_values_or_headers()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 42, name = "Secret Person" }), request =>
            request.Headers.Add("Authorization", "Bearer super-secret-token"));

        await Fixture.WaitForAuditAsync(result.CorrelationId);

        // Read every column of the row as text and check that no caller data was stored.
        var row = await Fixture.WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT (SELECT * FROM RequestLogs WHERE CorrelationId = {result.CorrelationId} FOR JSON PATH) AS Value")
            .SingleAsync());
        Assert.DoesNotContain("Secret Person", row);
        Assert.DoesNotContain("super-secret-token", row);
    }
}
