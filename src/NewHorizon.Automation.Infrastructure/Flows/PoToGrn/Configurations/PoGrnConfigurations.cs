using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.PoToGrn;

namespace NewHorizon.Automation.Infrastructure.Flows.PoToGrn.Configurations;

public sealed class PoGrnAutomationConfigConfiguration : IEntityTypeConfiguration<PoGrnAutomationConfig>
{
    /// <summary>The one row's fixed id, so the seed and the repository agree on it.</summary>
    public static readonly Guid SingletonId = new("7a2e5d30-0000-0000-0000-000000000001");

    public void Configure(EntityTypeBuilder<PoGrnAutomationConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PoGrnAutomationConfig");

        builder.HasKey(config => config.Id);
        builder.Property(config => config.Id).ValueGeneratedNever();

        builder.Property(config => config.RunMode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(config => config.ReceiptMode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(config => config.ScheduleTime).HasColumnType("time");
        builder.Property(config => config.LastScheduledRunDate).HasColumnType("date");
        builder.Property(config => config.InvoiceNumber).HasMaxLength(PoGrnAutomationConfig.InvoiceNumberMaxLength);
        builder.Property(config => config.Sites).HasMaxLength(200);
        builder.Property(config => config.PoTypes).HasMaxLength(PoGrnAutomationConfig.PoTypesMaxLength);
        builder.Property(config => config.PoNumbers).HasMaxLength(PoGrnAutomationConfig.PoNumbersMaxLength);
        builder.Property(config => config.LastRunStatus).HasMaxLength(20);
        builder.Property(config => config.UpdatedBy).HasMaxLength(100);

        builder.Ignore(config => config.HasInvoiceNumber);

        // Seeded switched off, PO-based, no schedule, no invoice number: installing the agent
        // receives nothing until a person turns it on and names the invoice number.
        builder.HasData(new
        {
            Id = SingletonId,
            IsActive = false,
            RunMode = PoGrnRunMode.Api,
            ReceiptMode = GrnReceiptMode.Complete,
            DryRun = false,
            UpdatedAtUtc = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero),
        });
    }
}

public sealed class PoGrnRunConfiguration : IEntityTypeConfiguration<PoGrnRun>
{
    public void Configure(EntityTypeBuilder<PoGrnRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PoGrnRun");

        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();

        builder.Property(run => run.Trigger).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.ReceiptMode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.TriggeredBy).HasMaxLength(100);
        builder.Property(run => run.TriggerReference).HasMaxLength(100);
        builder.Property(run => run.RequestedSites).HasMaxLength(200);
        builder.Property(run => run.FailureReason).HasMaxLength(2000);

        builder.HasIndex(run => run.StartedAtUtc).HasDatabaseName("IX_PoGrnRun_StartedAtUtc");
    }
}

public sealed class PoGrnReceiptConfiguration : IEntityTypeConfiguration<PoGrnReceipt>
{
    public void Configure(EntityTypeBuilder<PoGrnReceipt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PoGrnReceipt", table =>
            // A created GRN has a number; anything else says why there is none.
            table.HasCheckConstraint(
                "CK_PoGrnReceipt_Result",
                "([Status] = 'Created' AND [GrnNumber] IS NOT NULL) OR ([Status] <> 'Created' AND [Reason] IS NOT NULL)"));

        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.Id).ValueGeneratedNever();

        builder.Property(receipt => receipt.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(receipt => receipt.PoNumber).HasMaxLength(60).IsRequired();
        builder.Property(receipt => receipt.PoType).HasMaxLength(5).IsRequired();
        builder.Property(receipt => receipt.VendorCode).HasMaxLength(20).IsRequired();
        builder.Property(receipt => receipt.GrnNumber).HasMaxLength(60);
        builder.Property(receipt => receipt.Reason).HasMaxLength(PoGrnReceipt.ReasonMaxLength);

        builder.HasOne<PoGrnRun>()
            .WithMany()
            .HasForeignKey(receipt => receipt.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        // "Every attempt at this PO", newest first.
        builder.HasIndex(receipt => new { receipt.PoId, receipt.RecordedAtUtc })
            .HasDatabaseName("IX_PoGrnReceipt_PoId_RecordedAtUtc");
    }
}
