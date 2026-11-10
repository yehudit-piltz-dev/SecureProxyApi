using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SecureProxy.Api.Configuration;
using SecureProxy.Api.Data;
using SecureProxy.Api.Domain;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Admin;

/// <summary>
/// Configuration API. Updates use optimistic concurrency: the caller sends the version it read
/// (the ETag) in If-Match, and a stale version gets 409 Conflict instead of overwriting a newer change.
/// Every successful write evicts the configuration cache of this instance.
/// </summary>
public static partial class AdminEndpoints
{
    private const int MaxTimeoutMs = 300_000;
    private const int MaxRequestBytesLimit = 1024 * 1024;
    private const int MaxResponseBytesLimit = 100 * 1024 * 1024;
    private const int MaxPageSize = 200;

    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin")
            .AddEndpointFilter<AdminKeyFilter>()
            .RequireRateLimiting(RateLimitPolicies.Admin)
            .WithTags("Admin");

        admin.MapGet("/destinations", ListDestinations);
        admin.MapGet("/destinations/{id:int}", GetDestination);
        admin.MapPost("/destinations", CreateDestination);
        admin.MapPut("/destinations/{id:int}", UpdateDestination);

        admin.MapGet("/operations", ListOperations);
        admin.MapGet("/operations/{id:int}", GetOperation);
        admin.MapPost("/operations", CreateOperation);
        admin.MapPut("/operations/{id:int}", UpdateOperation);

