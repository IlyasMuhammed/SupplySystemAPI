using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class CarrierMap : IEntityTypeConfiguration<Carrier>
{
    public void Configure(EntityTypeBuilder<Carrier> b)
    {
        b.ToTable("carriers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.Property(x => x.ServiceType).HasMaxLength(30);
        b.Property(x => x.TrackingUrlTemplate).HasMaxLength(300);
        b.Property(x => x.ContactName).HasMaxLength(100);
        b.Property(x => x.ContactPhone).HasMaxLength(20);
        b.Property(x => x.ContactEmail).HasMaxLength(100);
        // Defaulted at the database, which is what backfills every carrier that existed before
        // these columns did. A carrier we do not know how to talk to is one a person books.
        b.Property(x => x.ProviderKey).HasMaxLength(50).HasDefaultValue("MANUAL");
        b.Property(x => x.IntegrationMode).HasMaxLength(20).HasDefaultValue("MANUAL");
        b.Property(x => x.ScacCode).HasMaxLength(20);
        b.Property(x => x.DefaultCurrency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Active");
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasMany(x => x.Shipments)
         .WithOne(x => x.Carrier)
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ShipmentMap : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> b)
    {
        b.ToTable("shipments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ShipmentNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.ShipmentNumber }).IsUnique();
        b.Property(x => x.PoNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.CarrierName).HasMaxLength(100);
        b.Property(x => x.ShipmentType).HasMaxLength(20).HasDefaultValue("Courier");
        b.Property(x => x.TrackingNumber).HasMaxLength(100);
        b.Property(x => x.TrackingUrl).HasMaxLength(300);
        b.Property(x => x.DestinationAddress).HasMaxLength(300).IsRequired();
        b.Property(x => x.WeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.VolumeCbm).HasColumnType("decimal(18,3)");
        b.Property(x => x.FreightCost).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Preparing");
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.IsActive).HasDefaultValue(true);
    }
}
