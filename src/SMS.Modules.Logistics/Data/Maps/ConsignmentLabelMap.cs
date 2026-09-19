using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class ConsignmentLabelMap : IEntityTypeConfiguration<ConsignmentLabel>
{
    public void Configure(EntityTypeBuilder<ConsignmentLabel> b)
    {
        b.ToTable("consignment_labels");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.AwbNumber).HasMaxLength(100).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.ProviderKey).HasMaxLength(50).IsRequired();

        // Two requests fetching the same label at once store it once: the loser hits this index
        // and rereads the winner's row.
        b.HasIndex(x => new { x.ConsignmentId, x.Sha256 }).IsUnique();

        // The printing lookup: the latest label for the consignment's current airway bill.
        b.HasIndex(x => new { x.ConsignmentId, x.AwbNumber });

        b.HasOne(x => x.Consignment)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
