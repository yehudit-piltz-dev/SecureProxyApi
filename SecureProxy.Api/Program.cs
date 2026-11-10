using System.Net;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SecureProxy.Api.Admin;
using SecureProxy.Api.Audit;
using SecureProxy.Api.Configuration;
using SecureProxy.Api.Data;
using SecureProxy.Api.Infrastructure;
using SecureProxy.Api.Proxy;
using SecureProxy.Api.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IChangeActorAccessor, HttpChangeActorAccessor>();
builder.Services.AddDbContext<ProxyDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("ProxyDatabase")));

// Configuration
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConfigurationCache>();
builder.Services.AddScoped<OperationalSettingsProvider>();
builder.Services.AddScoped<OperationConfigurationProvider>();

// Proxy pipeline
builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection(ProxyOptions.SectionName));
builder.Services.AddSingleton<IKeySource, ConfigurationKeySource>();
builder.Services.AddSingleton<DestinationConcurrencyLimiter>();
builder.Services.AddScoped<PayloadDecryptor>();
builder.Services.AddScoped<RequestAuditor>();
builder.Services.AddScoped<UpstreamForwarder>();
builder.Services.AddScoped<ProxyExecutor>();
builder.Services.AddProxyRateLimiting();

builder.Services.AddHttpClient(UpstreamForwarder.HttpClientName, client =>
    {
        // The proxy applies the configured per-operation timeout itself.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // A redirect could lead outside the configured destination.
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),

        // Pooled connections are recycled so DNS changes are picked up.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    // The default HttpClient logging writes full URLs, including query string parameter values.
    .RemoveAllLoggers();

// Admin API: enums as names ("Path", "Integer"), and problem responses carry the correlation ID.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<ProxyEnvelopeOperationFilter>();
    options.AddSecurityDefinition("AdminKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = AdminKeyFilter.HeaderName,
        Description = "Admin API key (configuration key Admin:ApiKey).",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "AdminKey" } }] = [],
    });
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

// Unhandled exceptions become a generic problem response: no stack traces or internal details.
app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    ProblemWriter.WriteAsync(context, ProxyError.InternalError)));

if (app.Environment.IsDevelopment())
{
    // Local convenience only. Other environments apply migrations as a deployment step.
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ProxyDbContext>();
        await db.Database.MigrateAsync();
        await DevelopmentDataSeeder.SeedAsync(db, app.Configuration["Seed:CustomersBaseUrl"] ?? "http://localhost:5240/");
    }

    app.UseSwagger();
    app.UseSwaggerUI();
}

// No UseHttpsRedirection: redirecting an API call does not protect a body that was already sent
// over plain HTTP. HTTPS is enforced by the hosting configuration instead (see DESIGN-NOTES.md).

app.UseRateLimiter();

const string ProxyRoute = "/api/proxy/execute";
app.MapPost(ProxyRoute, (HttpContext context, ProxyExecutor executor) => executor.ExecuteAsync(context))
    .WithName("ExecuteProxyOperation")
    .WithTags("Proxy")
    .RequireRateLimiting(RateLimitPolicies.Proxy)
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status502BadGateway)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .ProducesProblem(StatusCodes.Status504GatewayTimeout);

// Other methods reach the executor too, so they get the same audited 405 problem response.
app.MapMethods(ProxyRoute, ["GET", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"],
        (HttpContext context, ProxyExecutor executor) => executor.ExecuteAsync(context))
    .RequireRateLimiting(RateLimitPolicies.Proxy)
    .ExcludeFromDescription();

app.MapAdminEndpoints();

app.Run();

/// <summary>Exposed for <c>WebApplicationFactory</c> in the integration tests.</summary>
public partial class Program;
