using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Infrastructure.Persistence.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AutomationJob");

        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();

        builder.Property(job => job.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(job => job.WorkflowType).HasMaxLength(50).IsRequired();
        builder.Property(job => job.DocumentType).HasMaxLength(50).IsRequired();
        builder.Property(job => job.DocumentId).HasMaxLength(100).IsRequired();

        // Enums are stored as strings: the filtered index below reads as plain SQL, and an
        // operator inspecting the table sees 'Running', not 1.
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(job => job.Mode).HasConversion<string>().HasMaxLength(10).IsRequired();

        builder.Property(job => job.CurrentStage).HasMaxLength(50);
        builder.Property(job => job.IdempotencyKey)
            .HasMaxLength(IdempotencyKey.Length)
            .IsFixedLength()
            .IsRequired();

        builder.Property(job => job.ApprovedBy).HasMaxLength(100);
        builder.Property(job => job.CancelledBy).HasMaxLength(100);
        builder.Property(job => job.CancellationReason).HasMaxLength(1000);

        // Derived, not stored: the total is the database's own arithmetic on the two timestamps,
        // so no code path can leave it disagreeing with them.
        builder.Property(job => job.DurationMs)
            .HasComputedColumnSql("DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])");

        builder.Property(job => job.RowVersion).IsRowVersion();

        builder.Metadata
            .FindNavigation(nameof(Job.Steps))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(job => job.Steps)
            .WithOne()
            .HasForeignKey(step => step.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Provenance. Both are NoAction rather than Cascade: deleting a run or a conversion must
        // never silently take the execution history with it — the history is the point.
        builder.HasOne<IndentPoConversion>()
            .WithMany()
            .HasForeignKey(job => job.ConversionId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<AutomationRun>()
            .WithMany()
            .HasForeignKey(job => job.RunId)
            .OnDelete(DeleteBehavior.NoAction);

        // Claiming order: highest priority first, oldest first within a priority. NotBeforeUtc is
        // included so the backoff filter is served by the same index rather than a lookup per row.
        builder.HasIndex(job => new { job.Status, job.Priority, job.CreatedAtUtc })
            .IncludeProperties(job => job.NotBeforeUtc)
            .HasDatabaseName("IX_AutomationJob_Claim");

        // Layer one of idempotency, enforced by the database rather than by application code:
        // a document may have at most one job that has not been cancelled, so the ERP push and
        // the reconciliation poll cannot both create a run. Cancelled jobs are excluded so a
        // rejected document can legitimately be re-enqueued later.
        //
        // Indent conversions are excluded and given their own rule below. Under this one a
        // completed conversion would hold its key for ever, and the same indent could never be
        // converted a second time — which is wrong for an indent whose lines are still open.
        //
        // Named through the overload, not just HasDatabaseName: EF keys an index by its property
        // list, so a second HasIndex(job => job.IdempotencyKey) would return this same builder and
        // quietly overwrite the filter rather than adding an index. The generated migration then
        // drops this one and keeps only the conversion rule — losing per-document duplicate safety
        // with nothing in the diff that looks wrong.
        builder.HasIndex(job => job.IdempotencyKey, "UX_AutomationJob_IdempotencyKey_Live")
            .IsUnique()
            .HasFilter("[Status] <> 'Cancelled' AND [ConversionId] IS NULL")
            .HasDatabaseName("UX_AutomationJob_IdempotencyKey_Live");

        // A cycle has no document, so the key above cannot express "do not start another one":
        // each cycle's id is the moment it began, which is unique by construction. What must be
        // prevented is two cycles being live at once — two agents against this one database would
        // otherwise both create SJOs for the same OAFs.
        //
        // Completed and Cancelled are excluded rather than Cancelled alone, because unlike a
        // document a cycle is meant to run again: once one finishes the next may start.
        // Spelled out with <> rather than NOT IN: a filtered index predicate may only use
        // comparisons joined by AND, so IN / NOT IN / OR are rejected by SQL Server.
        builder.HasIndex(job => job.WorkflowType)
            .IsUnique()
            .HasFilter(
                $"[DocumentType] = '{DocumentTypes.Cycle}' "
                + "AND [Status] <> 'Completed' AND [Status] <> 'Cancelled'")
            .HasDatabaseName("UX_AutomationJob_LiveCycle");

        // One attempt at a time per indent — two at once would race for the same indent lines —
        // and any number of finished attempts in history, which is what tracking retries means.
        //
        // "Live" here means still going: Pending, Running or AwaitingApproval. Failed and Skipped
        // are excluded along with Completed and Cancelled, because a finished attempt — however it
        // finished — is over. Leaving one in would hold the indent's key for ever and make retry
        // silently do nothing — the endpoint would answer 200 having created no second attempt at
        // all. Unlike a cycle, a finished conversion is not resumed by the engine; it is attempted
        // again from the start.
        //
        // Keyed off ConversionId being set rather than a list of document types, because a
        // filtered predicate may only use comparisons joined by AND: IN, NOT IN and OR are all
        // rejected by SQL Server, and the three indent families would need one of them.
        builder.HasIndex(job => job.IdempotencyKey, "UX_AutomationJob_LiveIndentConversion")
            .IsUnique()
            .HasFilter(
                "[ConversionId] IS NOT NULL "
                + "AND [Status] <> 'Completed' AND [Status] <> 'Cancelled' AND [Status] <> 'Failed' "
                + "AND [Status] <> 'Skipped'")
            .HasDatabaseName("UX_AutomationJob_LiveIndentConversion");

        builder.HasIndex(job => job.DocumentId)
            .HasDatabaseName("IX_AutomationJob_DocumentId");

        builder.HasIndex(job => job.CreatedAtUtc)
            .HasDatabaseName("IX_AutomationJob_CreatedAtUtc");

        // "Every execution for this indent, newest first" — the history view's only query.
        builder.HasIndex(job => new { job.ConversionId, job.CreatedAtUtc })
            .HasDatabaseName("IX_AutomationJob_Conversion");

        builder.HasIndex(job => job.RunId)
            .HasDatabaseName("IX_AutomationJob_RunId");
    }
}
