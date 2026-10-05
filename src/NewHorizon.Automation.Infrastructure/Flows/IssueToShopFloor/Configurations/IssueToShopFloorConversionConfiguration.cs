using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Infrastructure.Flows.IssueToShopFloor.Configurations;

public sealed class IssueToShopFloorConversionConfiguration : IEntityTypeConfiguration<IssueToShopFloorConversion>
{
    public void Configure(EntityTypeBuilder<IssueToShopFloorConversion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IssueToShopFloorConversion");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.IssueSource).HasMaxLength(20).IsRequired();
        builder.Property(c => c.DocumentNumber).HasMaxLength(50).IsRequired();
        builder.Property(c => c.SiteCode).HasMaxLength(20);
        builder.Property(c => c.TerminalIssueNumber).HasMaxLength(50);

        builder.HasIndex(c => new { c.IssueSource, c.DocumentNumber })
            .IsUnique()
            .HasDatabaseName("UX_IssueToShopFloorConversion_Document");

        builder.HasIndex(c => c.DocumentNumber)
            .HasDatabaseName("IX_IssueToShopFloorConversion_DocNo");

        builder.HasIndex(c => c.SiteId)
            .HasDatabaseName("IX_IssueToShopFloorConversion_SiteId");
    }
}
