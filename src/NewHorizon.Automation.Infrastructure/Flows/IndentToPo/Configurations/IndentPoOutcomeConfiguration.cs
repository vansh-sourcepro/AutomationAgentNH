using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Infrastructure.Flows.IndentToPo.Configurations;

public sealed class IndentPoOutcomeConfiguration : IEntityTypeConfiguration<IndentPoOutcome>
{
    public void Configure(EntityTypeBuilder<IndentPoOutcome> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "IndentPoOutcome",
            table => table.HasCheckConstraint(
                "CK_IndentPoOutcome_Result",
                // An order must name itself; anything else must name its reason. This is the rule
                // the table exists for: an indent that converted to nothing can never be recorded
                // without the cause beside it.
                "([Outcome] = 'Created' AND [PoId] IS NOT NULL AND [PoNumber] IS NOT NULL) "
                + "OR ([Outcome] <> 'Created' AND [Reason] IS NOT NULL)"));

        builder.HasKey(outcome => outcome.Id);
        builder.Property(outcome => outcome.Id).ValueGeneratedNever();

        builder.Property(outcome => outcome.Outcome)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(outcome => outcome.VendorCode).HasMaxLength(20);
        builder.Property(outcome => outcome.CurrencyCode).HasMaxLength(5);
        builder.Property(outcome => outcome.RateStructureCode).HasMaxLength(20);
        builder.Property(outcome => outcome.PoNumber).HasMaxLength(50);
        builder.Property(outcome => outcome.Reason).HasMaxLength(FieldLengths.Message);

        // An outcome is meaningless without the execution that produced it, so it dies with it.
        // Contrast AutomationLog and AutomationError, which are deliberately left unconstrained
        // because their retention windows are configured separately.
        builder.HasOne<Job>()
            .WithMany()
            .HasForeignKey(outcome => outcome.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // NoAction, not Cascade: the job already cascades to both this row and the step, and two
        // cascade paths to one table is exactly what SQL Server refuses.
        builder.HasOne<JobStep>()
            .WithMany()
            .HasForeignKey(outcome => outcome.StepId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(outcome => new { outcome.JobId, outcome.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_IndentPoOutcome_Job_Sequence");

        // One tracking row per ERP purchase order. If a bug ever recorded the same PO twice, this
        // is where it stops rather than quietly doubling the count on a report.
        builder.HasIndex(outcome => outcome.PoId)
            .IsUnique()
            .HasFilter("[PoId] IS NOT NULL")
            .HasDatabaseName("UX_IndentPoOutcome_PoId");

        builder.HasIndex(outcome => outcome.VendorCode)
            .HasDatabaseName("IX_IndentPoOutcome_VendorCode");
    }
}
