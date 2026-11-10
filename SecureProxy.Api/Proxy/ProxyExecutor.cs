using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Audit;
using SecureProxy.Api.Configuration;
using SecureProxy.Api.Domain;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Security;

namespace SecureProxy.Api.Proxy;

/// <summary>
/// Runs one <c>POST /api/proxy/execute</c> request through the processing sequence.
/// Every check runs before the upstream request is created, so a request that fails
/// any of them never reaches the destination (fail closed).
/// </summary>
public sealed class ProxyExecutor(
    OperationConfigurationProvider operations,
    OperationalSettingsProvider settings,
    PayloadDecryptor decryptor,
    UpstreamForwarder forwarder,
    RequestAuditor auditor,
    DestinationConcurrencyLimiter concurrencyLimiter,
    TimeProvider timeProvider,
    ILogger<ProxyExecutor> logger)
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var startedTimestamp = timeProvider.GetTimestamp();
        var audit = new RequestLog
        {
            CorrelationId = context.TraceIdentifier,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            Outcome = ExecutionOutcome.InProgress,
        };

        try
        {
            await ExecuteCoreAsync(context, audit);
        }
        catch (ProxyException ex)
        {
            await FailAsync(context, audit, ex.Error, ex.Parameter, ex.UpstreamStatusCode);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            audit.Outcome = ExecutionOutcome.CallerCancelled;
            audit.Reason = ProxyError.CallerCancelled.Code;
            audit.ProxyStatusCode = ProxyError.ClientClosedRequest;
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException)
        {
            // Configuration could not be read: fail closed.
            logger.LogError(ex, "Proxy configuration could not be loaded");
            await FailAsync(context, audit, ProxyError.ConfigurationUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected proxy failure");
            await FailAsync(context, audit, ProxyError.InternalError);
        }
        finally
        {
            audit.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            audit.DurationMs = (long)timeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            await auditor.CompleteAsync(audit);

            logger.LogInformation(
                "Proxy execution {Outcome}: operation {OperationKey}, reason {Reason}, status {ProxyStatusCode}, upstream {UpstreamStatusCode}, forwarded {Forwarded}, {DurationMs} ms",
                audit.Outcome, audit.OperationKey, audit.Reason, audit.ProxyStatusCode, audit.UpstreamStatusCode, audit.Forwarded, audit.DurationMs);
        }
    }

    private async Task ExecuteCoreAsync(HttpContext context, RequestLog audit)
    {
        var cancellationToken = context.RequestAborted;

        // 1. Validate the request envelope.
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers.Allow = "POST";
            throw new ProxyException(ProxyError.MethodNotAllowed);
        }

        var maxEnvelopeBytes = await settings.GetMaxEnvelopeBytesAsync(cancellationToken);
        using var envelope = await ProxyEnvelope.ReadAsync(context.Request, maxEnvelopeBytes, cancellationToken);
        audit.OperationKey = envelope.ClientId;

        // 2. Find the operation by its clientId key, and check that it and its destination may be used.
        var operation = await operations.FindAsync(envelope.ClientId, cancellationToken)
                        ?? throw new ProxyException(ProxyError.UnknownOperation);
        audit.OperationId = operation.Id;
        audit.DestinationId = operation.Destination?.Id;

        if (!operation.IsEnabled)
        {
            throw new ProxyException(ProxyError.OperationDisabled);
        }

        if (operation.Destination is not { IsEnabled: true })
        {
            throw new ProxyException(ProxyError.DestinationUnavailable);
        }

        if (operation.ConfigurationError is not null)
        {
            logger.LogError("Operation {OperationKey} is misconfigured: {ConfigurationError}", operation.Key, operation.ConfigurationError);
            throw new ProxyException(ProxyError.OperationMisconfigured);
        }

        if (envelope.BodyLength > operation.MaxRequestBytes)
        {
            throw new ProxyException(ProxyError.RequestTooLarge);
        }

        // 3. Decrypt when the operation requires it, then validate the parameters (urlId included).
        byte[]? plaintext = null;
        JsonDocument? decrypted = null;
        try
        {
            JsonElement parameters;
            if (operation.RequiresEncryption)
            {
                if (envelope.EncryptedJson is not { } encrypted)
                {
                    throw new ProxyException(ProxyError.EncryptionRequired);
                }

                plaintext = await decryptor.DecryptAsync(operation, encrypted, cancellationToken);
                decrypted = ParseDecrypted(plaintext);
                parameters = decrypted.RootElement;
            }
            else
            {
                if (envelope.Json is not { } json)
                {
                    throw new ProxyException(ProxyError.EncryptionNotAllowed);
                }

                parameters = json;
            }

            var validated = ParameterValidator.Validate(operation, parameters);
            if (validated.IgnoredUnknownCount > 0)
            {
                logger.LogInformation("Dropped {Count} unknown parameters for operation {OperationKey}", validated.IgnoredUnknownCount, operation.Key);
            }

            // 4. Build the upstream request from trusted configuration, audit it, then forward it.
            using var request = UpstreamRequestFactory.Create(operation, validated, context.Request.Headers, context.TraceIdentifier);

            // Held until the response has been streamed, so it bounds requests in flight per destination.
            using var lease = concurrencyLimiter.TryAcquire(operation.Destination.Id);
            if (!lease.IsAcquired)
            {
                throw new ProxyException(ProxyError.DestinationBusy);
            }

            await auditor.RecordForwardingAsync(audit);
            await forwarder.ForwardAsync(context, operation, request, audit);
        }
        finally
        {
            decrypted?.Dispose();
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static JsonDocument ParseDecrypted(byte[] plaintext)
    {
        try
        {
            var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
        }

        // Authentic but not a JSON object: still never forwarded.
        throw new ProxyException(ProxyError.InvalidEncryptedPayload);
    }

    private static async Task FailAsync(HttpContext context, RequestLog audit, ProxyError error, string? parameter = null, int? upstreamStatusCode = null)
    {
        audit.Outcome = error.Outcome;
        audit.Reason = error.Code;

        if (context.Response.HasStarted)
        {
            // Part of an upstream response was already sent; the only honest signal left is to abort.
            context.Abort();
            return;
        }

        audit.ProxyStatusCode = error.StatusCode;
        await ProblemWriter.WriteAsync(context, error, parameter, upstreamStatusCode);
    }
}
