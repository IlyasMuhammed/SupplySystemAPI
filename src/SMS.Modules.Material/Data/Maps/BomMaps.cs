using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Data.Maps;

internal sealed class BillOfMaterialMap : IEntityTypeConfiguration<BillOfMaterial>
{
    public void Configure(EntityTypeBuilder<BillOfMaterial> b)
    {
        b.ToTable("bill_of_materials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired();
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.BomNumber).HasMaxLength(50).IsRequired();
        // Composite, not global — each org draws its own BOM numbers.
        b.HasIndex(x => new { x.OrganizationId, x.BomNumber }).IsUnique();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.BaseQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.BaseUom).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        // §7.4 UQ — one version number per product (and variant) per org. NULL variants compare
        // equal in a SQL Server unique index, which is exactly the product-level uniqueness wanted.
        b.HasIndex(x => new { x.OrganizationId, x.ProductUuid, x.ProductVariantUuid, x.Version }).IsUnique();
        // §27.3 IX_BOM_Product_Active — the "which recipe is live for this product" lookup.
        b.HasIndex(x => new { x.OrganizationId, x.ProductUuid, x.Status });

        b.HasMany(x => x.Lines)
         .WithOne(x => x.Bom)
         .HasForeignKey(x => x.BomId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BillOfMaterialLineMap : IEntityTypeConfiguration<BillOfMaterialLine>
{
    public void Configure(EntityTypeBuilder<BillOfMaterialLine> b)
    {
        b.ToTable("bill_of_material_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.ScrapPercentage).HasColumnType("decimal(5,2)");
        b.Property(x => x.Uom).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        // The circular-reference walk asks "which live BOMs consume this product".
        b.HasIndex(x => new { x.OrganizationId, x.MaterialProductUuid });
        b.HasIndex(x => new { x.BomId, x.Sequence });
    }
}
