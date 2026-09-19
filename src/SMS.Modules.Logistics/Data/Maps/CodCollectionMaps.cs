using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class CodCollectionMap : IEntityTypeConfiguration<CodCollection>
{
    public void Configure(EntityTypeBuilder<CodCollection> b)
    {
        b.ToTable("cod_collections");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.CarrierName).HasMaxLength(100);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.CollectionReference).HasMaxLength(100);
        b.Property(x => x.WriteOffReason).HasMaxLength(500);

        b.Property(x => x.ExpectedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CollectedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.RemittedAmount).HasColumnType("decimal(18,2)");

        // One per consignment. Two would count the same cash twice, and the second would look
        // exactly as legitimate as the first.
        b.HasIndex(x => x.ConsignmentId).IsUnique().HasFilter("[IsDelete] = 0");

        // "What cash is a carrier still holding" — the only question this table exists to answer.
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.CarrierId });

        b.HasOne(x => x.Consignment)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Remittances)
         .WithOne(x => x.CodCollection)
         .HasForeignKey(x => x.CodCollectionId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CodRemittanceMap : IEntityTypeConfiguration<CodRemittance>
{
    public void Configure(EntityTypeBuilder<CodRemittance> b)
    {
        b.ToTable("cod_remittances");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Note).HasMaxLength(500);

        // Carriers remit in batches, so finding every consignment covered by one transfer is a
        // question somebody asks the moment a payment lands.
        b.HasIndex(x => new { x.OrganizationId, x.Reference });
    }
}