        admin.MapGet("/configuration-changes", ListConfigurationChanges);
        admin.MapGet("/request-logs", ListRequestLogs);
        return admin;
    }

    // ---- Destinations -------------------------------------------------------------------------

    private static async Task<IResult> ListDestinations(ProxyDbContext db, CancellationToken ct)
    {
        var destinations = await db.Destinations.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct);
        return Results.Ok(destinations.Select(DestinationResponse.From));
    }

    private static async Task<IResult> GetDestination(int id, ProxyDbContext db, HttpContext http, CancellationToken ct)
    {
        var destination = await db.Destinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return AdminResults.NotFound();
        }

        http.Response.Headers.ETag = ConfigurationVersion.ToETag(destination.RowVersion);
        return Results.Ok(DestinationResponse.From(destination));
    }

    private static async Task<IResult> CreateDestination(
        DestinationRequest request, ProxyDbContext db, ConfigurationCache cache, HttpContext http, CancellationToken ct)
    {
        var errors = ValidateDestination(request);
        if (errors.Count > 0)
        {
            return AdminResults.ValidationFailed(errors);
        }

        var destination = new Destination { Name = request.Name.Trim(), BaseUrl = request.BaseUrl.Trim() };
        Apply(destination, request);
        db.Destinations.Add(destination);

        if (await SaveAsync(db, cache, "destination", ct) is { } failure)
        {
            return failure;
        }

        http.Response.Headers.ETag = ConfigurationVersion.ToETag(destination.RowVersion);
        return Results.Created($"/api/admin/destinations/{destination.Id}", DestinationResponse.From(destination));
    }

    private static async Task<IResult> UpdateDestination(
        int id,
        DestinationRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ProxyDbContext db,
        ConfigurationCache cache,
        HttpContext http,
        CancellationToken ct)
    {
        if (CheckPrecondition(ifMatch, out var version) is { } preconditionFailure)
        {
            return preconditionFailure;
        }

        var errors = ValidateDestination(request);
        if (errors.Count > 0)
        {
            return AdminResults.ValidationFailed(errors);
        }

        var destination = await db.Destinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (destination is null)
        {
            return AdminResults.NotFound();
        }

        // The UPDATE is sent with "WHERE RowVersion = <version the caller read>". If another update
        // happened in between, no row matches and EF throws DbUpdateConcurrencyException (409).
        db.Entry(destination).Property(d => d.RowVersion).OriginalValue = version;
        Apply(destination, request);
        db.Entry(destination).Property(d => d.UpdatedAt).IsModified = true;

        if (await SaveAsync(db, cache, "destination", ct) is { } failure)
        {
            return failure;
        }

        http.Response.Headers.ETag = ConfigurationVersion.ToETag(destination.RowVersion);
        return Results.Ok(DestinationResponse.From(destination));
    }

    private static void Apply(Destination destination, DestinationRequest request)
    {
        destination.Name = request.Name.Trim();
        destination.BaseUrl = request.BaseUrl.Trim();
        destination.IsEnabled = request.IsEnabled;
        destination.TimeoutMs = request.TimeoutMs;
    }

    private static List<string> ValidateDestination(DestinationRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            errors.Add("name is required and must be at most 100 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl) || request.BaseUrl.Length > 500
            || !OperationSnapshotFactory.TryNormalizeBaseUrl(request.BaseUrl.Trim(), out _))
        {
            errors.Add(OperationSnapshotFactory.InvalidBaseUrlMessage);
        }

        if (request.TimeoutMs is <= 0 or > MaxTimeoutMs)
        {
            errors.Add($"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        }

        return errors;
    }

    // ---- Operations ---------------------------------------------------------------------------

    private static IQueryable<Operation> OperationsWithRules(ProxyDbContext db) =>
        db.Operations.Include(o => o.ParameterRules).Include(o => o.HeaderRules).AsSplitQuery();

    private static async Task<IResult> ListOperations(ProxyDbContext db, CancellationToken ct)
    {
        var operations = await OperationsWithRules(db).AsNoTracking().OrderBy(o => o.Id).ToListAsync(ct);
        return Results.Ok(operations.Select(OperationResponse.From));
    }

    private static async Task<IResult> GetOperation(int id, ProxyDbContext db, HttpContext http, CancellationToken ct)
    {
        var operation = await OperationsWithRules(db).AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct);
        if (operation is null)
        {
            return AdminResults.NotFound();
        }

        http.Response.Headers.ETag = ConfigurationVersion.ToETag(operation.RowVersion);
        return Results.Ok(OperationResponse.From(operation));
    }

    private static async Task<IResult> CreateOperation(
        OperationRequest request, ProxyDbContext db, ConfigurationCache cache, HttpContext http, CancellationToken ct)
    {
        var operation = new Operation { Key = request.Key, HttpMethod = request.HttpMethod, PathTemplate = request.PathTemplate };
        var errors = await ApplyAndValidateAsync(operation, request, db, ct);
        if (errors.Count > 0)
        {
            return AdminResults.ValidationFailed(errors);
        }

        db.Operations.Add(operation);
        if (await SaveAsync(db, cache, "operation", ct) is { } failure)
        {
            return failure;
        }

        http.Response.Headers.ETag = ConfigurationVersion.ToETag(operation.RowVersion);
        return Results.Created($"/api/admin/operations/{operation.Id}", OperationResponse.From(operation));
    }

    private static async Task<IResult> UpdateOperation(
        int id,
        OperationRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ProxyDbContext db,
        ConfigurationCache cache,
        HttpContext http,
        CancellationToken ct)
    {
        if (CheckPrecondition(ifMatch, out var version) is { } preconditionFailure)
        {
            return preconditionFailure;
        }

        var operation = await OperationsWithRules(db).SingleOrDefaultAsync(o => o.Id == id, ct);
        if (operation is null)
        {
            return AdminResults.NotFound();
        }

        db.Entry(operation).Property(o => o.RowVersion).OriginalValue = version;
        var errors = await ApplyAndValidateAsync(operation, request, db, ct);
        if (errors.Count > 0)
        {
            return AdminResults.ValidationFailed(errors);
        }

        // Rules live in their own tables. Touching the operation row makes every update, including a
        // rules-only change, go through the operation's rowversion check and produce a new version.
        db.Entry(operation).Property(o => o.UpdatedAt).IsModified = true;

        if (await SaveAsync(db, cache, "operation", ct) is { } failure)
        {
            return failure;
        }

        http.Response.Headers.ETag = ConfigurationVersion.ToETag(operation.RowVersion);
        return Results.Ok(OperationResponse.From(operation));
    }

    /// <summary>
    /// Applies the request to the entity and validates the result with the same checks the proxy
    /// runs on every load, so configuration the proxy would refuse to execute is refused here first.
    /// </summary>
    private static async Task<List<string>> ApplyAndValidateAsync(Operation operation, OperationRequest request, ProxyDbContext db, CancellationToken ct)
    {
        var errors = ValidateOperationFields(request);
        if (errors.Count > 0)
        {
            return errors;
        }

        operation.Key = request.Key;
        operation.DestinationId = request.DestinationId;
        operation.HttpMethod = request.HttpMethod.Trim().ToUpperInvariant();
        operation.PathTemplate = request.PathTemplate.Trim();
        operation.IsEnabled = request.IsEnabled;
        operation.MaxRequestBytes = request.MaxRequestBytes;
        operation.MaxResponseBytes = request.MaxResponseBytes;
        operation.TimeoutMs = request.TimeoutMs;
        operation.RequiresEncryption = request.RequiresEncryption;
        operation.EncryptionProfileId = request.EncryptionProfileId;
        operation.AllowUnknownParameters = request.AllowUnknownParameters;
        SyncParameterRules(operation, request.ParameterRules, db);
        SyncHeaderRules(operation, request.HeaderRules, db);

        operation.Destination = await db.Destinations.SingleOrDefaultAsync(d => d.Id == request.DestinationId, ct);
        if (operation.Destination is null)
        {
            errors.Add("destinationId does not refer to an existing destination.");
        }

        operation.EncryptionProfile = request.EncryptionProfileId is { } profileId
            ? await db.EncryptionProfiles.SingleOrDefaultAsync(p => p.Id == profileId, ct)
            : null;
        if (request.EncryptionProfileId is not null && operation.EncryptionProfile is null)
        {
            errors.Add("encryptionProfileId does not refer to an existing encryption profile.");
        }

        if (errors.Count == 0 && OperationSnapshotFactory.Create(operation).ConfigurationError is { } configurationError)
        {
            errors.Add(configurationError);
        }

        return errors;
    }

    private static List<string> ValidateOperationFields(OperationRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(request.Key) || !OperationKeyFormat.IsValid(request.Key))
        {
            errors.Add("key must be letters and digits separated by '.', '_' or '-', at most 100 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.HttpMethod) || !OperationSnapshotFactory.AllowedMethods.Contains(request.HttpMethod.Trim().ToUpperInvariant()))
        {
            errors.Add($"httpMethod must be one of {string.Join(", ", OperationSnapshotFactory.AllowedMethods)}.");
        }

        if (request.PathTemplate is null || request.PathTemplate.Length > 500)
        {
            errors.Add("pathTemplate is required and must be at most 500 characters.");
        }

        if (request.MaxRequestBytes is <= 0 or > MaxRequestBytesLimit)
        {
            errors.Add($"maxRequestBytes must be between 1 and {MaxRequestBytesLimit}.");
        }

        if (request.MaxResponseBytes is <= 0 or > MaxResponseBytesLimit)
        {
            errors.Add($"maxResponseBytes must be between 1 and {MaxResponseBytesLimit}.");
        }

        if (request.TimeoutMs is <= 0 or > MaxTimeoutMs)
        {
            errors.Add($"timeoutMs must be empty or between 1 and {MaxTimeoutMs}.");
        }

        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in request.ParameterRules ?? [])
        {
            if (string.IsNullOrEmpty(rule.Name) || !ParameterNameRegex().IsMatch(rule.Name))
            {
                errors.Add($"Parameter name '{rule.Name}' must start with a letter and contain only letters, digits and '_'.");
            }
            else if (!parameterNames.Add(rule.Name))
            {
                errors.Add($"Parameter '{rule.Name}' is defined more than once.");
            }

            if (rule.MaxLength is <= 0 || rule.Pattern?.Length > 500)
            {
                errors.Add($"Parameter '{rule.Name}' has an invalid maxLength or pattern.");
            }
        }

        var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in request.HeaderRules ?? [])
        {
            if (string.IsNullOrEmpty(rule.HeaderName) || !HeaderNameRegex().IsMatch(rule.HeaderName))
            {
                errors.Add($"Header name '{rule.HeaderName}' is invalid.");
            }
            else if (!headerNames.Add(rule.HeaderName))
            {
                errors.Add($"Header '{rule.HeaderName}' is defined more than once.");
            }
            else if (HeaderPolicy.IsForbidden(rule.HeaderName))
            {
                errors.Add($"Header '{rule.HeaderName}' is never forwarded.");
            }
            else if (HeaderPolicy.IsSensitive(rule.HeaderName) && !rule.AllowSensitive)
            {
                errors.Add($"Header '{rule.HeaderName}' is sensitive; set allowSensitive to forward it.");
            }
        }

        return errors;
    }

    /// <summary>Updates rules in place by name, so the history shows real changes rather than delete-and-recreate.</summary>
    private static void SyncParameterRules(Operation operation, IReadOnlyList<ParameterRuleRequest>? requested, ProxyDbContext db)
    {
        var existing = operation.ParameterRules.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var request in requested ?? [])
        {
            if (!existing.Remove(request.Name, out var rule))
            {
                rule = new ParameterRule { Name = request.Name };
                operation.ParameterRules.Add(rule);
            }

            rule.Name = request.Name;
            rule.Location = request.Location;
            rule.IsRequired = request.IsRequired;
            rule.DataType = request.DataType;
            rule.MaxLength = request.MaxLength;
            rule.MinValue = request.MinValue;
            rule.MaxValue = request.MaxValue;
            rule.Pattern = string.IsNullOrEmpty(request.Pattern) ? null : request.Pattern;
        }

        foreach (var removed in existing.Values)
        {
            operation.ParameterRules.Remove(removed);
            db.ParameterRules.Remove(removed);
        }
    }

    private static void SyncHeaderRules(Operation operation, IReadOnlyList<HeaderRuleRequest>? requested, ProxyDbContext db)
    {
        var existing = operation.HeaderRules.ToDictionary(r => r.HeaderName, StringComparer.OrdinalIgnoreCase);
        foreach (var request in requested ?? [])
        {
            if (!existing.Remove(request.HeaderName, out var rule))
            {
                rule = new HeaderRule { HeaderName = request.HeaderName };
                operation.HeaderRules.Add(rule);
            }

            rule.HeaderName = request.HeaderName;
            rule.AllowSensitive = request.AllowSensitive;
        }

        foreach (var removed in existing.Values)
        {
            operation.HeaderRules.Remove(removed);
            db.HeaderRules.Remove(removed);
        }
    }

    // ---- History and audit --------------------------------------------------------------------

    private static async Task<IResult> ListConfigurationChanges(
        ProxyDbContext db, string? entityType, string? entityKey, int? take, CancellationToken ct)
    {
        var query = db.ConfigurationChanges.AsNoTracking();
        if (!string.IsNullOrEmpty(entityType))
        {
            query = query.Where(c => c.EntityType == entityType);
        }

        if (!string.IsNullOrEmpty(entityKey))
        {
            query = query.Where(c => c.EntityKey == entityKey);
        }

        return Results.Ok(await query.OrderByDescending(c => c.Id).Take(PageSize(take)).ToListAsync(ct));
    }

    private static async Task<IResult> ListRequestLogs(
        ProxyDbContext db, string? correlationId, string? operationKey, int? take, CancellationToken ct)
    {
        var query = db.RequestLogs.AsNoTracking();
        if (!string.IsNullOrEmpty(correlationId))
        {
            query = query.Where(l => l.CorrelationId == correlationId);
        }

        if (!string.IsNullOrEmpty(operationKey))
        {
            query = query.Where(l => l.OperationKey == operationKey);
        }

        return Results.Ok(await query.OrderByDescending(l => l.Id).Take(PageSize(take)).ToListAsync(ct));
    }

    // ---- Shared -------------------------------------------------------------------------------

    private static int PageSize(int? take) => Math.Clamp(take ?? 50, 1, MaxPageSize);

    private static IResult? CheckPrecondition(string? ifMatch, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return AdminResults.PreconditionRequired();
        }

        return ConfigurationVersion.TryParse(ifMatch, out version) ? null : AdminResults.InvalidPrecondition();
    }

    /// <summary>Saves, maps write conflicts to 409, and evicts the configuration cache on success.</summary>
    private static async Task<IResult?> SaveAsync(ProxyDbContext db, ConfigurationCache cache, string what, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AdminResults.Conflict();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Unique index violation, for example a second operation with the same key.
            return AdminResults.Duplicate(what);
        }

        cache.InvalidateAll();
        return null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,99}$")]
    private static partial Regex ParameterNameRegex();

    [GeneratedRegex("^[A-Za-z0-9-]{1,100}$")]
    private static partial Regex HeaderNameRegex();
}
