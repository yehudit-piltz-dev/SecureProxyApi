namespace SecureProxy.Api.Domain;

/// <summary>
/// History entry written in the same transaction as every change to a configuration entity.
/// </summary>
public sealed class ConfigurationChange
{
    public long Id { get; set; }

    /// <summary>Entity type name, for example <c>Operation</c>.</summary>
    public required string EntityType { get; set; }

    /// <summary>Primary key of the changed entity.</summary>
    public required string EntityKey { get; set; }

    public ConfigurationChangeType ChangeType { get; set; }

    public DateTime ChangedAt { get; set; }

    public required string ChangedBy { get; set; }

    /// <summary>
    /// JSON object of changed properties: <c>{"IsEnabled":{"old":true,"new":false}}</c>.
    /// Properties marked <see cref="NotInHistoryAttribute"/> are left out.
    /// </summary>
    public required string Changes { get; set; }
}
