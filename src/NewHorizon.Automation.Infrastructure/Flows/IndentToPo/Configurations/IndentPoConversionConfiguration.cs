using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.Infrastructure.Flows.IndentToPo.Configurations;

public sealed class IndentPoConversionConfiguration : IEntityTypeConfiguration<IndentPoConversion>
{
    public void Configure(EntityTypeBuilder<IndentPoConversion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IndentPoConversion");

        builder.HasKey(conversion => conversion.Id);
        builder.Property(conversion => conversion.Id).ValueGeneratedNever();

        builder.Property(conversion => conversion.IndentKind)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(conversion => conversion.IndentNumber).HasMaxLength(50).IsRequired();

        // Derived from IndentKind, so it is not a column — storing it would be a second copy of
        // the same fact, and the two could drift.
        builder.Ignore(conversion => conversion.DocumentType);

        // The natural key. One case per indent, however many times it is attempted — and the kind
        // is part of it because XINDID and XINDAUTOID are keys into different ERP tables.
        builder.HasIndex(conversion => new { conversion.IndentKind, conversion.IndentId })
            .IsUnique()
            .HasDatabaseName("UX_IndentPoConversion_Indent");

        builder.HasIndex(conversion => conversion.IndentNumber)
            .HasDatabaseName("IX_IndentPoConversion_IndentNumber");

        builder.HasIndex(conversion => conversion.SiteId)
            .HasDatabaseName("IX_IndentPoConversion_SiteId");
    }
}
