namespace SecureProxy.Api.Domain;

/// <summary>
/// Marks proxy configuration entities. Every change to them gets creation/update
/// timestamps and is written to the configuration history (see <see cref="ConfigurationChange"/>).
/// </summary>
public interface IConfigurationEntity
{
    DateTime CreatedAt { get; set; }

    DateTime UpdatedAt { get; set; }
}

/// <summary>Excludes a property from the configuration history, for example a concurrency token.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotInHistoryAttribute : Attribute;
