using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

/// <summary>
/// A45 M1. Every index leads with OrganizationId (the tenant filter is on every query). The hand-guarded migration
/// A45_SupplierPricelists creates exactly these names; keep the two in step.
/// </summary>
internal sealed class SupplierPricelistMap : IEntityTypeConfiguration<SupplierPricelist>
{
    public void Configure(EntityTypeBuilder<SupplierPricelist> b)
    {
        b.ToTable("SupplierPricelists", t =>
        {
            t.HasCheckConstraint("CK_SupplierPricelists_Price", "[UnitPrice] > 0");
            t.HasCheckConstraint("CK_SupplierPricelists_MinQty", "[MinQuantity] >= 0");
            t.HasCheckConstraint("CK_SupplierPricelists_Dates", "[ValidTo] >= [ValidFrom]");
            t.HasCheckConstraint("CK_SupplierPricelists_DateBasis", "[PricingDateBasis] IN (0, 1)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique().HasDatabaseName("UX_SupplierPricelists_UUID");
        b.Property(x => x.OrganizationId).IsRequired();

        b.Property(x => x.VendorProductCode).HasMaxLength(100);
        b.Property(x => x.VendorProductName).HasMaxLength(255);
        b.Property(x => x.MinQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,4)");
        b.Property(x => x.PricingDateBasis).HasConversion<byte>();
        b.Property(x => x.ValidFrom).HasColumnType("date");
        b.Property(x => x.ValidTo).HasColumnType("date");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();

        // The resolver's seek: one supplier, one product, active rows, then the date window.
        b.HasIndex(x => new { x.OrganizationId, x.SupplierUuid, x.ProductUuid, x.IsActive, x.ValidFrom, x.ValidTo })
         .HasDatabaseName("IX_SupplierPricelists_Lookup")
         .IncludeProperties(x => new { x.UUID, x.VariantUuid, x.UnitPrice, x.MinQuantity, x.Priority, x.PricingDateBasis, x.CurrencyId, x.LeadTimeDays });
        // The resolver when it only knows the variant (no Inventory to derive the product from).
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid, x.SupplierUuid, x.IsActive })
         .HasDatabaseName("IX_SupplierPricelists_Variant")
         .HasFilter("[VariantUuid] IS NOT NULL");
        // The supplier's Products tab.
        b.HasIndex(x => new { x.OrganizationId, x.SupplierUuid, x.IsActive })
         .HasDatabaseName("IX_SupplierPricelists_Supplier")
         .IncludeProperties(x => x.ProductUuid);
        // An exact duplicate active row is refused (a NULL variant counts as one value, so two product-level rows collide).
        b.HasIndex(x => new { x.OrganizationId, x.ProductUuid, x.VariantUuid, x.SupplierUuid, x.MinQuantity, x.ValidFrom, x.ValidTo })
         .IsUnique()
         .HasDatabaseName("UQ_SupplierPricelists_NoDuplicate")
         .HasFilter("[IsActive] = 1");
    }
}
