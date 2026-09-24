using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Infrastructure.Persistence.Configurations;

public sealed class JobStepConfiguration : IEntityTypeConfiguration<JobStep>
{
    public void Configure(EntityTypeBuilder<JobStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AutomationJobStep");

        builder.HasKey(step => step.Id);
        builder.Property(step => step.Id).ValueGeneratedNever();

        builder.Property(step => step.Stage).HasMaxLength(50).IsRequired();
        builder.Property(step => step.OperationName).HasMaxLength(100).IsRequired();
        builder.Property(step => step.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Recorded per step so the timeline can say "performed by the ERP" truthfully even after
        // the workflow definition changes underneath it.
        builder.Property(step => step.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();

        // The Site ID for a per-site step; null for a step that runs once per cycle.
        builder.Property(step => step.Target).HasMaxLength(50);

        // Payloads are unbounded JSON and are trimmed by the nightly retention purge.
        builder.Property(step => step.RequestPayload);
        builder.Property(step => step.ResponsePayload);

        builder.Property(step => step.ErpDocumentRef).HasMaxLength(100);
        builder.Property(step => step.Remarks).HasMaxLength(1000);
        builder.Property(step => step.ApprovedBy).HasMaxLength(100);

        // Resume walks the plan in order; one row per operation per job.
        builder.HasIndex(step => new { step.JobId, step.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_AutomationJobStep_Job_Sequence");

        builder.HasIndex(step => new { step.JobId, step.Status })
            .HasDatabaseName("IX_AutomationJobStep_Job_Status");
    }
}
