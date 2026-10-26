using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;

namespace NewHorizon.Automation.Infrastructure.Flows.QuotationToSo.Configurations;

public sealed class QuotationSoAutomationConfigConfiguration : IEntityTypeConfiguration<QuotationSoAutomationConfig>
{
    public static readonly Guid DefaultConfigId = new("7a2e5d31-0000-0000-0000-000000000001");

    public void Configure(EntityTypeBuilder<QuotationSoAutomationConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("QuotationSoAutomationConfig");

        builder.HasKey(config => config.Id);
        builder.Property(config => config.Id).ValueGeneratedNever();

        builder.Property(config => config.RunMode).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(config => config.ScheduleTime).HasColumnType("time");
        builder.Property(config => config.LastScheduledRunDate).HasColumnType("date");

        builder.Property(config => config.QuotationNumbers).HasMaxLength(4000);
        builder.Property(config => config.CustomerCode).HasMaxLength(50);
        builder.Property(config => config.SiteCode).HasMaxLength(50);
        builder.Property(config => config.LastRunStatus).HasMaxLength(20);
        builder.Property(config => config.LastRunMessage).HasMaxLength(1000);
        builder.Property(config => config.UpdatedBy).HasMaxLength(100);

        builder.HasData(new
        {
            Id = DefaultConfigId,
            IsActive = true,
            RunMode = SoAutomationRunMode.Both,
            DryRun = false,
            UpdatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
    }
}
