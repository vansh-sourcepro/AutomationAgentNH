using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Logging;

namespace NewHorizon.Automation.Infrastructure.Persistence.Configurations;

public sealed class AutomationLogConfiguration : IEntityTypeConfiguration<AutomationLog>
{
    public void Configure(EntityTypeBuilder<AutomationLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AutomationLog");

        builder.HasKey(log => log.Id);
        builder.Property(log => log.Id).ValueGeneratedNever();

        builder.Property(log => log.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(log => log.Module).HasMaxLength(50);
        builder.Property(log => log.ApiEndpoint).HasMaxLength(500);
        builder.Property(log => log.Result).HasMaxLength(50).IsRequired();

        builder.HasIndex(log => log.JobId).HasDatabaseName("IX_AutomationLog_JobId");
        builder.HasIndex(log => log.CorrelationId).HasDatabaseName("IX_AutomationLog_CorrelationId");

        // The nightly purge deletes by age; this is the index it scans.
        builder.HasIndex(log => log.StartedAtUtc).HasDatabaseName("IX_AutomationLog_StartedAtUtc");
    }
}
