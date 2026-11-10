using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SecureProxy.Api.Data;

namespace SecureProxy.Api.Admin;

/// <summary>
/// Protects the admin API with a shared key sent as <c>X-Admin-Key</c>. The key comes from
/// configuration (<c>Admin:ApiKey</c>, supplied through user secrets or environment variables).
/// When no key is configured, every admin request is rejected.
/// </summary>
/// <remarks>
/// A shared key keeps the assessment self-contained. In production the admin API would use the
/// organization's identity provider (for example OAuth 2.0 client credentials) and per-user roles.
/// </remarks>
public sealed class AdminKeyFilter(IConfiguration configuration, ILogger<AdminKeyFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Key";
    public const string ConfigurationKey = "Admin:ApiKey";
    public const string AdminName = "admin";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var expected = configuration[ConfigurationKey];
        if (string.IsNullOrEmpty(expected))
        {
            logger.LogWarning("Admin request rejected: {ConfigurationKey} is not configured", ConfigurationKey);
            return AdminResults.Unauthorized();
        }

        var supplied = http.Request.Headers[HeaderName].ToString();

        // Hashing first gives equal-length inputs, so the comparison time does not reveal the key length.
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
        {
            logger.LogWarning("Admin request rejected: missing or invalid {HeaderName}", HeaderName);
            return AdminResults.Unauthorized();
        }

        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, AdminName)], authenticationType: "AdminKey"));
        return await next(context);
    }
}

/// <summary>Records the authenticated admin as the author of configuration changes.</summary>
public sealed class HttpChangeActorAccessor(IHttpContextAccessor httpContextAccessor) : IChangeActorAccessor
{
    public string? CurrentActor => httpContextAccessor.HttpContext?.User.Identity?.Name;
}
