using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Configuration;

namespace NewHorizon.Automation.Infrastructure.Persistence.Configurations;

public sealed class AutomationConfigConfiguration : IEntityTypeConfiguration<AutomationConfig>
{
    public void Configure(EntityTypeBuilder<AutomationConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AutomationConfig");

        builder.HasKey(config => config.Id);
        builder.Property(config => config.Id).ValueGeneratedNever();

        builder.Property(config => config.Module).HasMaxLength(50).IsRequired();
        builder.Property(config => config.Mode).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(config => config.LoggingLevel).HasMaxLength(20).IsRequired();
        builder.Property(config => config.UpdatedBy).HasMaxLength(100);

        // TimeOnly maps to SQL `time`; the window is a wall-clock range, not an instant.
        builder.Property(config => config.WorkingHoursStart).HasColumnType("time");
        builder.Property(config => config.WorkingHoursEnd).HasColumnType("time");

        // One row per module: the module is the natural key the whole agent looks up by, since a
        // deployment serves exactly one client's ERP.
        builder.HasIndex(config => config.Module)
            .IsUnique()
            .HasDatabaseName("UX_AutomationConfig_Module");
    }
}
