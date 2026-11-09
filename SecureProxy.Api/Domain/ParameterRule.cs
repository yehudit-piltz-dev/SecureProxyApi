namespace SecureProxy.Api.Domain;

/// <summary>Validation rule for one parameter of an operation.</summary>
public sealed class ParameterRule : IConfigurationEntity
{
    public int Id { get; set; }

    public int OperationId { get; set; }

    public required string Name { get; set; }

    public ParameterLocation Location { get; set; }

    public bool IsRequired { get; set; }

    public ParameterDataType DataType { get; set; }

    /// <summary>Maximum length of a string value.</summary>
    public int? MaxLength { get; set; }

    /// <summary>Inclusive lower bound for numeric values.</summary>
    public decimal? MinValue { get; set; }

    /// <summary>Inclusive upper bound for numeric values.</summary>
    public decimal? MaxValue { get; set; }

    /// <summary>Optional regular expression a string value must fully match.</summary>
    public string? Pattern { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
