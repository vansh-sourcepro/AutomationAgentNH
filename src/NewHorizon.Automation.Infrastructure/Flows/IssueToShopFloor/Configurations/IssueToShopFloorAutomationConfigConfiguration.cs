using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Infrastructure.Flows.IssueToShopFloor.Configurations;

public sealed class IssueToShopFloorAutomationConfigConfiguration : IEntityTypeConfiguration<IssueToShopFloorAutomationConfig>
{
    public void Configure(EntityTypeBuilder<IssueToShopFloorAutomationConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IssueToShopFloorAutomationConfig");

        builder.HasKey(config => config.Id);
        builder.Property(config => config.Id).ValueGeneratedNever();

        builder.Property(config => config.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(config => config.RunMode).HasConversion<string>().HasMaxLength(20).IsRequired();

        // TimeOnly maps to SQL `time` (a wall-clock slot, not an instant); DateOnly to `date`.
        builder.Property(config => config.ScheduleTime).HasColumnType("time");
        builder.Property(config => config.LastScheduledRunDate).HasColumnType("date");

        builder.Property(config => config.Sites).HasMaxLength(200);
        builder.Property(config => config.DocumentNumbers).HasMaxLength(4000);
        builder.Property(config => config.LastRunStatus).HasMaxLength(20);
        builder.Property(config => config.UpdatedBy).HasMaxLength(100);

        // One row per source type — the natural key the scheduler and the endpoints look up by.
        builder.HasIndex(config => config.Source)
            .IsUnique()
            .HasDatabaseName("UX_IssueToShopFloorAutomationConfig_Source");

        // The three rows exist from the first migration; all inert until configured.
        builder.HasData(
            SeedRow(IssueToShopFloorSource.Sjo, new Guid("7a2e5d30-0000-0000-0000-00000000000a")),
            SeedRow(IssueToShopFloorSource.WorkOrder, new Guid("7a2e5d30-0000-0000-0000-00000000000b")),
            SeedRow(IssueToShopFloorSource.SalesOaf, new Guid("7a2e5d30-0000-0000-0000-00000000000c")));
    }

    private static object SeedRow(IssueToShopFloorSource source, Guid id) => new
    {
        Id = id,
        Source = source,
        RunMode = PoAutomationRunMode.Api,
        IsActive = false,
        DryRun = false,
        UpdatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
}
