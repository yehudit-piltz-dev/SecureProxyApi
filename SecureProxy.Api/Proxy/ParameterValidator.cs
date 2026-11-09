using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SecureProxy.Api.Configuration;
using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Proxy;

/// <summary>Parameters that passed validation, grouped by where they go in the upstream request.</summary>
public sealed record ValidatedParameters(
    IReadOnlyDictionary<string, string> Path,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    byte[]? JsonBody,
    int IgnoredUnknownCount);

/// <summary>Checks caller parameters against the operation's parameter rules.</summary>
public static class ParameterValidator
{
    /// <summary>The brief requires every request's parameters to contain this key.</summary>
    public const string UrlIdParameter = "urlId";

    public static ValidatedParameters Validate(OperationSnapshot operation, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new ProxyException(ProxyError.InvalidParameters);
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignoredUnknown = 0;

        foreach (var property in parameters.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                // Duplicate names (in any casing) could be read differently by the proxy and the destination.
                throw new ProxyException(ProxyError.InvalidParameters);
            }

            if (operation.Parameters.ContainsKey(property.Name))
            {
                values[property.Name] = property.Value;
            }
            else if (operation.AllowUnknownParameters)
            {
                // Allowed unknown parameters are accepted but dropped: only validated values are forwarded.
                ignoredUnknown++;
            }
            else
            {
                throw new ProxyException(ProxyError.UnknownParameter);
            }
        }

        if (!parameters.TryGetProperty(UrlIdParameter, out _))
        {
            throw new ProxyException(ProxyError.MissingUrlId);
        }

        var path = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = new List<KeyValuePair<string, string>>();
        var body = new List<KeyValuePair<string, JsonElement>>();

        foreach (var rule in operation.Parameters.Values)
        {
            var present = values.TryGetValue(rule.Name, out var value) && value.ValueKind != JsonValueKind.Null;
            if (!present)
            {
                // Path parameters are always required: the path cannot be built without them.
                if (rule.IsRequired || rule.Location == ParameterLocation.Path)
                {
                    throw new ProxyException(ProxyError.MissingParameter, rule.Name);
                }

                continue;
            }

            var text = ValidateValue(rule, value);
            switch (rule.Location)
            {
                case ParameterLocation.Path:
                    EnsureSafePathSegment(rule, text);
                    path[rule.Name] = text;
                    break;
                case ParameterLocation.Query:
                    query.Add(new(rule.Name, text));
                    break;
                case ParameterLocation.Body:
                    body.Add(new(rule.Name, value));
                    break;
            }
        }

        return new ValidatedParameters(path, query, body.Count > 0 ? SerializeBody(body) : null, ignoredUnknown);
    }

    /// <summary>Checks type and limits, and returns the invariant text form used in paths and query strings.</summary>
    private static string ValidateValue(ParameterRuleSnapshot rule, JsonElement value)
    {
        switch (rule.DataType)
        {
            case ParameterDataType.String when value.ValueKind == JsonValueKind.String:
                var text = value.GetString()!;
                if (rule.MaxLength is { } maxLength && text.Length > maxLength)
                {
                    throw Invalid(rule);
                }

                if (rule.Pattern is { } pattern && !Matches(pattern, text))
                {
                    throw Invalid(rule);
                }

                if (text.Any(char.IsControl))
                {
                    throw Invalid(rule);
                }

                return text;

            case ParameterDataType.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer):
                EnsureInRange(rule, integer);
                return integer.ToString(CultureInfo.InvariantCulture);

            case ParameterDataType.Decimal when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number):
                EnsureInRange(rule, number);
                return number.ToString(CultureInfo.InvariantCulture);

            case ParameterDataType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return value.GetBoolean() ? "true" : "false";

            case ParameterDataType.Guid when value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out var guid):
                return guid.ToString("D");

            default:
                // Wrong JSON type, including objects and arrays: only flat scalar values are accepted.
                throw Invalid(rule);
        }
    }

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static void EnsureInRange(ParameterRuleSnapshot rule, decimal value)
    {
        if (value < rule.MinValue || value > rule.MaxValue)
        {
            throw Invalid(rule);
        }
    }

    /// <summary>
    /// Rejects values that could change the path structure. Escaping already encodes "/" and "%",
    /// but "." and ".." survive escaping, so they are rejected explicitly, as is anything suspicious.
    /// </summary>
    private static void EnsureSafePathSegment(ParameterRuleSnapshot rule, string text)
    {
        if (text.Length == 0
            || text is "." or ".."
            || text.Contains("..", StringComparison.Ordinal)
            || text.IndexOfAny(['/', '\\', '%', '?', '#']) >= 0)
        {
            throw new ProxyException(ProxyError.InvalidPathParameter, rule.Name);
        }
    }

    private static byte[] SerializeBody(List<KeyValuePair<string, JsonElement>> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in body)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static ProxyException Invalid(ParameterRuleSnapshot rule) => new(ProxyError.InvalidParameter, rule.Name);
}
