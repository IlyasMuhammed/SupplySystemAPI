using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

/// <summary>A50 D-19 — <c>logistics.document_number_formats</c> (migration A50_DocumentNumberFormats, guarded SQL).</summary>
internal sealed class DocumentNumberFormatMap : IEntityTypeConfiguration<DocumentNumberFormat>
{
    public void Configure(EntityTypeBuilder<DocumentNumberFormat> b)
    {
        b.ToTable("document_number_formats");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.DocumentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.DefaultPrefix).HasMaxLength(10).IsRequired();
        b.Property(x => x.Prefix).HasMaxLength(10).IsRequired();
        b.Property(x => x.Pattern).HasMaxLength(50);

        // One format per document type per organization; the generator reads by (organization, default prefix).
        b.HasIndex(x => new { x.OrganizationId, x.DocumentType }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.DefaultPrefix });
    }
}
