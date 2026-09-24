using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Infrastructure.Flows.IndentToPo.Configurations;

public sealed class IndentPoAutomationConfigConfiguration : IEntityTypeConfiguration<IndentPoAutomationConfig>
{
    public void Configure(EntityTypeBuilder<IndentPoAutomationConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IndentPoAutomationConfig");

        builder.HasKey(config => config.Id);
        builder.Property(config => config.Id).ValueGeneratedNever();

        builder.Property(config => config.IndentKind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(config => config.RunMode).HasConversion<string>().HasMaxLength(20).IsRequired();

        // TimeOnly maps to SQL `time` (a wall-clock slot, not an instant); DateOnly to `date`.
        builder.Property(config => config.ScheduleTime).HasColumnType("time");
        builder.Property(config => config.LastScheduledRunDate).HasColumnType("date");

        builder.Property(config => config.Sites).HasMaxLength(200);
        builder.Property(config => config.IndentNumbers).HasMaxLength(4000);
        builder.Property(config => config.LastRunStatus).HasMaxLength(20);
        builder.Property(config => config.UpdatedBy).HasMaxLength(100);

        // One row per indent type — the key the scheduler and the endpoints look up by.
        builder.HasIndex(config => config.IndentKind)
            .IsUnique()
            .HasDatabaseName("UX_IndentPoAutomationConfig_IndentKind");

        // The three rows exist from the first migration; all inert, so a fresh install still
        // converts nothing until a person turns one on.
        builder.HasData(
            SeedRow(IndentKind.Regular, new Guid("6f1d4c20-0000-0000-0000-00000000000a")),
            SeedRow(IndentKind.Capital, new Guid("6f1d4c20-0000-0000-0000-00000000000b")),
            SeedRow(IndentKind.Service, new Guid("6f1d4c20-0000-0000-0000-00000000000c")));
    }

    /// <summary>
    /// A fixed seed row. The timestamp is a constant so the migration is deterministic — EF refuses
    /// <c>HasData</c> with a value that changes between model builds.
    /// </summary>
    private static object SeedRow(IndentKind kind, Guid id) => new
    {
        Id = id,
        IndentKind = kind,
        // "Indent-based" — the row does nothing on its own; a person converts named indents with the
        // screen's Run button, or switches the row to Timer-based / Both.
        RunMode = PoAutomationRunMode.Api,
        IsActive = false,
        DryRun = false,
        UpdatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
}
