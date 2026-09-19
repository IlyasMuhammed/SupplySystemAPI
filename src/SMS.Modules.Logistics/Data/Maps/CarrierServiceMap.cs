using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class CarrierServiceMap : IEntityTypeConfiguration<CarrierService>
{
    public void Configure(EntityTypeBuilder<CarrierService> b)
    {
        b.ToTable("carrier_services");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.ServiceCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.ServiceName).HasMaxLength(150).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);

        // The code is how a consignment names a service, so two of them on one carrier would make
        // the match ambiguous — which is the whole failure this table exists to end.
        //
        // Filtered on IsDelete (added in T-49). Deletion here is soft, so without the filter a
        // service removed last year would reserve its code forever and the database would refuse a
        // service the repository had already accepted — the two disagreeing about the same rule.
        b.HasIndex(x => new { x.OrganizationId, x.CarrierId, x.ServiceCode })
         .IsUnique()
         .HasFilter("[IsDelete] = 0");

        // "The services for this carrier", and "which one is the default" — the only two reads.
        b.HasIndex(x => new { x.OrganizationId, x.CarrierId, x.IsDefault });

        b.Property(x => x.MinimumChargeableKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.WeightRoundingKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.MaxWeightKgPerPackage).HasColumnType("decimal(18,3)");
        b.Property(x => x.MaxLengthCm).HasColumnType("decimal(18,2)");
        b.Property(x => x.MaxLengthPlusGirthCm).HasColumnType("decimal(18,2)");

        b.Property(x => x.IsActive).HasDefaultValue(true);

        // Restrict: a carrier with services configured is one somebody is shipping on, and taking
        // its products away underneath booked consignments helps nobody.
        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
