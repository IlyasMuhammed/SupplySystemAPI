using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class ShippingRuleMap : IEntityTypeConfiguration<ShippingRule>
{
    public void Configure(EntityTypeBuilder<ShippingRule> b)
    {
        b.ToTable("shipping_rules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.ServiceCode).HasMaxLength(50);
        b.Property(x => x.Strategy).HasMaxLength(20);

        b.Property(x => x.OriginCountryIso).HasMaxLength(2);
        b.Property(x => x.DestinationCountryIso).HasMaxLength(2);
        b.Property(x => x.OriginPostcodePrefix).HasMaxLength(10);
        b.Property(x => x.DestinationPostcodePrefix).HasMaxLength(10);

        b.Property(x => x.MinChargeableWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.MaxChargeableWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.MinDeclaredValue).HasColumnType("decimal(18,2)");
        b.Property(x => x.MaxDeclaredValue).HasColumnType("decimal(18,2)");

        // Two rules at one priority would make the one that fires depend on row order, and the
        // symptom is a parcel on the wrong carrier.
        //
        // Filtered on IsDelete, because deletion here is soft: without the filter a rule removed
        // last year would keep its priority reserved forever, and the database would refuse a rule
        // the application had already accepted.
        b.HasIndex(x => new { x.OrganizationId, x.Priority })
         .IsUnique()
         .HasFilter("[IsDelete] = 0");

        b.Property(x => x.IsActive).HasDefaultValue(true);

        // Restrict: a carrier named by a live routing rule is one goods are being sent to.
        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
