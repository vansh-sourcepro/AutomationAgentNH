using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Errors;

namespace NewHorizon.Automation.Infrastructure.Persistence.Configurations;

public sealed class AutomationErrorConfiguration : IEntityTypeConfiguration<AutomationError>
{
    public void Configure(EntityTypeBuilder<AutomationError> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AutomationError");

        builder.HasKey(error => error.Id);
        builder.Property(error => error.Id).ValueGeneratedNever();

        builder.Property(error => error.ErrorType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(error => error.TechnicalMessage).IsRequired();
        builder.Property(error => error.LaymanMessage).HasMaxLength(1000).IsRequired();
        builder.Property(error => error.ApiEndpoint).HasMaxLength(500);

        builder.HasIndex(error => error.JobId).HasDatabaseName("IX_AutomationError_JobId");
        builder.HasIndex(error => error.CreatedAtUtc).HasDatabaseName("IX_AutomationError_CreatedAtUtc");
    }
}
