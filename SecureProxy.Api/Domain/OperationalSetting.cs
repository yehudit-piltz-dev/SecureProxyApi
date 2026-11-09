namespace SecureProxy.Api.Domain;

/// <summary>A global proxy setting stored as a key/value pair, for example the configuration cache lifetime.</summary>
public sealed class OperationalSetting : IConfigurationEntity
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Keys of the known operational settings.</summary>
public static class OperationalSettingKeys
{
    /// <summary>How long operation and destination configuration is cached, in seconds.</summary>
    public const string ConfigurationCacheSeconds = "ConfigurationCache.Seconds";

    /// <summary>Maximum size of any request envelope, in bytes, checked before the operation is known.</summary>
    public const string MaxEnvelopeBytes = "Proxy.MaxEnvelopeBytes";
}
