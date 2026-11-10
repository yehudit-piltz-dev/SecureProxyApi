using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Data;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class ConfigurationConcurrencyTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // Brief 9.16: configuration concurrency conflicts are handled.
    [Fact]
    public async Task Update_with_a_stale_version_returns_409_and_does_not_overwrite()
    {
        var operation = await Fixture.CreateOperationAsync();
        var (read, etag) = await GetOperationAsync(operation.Id);

        // Admin A disables the operation using the version both admins read.
        var first = await PutOperationAsync(operation.Id, ToRequest(read) with { IsEnabled = false }, etag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Admin B still holds the old version and tries to change the timeout.
        var second = await PutOperationAsync(operation.Id, ToRequest(read) with { TimeoutMs = 1234 }, etag);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("configuration_conflict", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var (current, _) = await GetOperationAsync(operation.Id);
        Assert.False(current.IsEnabled);    // A's change is kept
        Assert.Null(current.TimeoutMs);     // B's stale change was not applied
    }

    [Fact]
    public async Task Update_without_if_match_returns_428()
    {
        var operation = await Fixture.CreateOperationAsync();
        var (read, _) = await GetOperationAsync(operation.Id);

        var response = await PutOperationAsync(operation.Id, ToRequest(read), etag: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task Rules_only_change_also_requires_the_current_version()
    {
        var operation = await Fixture.CreateOperationAsync();
        var (read, etag) = await GetOperationAsync(operation.Id);

        var first = await PutOperationAsync(operation.Id, ToRequest(read) with { HeaderRules = [] }, etag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotEqual(etag, first.Headers.ETag?.Tag);

        var stale = await PutOperationAsync(operation.Id, ToRequest(read), etag);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Rowversion_rejects_a_concurrent_write_at_the_database_level()
    {
        var operation = await Fixture.CreateOperationAsync();

        await using var scopeA = Fixture.Services.CreateAsyncScope();
        await using var scopeB = Fixture.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ProxyDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ProxyDbContext>();
        var copyA = await dbA.Operations.SingleAsync(o => o.Id == operation.Id);
        var copyB = await dbB.Operations.SingleAsync(o => o.Id == operation.Id);

        copyA.MaxRequestBytes = 1000;
        await dbA.SaveChangesAsync();

        copyB.MaxRequestBytes = 2000;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Admin_change_takes_effect_on_the_proxy_and_is_recorded_in_history()
    {
        var operation = await Fixture.CreateOperationAsync();
        var (read, etag) = await GetOperationAsync(operation.Id);

        var update = await PutOperationAsync(operation.Id, ToRequest(read) with { IsEnabled = false }, etag);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var result = await ExecuteAsync(Envelope(operation.Key, new { urlId = 1 }));
        AssertProxyError(result, HttpStatusCode.Forbidden, "operation_disabled");
        AssertNotForwarded();

        var history = await Fixture.WithDbAsync(db => db.ConfigurationChanges.AsNoTracking()
            .Where(c => c.EntityType == "Operation" && c.EntityKey == operation.Id.ToString() && c.ChangeType == Api.Domain.ConfigurationChangeType.Modified)
            .ToListAsync());
        var change = Assert.Single(history);
        Assert.Equal("admin", change.ChangedBy);
        Assert.Contains("\"IsEnabled\":{\"old\":true,\"new\":false}", change.Changes);
    }

    [Fact]
    public async Task History_of_new_entities_records_their_real_keys_and_enum_names()
    {
        // Regression for DEFECT-REPORT.md D2 and D3: history of added entities was captured before
        // SaveChanges, with temporary keys, and enums were written as numbers.
        var operation = await Fixture.CreateOperationAsync();

        var ruleHistory = await Fixture.WithDbAsync(async db =>
        {
            var urlIdRuleId = await db.ParameterRules.Where(r => r.OperationId == operation.Id && r.Name == "urlId").Select(r => r.Id).SingleAsync();
            return await db.ConfigurationChanges.AsNoTracking()
                .SingleAsync(c => c.EntityType == "ParameterRule" && c.EntityKey == urlIdRuleId.ToString());
        });

        Assert.Equal(Api.Domain.ConfigurationChangeType.Added, ruleHistory.ChangeType);
        Assert.Contains($"\"OperationId\":{{\"new\":{operation.Id}}}", ruleHistory.Changes);
        Assert.Contains("\"Location\":{\"new\":\"Path\"}", ruleHistory.Changes);
        Assert.Contains("\"DataType\":{\"new\":\"Integer\"}", ruleHistory.Changes);
    }

    [Fact]
    public async Task Admin_rejects_configuration_the_proxy_would_refuse_to_execute()
    {
        var operation = await Fixture.CreateOperationAsync();
        var (read, etag) = await GetOperationAsync(operation.Id);

        var response = await PutOperationAsync(operation.Id, ToRequest(read) with { PathTemplate = "../{urlId}" }, etag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_api_requires_the_admin_key()
    {
        using var anonymous = Fixture.CreateProxyClient();

        var missing = await anonymous.GetAsync("/api/admin/operations");
        using var wrongKey = new HttpRequestMessage(HttpMethod.Get, "/api/admin/operations");
        wrongKey.Headers.Add(AdminKeyFilter.HeaderName, "wrong");
        var wrong = await anonymous.SendAsync(wrongKey);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    private async Task<(OperationResponse Operation, string ETag)> GetOperationAsync(int id)
    {
        using var request = AdminRequest(HttpMethod.Get, $"/api/admin/operations/{id}");
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<OperationResponse>(Json))!, response.Headers.ETag!.Tag);
    }

    private async Task<HttpResponseMessage> PutOperationAsync(int id, OperationRequest body, string? etag)
    {
        using var request = AdminRequest(HttpMethod.Put, $"/api/admin/operations/{id}");
        request.Content = JsonContent.Create(body, options: Json);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return await Client.SendAsync(request);
    }

    private static HttpRequestMessage AdminRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AdminKeyFilter.HeaderName, ProxyTestFixture.AdminKey);
        return request;
    }

    private static OperationRequest ToRequest(OperationResponse o) => new(
        o.Key, o.DestinationId, o.HttpMethod, o.PathTemplate, o.IsEnabled, o.MaxRequestBytes, o.MaxResponseBytes,
        o.TimeoutMs, o.RequiresEncryption, o.EncryptionProfileId, o.AllowUnknownParameters, o.ParameterRules, o.HeaderRules);
}
