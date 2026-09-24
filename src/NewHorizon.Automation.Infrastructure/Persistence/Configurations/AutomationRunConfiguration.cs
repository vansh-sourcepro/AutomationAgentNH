using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Infrastructure.Persistence.Configurations;

public sealed class AutomationRunConfiguration : IEntityTypeConfiguration<AutomationRun>
{
    public void Configure(EntityTypeBuilder<AutomationRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AutomationRun");

        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();

        builder.Property(run => run.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(run => run.WorkflowType).HasMaxLength(50).IsRequired();

        // Strings for the same reason as on the job: an operator reading the table sees 'Chatbot'.
        builder.Property(run => run.TriggerSource).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.Mode).HasConversion<string>().HasMaxLength(10).IsRequired();

        builder.Property(run => run.TriggeredBy).HasMaxLength(100);
        builder.Property(run => run.TriggerReference).HasMaxLength(200);

        // Never blank: an absent filter is stored expanded to every type it was therefore allowed
        // to convert, so a reader can tell "found none" from "was not allowed to look".
        builder.Property(run => run.RequestedIndentTypes).HasMaxLength(60).IsRequired();
        builder.Property(run => run.RequestedSites).HasMaxLength(200);
        builder.Property(run => run.FailureReason).HasMaxLength(FieldLengths.Message);

        builder.Property(run => run.DurationMs)
            .HasComputedColumnSql("DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])");

        builder.HasIndex(run => run.StartedAtUtc)
            .HasDatabaseName("IX_AutomationRun_StartedAtUtc");

        // The dashboard question: "what has the chatbot been asking for this week?"
        builder.HasIndex(run => new { run.TriggerSource, run.StartedAtUtc })
            .HasDatabaseName("IX_AutomationRun_Trigger");

        builder.HasIndex(run => run.CorrelationId)
            .HasDatabaseName("IX_AutomationRun_CorrelationId");
    }
}
