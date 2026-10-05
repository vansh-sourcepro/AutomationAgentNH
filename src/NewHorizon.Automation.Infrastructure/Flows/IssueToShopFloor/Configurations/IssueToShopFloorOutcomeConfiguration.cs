using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Infrastructure.Flows.IssueToShopFloor.Configurations;

public sealed class IssueToShopFloorOutcomeConfiguration : IEntityTypeConfiguration<IssueToShopFloorOutcome>
{
    public void Configure(EntityTypeBuilder<IssueToShopFloorOutcome> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IssueToShopFloorOutcome");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.IssueSource).HasMaxLength(20).IsRequired();
        builder.Property(o => o.DocumentNumber).HasMaxLength(50).IsRequired();
        builder.Property(o => o.Outcome).HasMaxLength(20).IsRequired();
        builder.Property(o => o.IssueNumber).HasMaxLength(50);
        builder.Property(o => o.RefusalReason).HasMaxLength(FieldLengths.Message);

        builder.Property(o => o.TotalQuantity).HasPrecision(18, 4);

        builder.HasOne<Job>()
            .WithMany()
            .HasForeignKey(o => o.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(o => o.JobId)
            .IsUnique()
            .HasDatabaseName("UX_IssueToShopFloorOutcome_JobId");

        builder.HasIndex(o => o.DocumentNumber)
            .HasDatabaseName("IX_IssueToShopFloorOutcome_DocNo");

        builder.HasIndex(o => o.IssueNumber)
            .HasDatabaseName("IX_IssueToShopFloorOutcome_IssueNo");
    }
}
