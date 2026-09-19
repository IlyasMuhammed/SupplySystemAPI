using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class DocumentNumberSequenceMap : IEntityTypeConfiguration<DocumentNumberSequence>
{
    public void Configure(EntityTypeBuilder<DocumentNumberSequence> b)
    {
        b.ToTable("document_number_sequences");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.Prefix).HasMaxLength(10).IsRequired();

        // One counter per organization, prefix and year — and the backstop for the race where
        // two callers both find no row and both try to create the first one.
        b.HasIndex(x => new { x.OrganizationId, x.Prefix, x.Year }).IsUnique();

        b.Property(x => x.RowVersion).IsRowVersion();
    }
}
