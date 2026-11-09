using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureProxy.Api.Domain;

namespace SecureProxy.Api.Data;

internal sealed class DestinationConfiguration : IEntityTypeConfiguration<Destination>
{
    public void Configure(EntityTypeBuilder<Destination> builder)
    {
        builder.Property(d => d.Name).HasMaxLength(100);
        builder.HasIndex(d => d.Name).IsUnique();
        builder.Property(d => d.BaseUrl).HasMaxLength(500);
        builder.Property(d => d.RowVersion).IsRowVersion();
        builder.ToTable(t => t.HasCheckConstraint("CK_Destinations_TimeoutMs", "[TimeoutMs] > 0"));
    }
}

internal sealed class OperationConfiguration : IEntityTypeConfiguration<Operation>
{
    public void Configure(EntityTypeBuilder<Operation> builder)
    {
        builder.Property(o => o.Key).HasMaxLength(100);
        builder.HasIndex(o => o.Key).IsUnique();
        builder.Property(o => o.HttpMethod).HasMaxLength(10);
        builder.Property(o => o.PathTemplate).HasMaxLength(500);
        builder.Property(o => o.RowVersion).IsRowVersion();

        // Restrict: a destination or profile in use cannot be deleted from under its operations.
        builder.HasOne(o => o.Destination)
            .WithMany(d => d.Operations)
            .HasForeignKey(o => o.DestinationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.EncryptionProfile)
            .WithMany()
            .HasForeignKey(o => o.EncryptionProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(o => o.ParameterRules)
            .WithOne()
            .HasForeignKey(r => r.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(o => o.HeaderRules)
            .WithOne()
            .HasForeignKey(r => r.OperationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Operations_MaxRequestBytes", "[MaxRequestBytes] > 0");
            t.HasCheckConstraint("CK_Operations_MaxResponseBytes", "[MaxResponseBytes] > 0");
            t.HasCheckConstraint("CK_Operations_TimeoutMs", "[TimeoutMs] IS NULL OR [TimeoutMs] > 0");
            t.HasCheckConstraint("CK_Operations_Encryption", "[RequiresEncryption] = 0 OR [EncryptionProfileId] IS NOT NULL");
        });
    }
}

internal sealed class ParameterRuleConfiguration : IEntityTypeConfiguration<ParameterRule>
{
    public void Configure(EntityTypeBuilder<ParameterRule> builder)
    {
        builder.Property(r => r.Name).HasMaxLength(100);
        builder.HasIndex(r => new { r.OperationId, r.Name }).IsUnique();
        builder.Property(r => r.Location).HasConversion<string>().HasMaxLength(10);
        builder.Property(r => r.DataType).HasConversion<string>().HasMaxLength(10);
        builder.Property(r => r.MinValue).HasPrecision(28, 8);
        builder.Property(r => r.MaxValue).HasPrecision(28, 8);
        builder.Property(r => r.Pattern).HasMaxLength(500);
    }
}

internal sealed class HeaderRuleConfiguration : IEntityTypeConfiguration<HeaderRule>
{
    public void Configure(EntityTypeBuilder<HeaderRule> builder)
    {
        builder.Property(r => r.HeaderName).HasMaxLength(100);
        builder.HasIndex(r => new { r.OperationId, r.HeaderName }).IsUnique();
    }
}

internal sealed class EncryptionProfileConfiguration : IEntityTypeConfiguration<EncryptionProfile>
{
    public void Configure(EntityTypeBuilder<EncryptionProfile> builder)
    {
        builder.Property(p => p.Name).HasMaxLength(100);
        builder.HasIndex(p => p.Name).IsUnique();
        builder.Property(p => p.Algorithm).HasMaxLength(20);
        builder.Property(p => p.KeyReference).HasMaxLength(200);
    }
}

internal sealed class OperationalSettingConfiguration : IEntityTypeConfiguration<OperationalSetting>
{
    public void Configure(EntityTypeBuilder<OperationalSetting> builder)
    {
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(100);
        builder.Property(s => s.Value).HasMaxLength(500);
        builder.Property(s => s.Description).HasMaxLength(500);
    }
}

internal sealed class RequestLogConfiguration : IEntityTypeConfiguration<RequestLog>
{
    public void Configure(EntityTypeBuilder<RequestLog> builder)
    {
        builder.Property(l => l.CorrelationId).HasMaxLength(64);
        builder.HasIndex(l => l.CorrelationId);
        builder.HasIndex(l => l.StartedAt);
        builder.Property(l => l.OperationKey).HasMaxLength(100);
        builder.Property(l => l.Outcome).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.Reason).HasMaxLength(100);
    }
}

internal sealed class ConfigurationChangeConfiguration : IEntityTypeConfiguration<ConfigurationChange>
{
    public void Configure(EntityTypeBuilder<ConfigurationChange> builder)
    {
        builder.Property(c => c.EntityType).HasMaxLength(100);
        builder.Property(c => c.EntityKey).HasMaxLength(200);
        builder.HasIndex(c => new { c.EntityType, c.EntityKey });
        builder.Property(c => c.ChangeType).HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.ChangedBy).HasMaxLength(100);
    }
}
