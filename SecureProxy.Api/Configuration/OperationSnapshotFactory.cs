using System.Text.RegularExpressions;
using SecureProxy.Api.Domain;
using SecureProxy.Api.Proxy;

namespace SecureProxy.Api.Configuration;

/// <summary>
/// Converts stored configuration into an <see cref="OperationSnapshot"/> and checks that it is safe to use.
/// The checks run on every load, so a bad row in the database fails closed instead of being forwarded.
/// </summary>
public static partial class OperationSnapshotFactory
{
    public const string SupportedEncryptionAlgorithm = "AES-256-GCM";

    /// <summary>Methods an operation may be configured with. TRACE, CONNECT, HEAD and OPTIONS are not allowed.</summary>
    public static readonly IReadOnlySet<string> AllowedMethods =
        new HashSet<string>(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    private static readonly IReadOnlySet<string> MethodsWithBody =
        new HashSet<string>(StringComparer.Ordinal) { "POST", "PUT", "PATCH" };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static OperationSnapshot Create(Operation operation)
    {
        var errors = new List<string>();

        var method = operation.HttpMethod.Trim().ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
        {
            errors.Add($"HTTP method '{operation.HttpMethod}' is not allowed.");
            method = "GET"; // Placeholder only: the snapshot is unusable because of the error.
        }

        var destination = CreateDestination(operation.Destination, errors);
        var parameters = CreateParameters(operation.ParameterRules, errors);
        ValidatePathTemplate(operation.PathTemplate, parameters, errors);

        // Every request must contain urlId, so an operation without a rule for it could never be executed.
        if (!parameters.ContainsKey(ParameterValidator.UrlIdParameter))
        {
            errors.Add($"Operation has no rule for '{ParameterValidator.UrlIdParameter}'.");
        }

        if (!MethodsWithBody.Contains(method) && parameters.Values.Any(p => p.Location == ParameterLocation.Body))
        {
            errors.Add($"Body parameters are not allowed for {method}.");
        }

        EncryptionProfileSnapshot? profile = null;
        if (operation.EncryptionProfile is { } p)
        {
            profile = new EncryptionProfileSnapshot(p.Id, p.Algorithm, p.KeyReference, p.IsEnabled);
            if (!string.Equals(p.Algorithm, SupportedEncryptionAlgorithm, StringComparison.Ordinal))
            {
                errors.Add($"Encryption algorithm '{p.Algorithm}' is not supported.");
            }
        }

        if (operation.RequiresEncryption && profile is null)
        {
            errors.Add("Encryption is required but no encryption profile is configured.");
        }

        if (operation.MaxRequestBytes <= 0 || operation.MaxResponseBytes <= 0)
        {
            errors.Add("Size limits must be positive.");
        }

        var timeoutMs = operation.TimeoutMs ?? operation.Destination?.TimeoutMs ?? 0;
        if (timeoutMs <= 0)
        {
            errors.Add("Timeout must be positive.");
            timeoutMs = 1;
        }

        return new OperationSnapshot
        {
            Id = operation.Id,
            Key = operation.Key,
            IsEnabled = operation.IsEnabled,
            HttpMethod = new HttpMethod(method),
            PathTemplate = operation.PathTemplate,
            MaxRequestBytes = operation.MaxRequestBytes,
            MaxResponseBytes = operation.MaxResponseBytes,
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            RequiresEncryption = operation.RequiresEncryption,
            EncryptionProfile = profile,
            AllowUnknownParameters = operation.AllowUnknownParameters,
            Destination = destination,
            Parameters = parameters,
            Headers = operation.HeaderRules
                .Select(h => new HeaderRuleSnapshot(h.HeaderName.Trim(), h.AllowSensitive))
                .ToList(),
            ConfigurationError = errors.Count == 0 ? null : string.Join(" ", errors),
        };
    }

    private static DestinationSnapshot? CreateDestination(Destination? destination, List<string> errors)
    {
        if (destination is null)
        {
            return null;
        }

        if (!TryNormalizeBaseUrl(destination.BaseUrl, out var baseUri))
        {
            errors.Add(InvalidBaseUrlMessage);
        }

        return new DestinationSnapshot(
            destination.Id,
            destination.Name,
            destination.IsEnabled,
            baseUri,
            TimeSpan.FromMilliseconds(Math.Max(destination.TimeoutMs, 1)));
    }

    public const string InvalidBaseUrlMessage =
        "Destination base URL must be an absolute http(s) URL without credentials, query or fragment.";

    /// <summary>Checks a destination base URL and returns it with a trailing slash.</summary>
    public static bool TryNormalizeBaseUrl(string baseUrl, out Uri? baseUri)
    {
        baseUri = null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        // A trailing slash makes relative paths append to the base path instead of replacing its last segment.
        baseUri = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
        return true;
    }

    private static Dictionary<string, ParameterRuleSnapshot> CreateParameters(IEnumerable<ParameterRule> rules, List<string> errors)
    {
        var result = new Dictionary<string, ParameterRuleSnapshot>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            Regex? pattern = null;
            if (!string.IsNullOrEmpty(rule.Pattern))
            {
                try
                {
                    // Anchored so the whole value must match. The timeout guards against catastrophic backtracking.
                    pattern = new Regex($"^(?:{rule.Pattern})$", RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (ArgumentException)
                {
                    errors.Add($"Parameter '{rule.Name}' has an invalid pattern.");
                }
            }

            if (rule.MinValue > rule.MaxValue)
            {
                errors.Add($"Parameter '{rule.Name}' has MinValue greater than MaxValue.");
            }

            if (!result.TryAdd(rule.Name, new ParameterRuleSnapshot(
                    rule.Name, rule.Location, rule.IsRequired, rule.DataType, rule.MaxLength, rule.MinValue, rule.MaxValue, pattern)))
            {
                errors.Add($"Parameter '{rule.Name}' is defined more than once.");
            }
        }

        return result;
    }

    /// <summary>
    /// The template must be a relative path made of literal segments and whole-segment
    /// <c>{name}</c> placeholders, with exactly one path parameter rule per placeholder.
    /// </summary>
    private static void ValidatePathTemplate(string template, IReadOnlyDictionary<string, ParameterRuleSnapshot> parameters, List<string> errors)
    {
        var placeholders = new HashSet<string>(StringComparer.Ordinal);
        if (template.StartsWith('/') || template.Contains('\\') || template.Contains("//", StringComparison.Ordinal))
        {
            errors.Add("Path template must be a relative path.");
        }
        else if (template.Length > 0)
        {
            foreach (var segment in template.Split('/'))
            {
                var placeholder = PlaceholderRegex().Match(segment);
                if (placeholder.Success)
                {
                    placeholders.Add(placeholder.Groups["name"].Value);
                }
                else if (!LiteralSegmentRegex().IsMatch(segment) || segment is "." or "..")
                {
                    errors.Add("Path template contains an invalid segment.");
                }
            }
        }

        var pathParameters = parameters.Values.Where(p => p.Location == ParameterLocation.Path).Select(p => p.Name);
        if (!placeholders.SetEquals(pathParameters))
        {
            errors.Add("Path template placeholders do not match the path parameter rules.");
        }
    }

    [GeneratedRegex(@"^\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}$")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-._~]+$")]
    private static partial Regex LiteralSegmentRegex();
}
