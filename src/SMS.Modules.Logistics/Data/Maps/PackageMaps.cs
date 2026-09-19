using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class ShipmentPackageMap : IEntityTypeConfiguration<ShipmentPackage>
{
    public void Configure(EntityTypeBuilder<ShipmentPackage> b)
    {
        b.ToTable("shipment_packages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.PackageBarcode).HasMaxLength(50).IsRequired();
        // A handling-unit barcode goes on a physical carton. Two cartons sharing one would be
        // indistinguishable on a dock, which is why voided packages are kept rather than deleted.
        b.HasIndex(x => new { x.OrganizationId, x.PackageBarcode }).IsUnique();

        b.Property(x => x.PackageType).HasMaxLength(20).IsRequired();
        b.Property(x => x.SealNumber).HasMaxLength(50);
        b.Property(x => x.VoidReason).HasMaxLength(300);

        b.Property(x => x.LengthCm).HasColumnType("decimal(18,2)");
        b.Property(x => x.WidthCm).HasColumnType("decimal(18,2)");
        b.Property(x => x.HeightCm).HasColumnType("decimal(18,2)");
        b.Property(x => x.GrossWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.NetWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.DimWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.ChargeableWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.ChargeableWeightBasis).HasMaxLength(20);
        b.Property(x => x.DeclaredValue).HasColumnType("decimal(18,2)");

        b.HasMany(x => x.Contents)
         .WithOne(x => x.ShipmentPackage)
         .HasForeignKey(x => x.ShipmentPackageId)
         .OnDelete(DeleteBehavior.Cascade);

        // Pallet nesting. Restrict, not cascade: removing a pallet must not silently delete the
        // cartons on it — they still physically exist and need re-assigning.
        b.HasMany(x => x.ChildPackages)
         .WithOne(x => x.ParentPackage)
         .HasForeignKey(x => x.ParentPackageId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PackageContentMap : IEntityTypeConfiguration<PackageContent>
{
    public void Configure(EntityTypeBuilder<PackageContent> b)
    {
        b.ToTable("package_contents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Qty).HasColumnType("decimal(18,3)");
        b.Property(x => x.BatchNumber).HasMaxLength(50);
        b.Property(x => x.SerialNumber).HasMaxLength(100);

        // Restrict, deliberately. A delivery cascades to its lines and to its packages, and a
        // package cascades to its contents — so cascading from the line as well would give SQL
        // Server two cascade paths to the same table and it refuses to create the constraint.
        // Restricting is also the correct behaviour: a line that has already been packed should
        // not be deletable without first unpacking it.
        b.HasOne(x => x.DeliveryOrderLine)
         .WithMany(x => x.PackageContents)
         .HasForeignKey(x => x.DeliveryOrderLineId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
