using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Inventory.Data.Maps;

internal sealed class ProductCategoryMap : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> b)
    {
        b.ToTable("ProductCategories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        // Composite, not global — each org curates its own category codes.
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class ProductSubCategoryMap : IEntityTypeConfiguration<ProductSubCategory>
{
    public void Configure(EntityTypeBuilder<ProductSubCategory> b)
    {
        b.ToTable("ProductSubCategories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Category).WithMany(x => x.SubCategories)
            .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductMap : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("Products");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.Sku).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ShortName).HasMaxLength(50);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Brand).HasMaxLength(100);
        b.Property(x => x.UomCode).HasMaxLength(20);
        b.Property(x => x.WeightKg).HasColumnType("decimal(18,4)");
        b.Property(x => x.Dimensions).HasMaxLength(50);
        b.Property(x => x.ReorderPoint).HasColumnType("decimal(18,4)");
        b.Property(x => x.ReorderQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.MinStockLevel).HasColumnType("decimal(18,4)");
        b.Property(x => x.MaxStockLevel).HasColumnType("decimal(18,4)");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.ImageUrl).HasMaxLength(500);
        b.HasIndex(x => x.Uuid).IsUnique();
        // Composite, not global — each org curates its own SKU catalog.
        b.HasIndex(x => new { x.OrganizationId, x.Sku }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Category).WithMany(x => x.Products)
            .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.SubCategory).WithMany(x => x.Products)
            .HasForeignKey(x => x.SubCategoryId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ProductVariantMap : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> b)
    {
        b.ToTable("ProductVariants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.Sku).HasMaxLength(50).IsRequired();
        b.Property(x => x.VariantName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Barcode).HasMaxLength(50);
        b.Property(x => x.PurchasePrice).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.SellingPrice).HasColumnType("decimal(18,4)");
        b.Property(x => x.LastPurchasePrice).HasColumnType("decimal(18,4)");
        b.Property(x => x.WeightKg).HasColumnType("decimal(18,4)");
        b.Property(x => x.Dimensions).HasMaxLength(100);
        b.Property(x => x.ReorderPoint).HasColumnType("decimal(18,4)");
        b.Property(x => x.IsDefault).IsRequired();
        b.Property(x => x.IsActive).HasDefaultValue(true);

        // Composite, not global — each org curates its own SKU/barcode catalog. Barcode is
        // nullable, so the unique index is filtered to only non-null values (a NULL barcode
        // must not collide with another NULL barcode).
        b.HasIndex(x => new { x.OrganizationId, x.Sku }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Barcode }).IsUnique().HasFilter("[Barcode] IS NOT NULL");
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.ProductId);

        b.HasOne(x => x.Product).WithMany(x => x.Variants)
            .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WarehouseMap : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("Warehouses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.ContactName).HasMaxLength(200);
        b.Property(x => x.ContactPhone).HasMaxLength(50);
        b.Property(x => x.GoogleMapsUrl).HasMaxLength(500);
        b.Property(x => x.Latitude).HasColumnType("decimal(9,6)");
        b.Property(x => x.Longitude).HasColumnType("decimal(9,6)");
        b.HasIndex(x => x.Uuid).IsUnique();
        // Composite, not global — each org assigns its own warehouse codes.
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class ZoneMap : IEntityTypeConfiguration<Zone>
{
    public void Configure(EntityTypeBuilder<Zone> b)
    {
        b.ToTable("Zones");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Code).HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Warehouse).WithMany(x => x.Zones)
            .HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RackMap : IEntityTypeConfiguration<Rack>
{
    public void Configure(EntityTypeBuilder<Rack> b)
    {
        b.ToTable("Racks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.RackCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.RackName).HasMaxLength(100);
        b.HasIndex(x => new { x.ZoneId, x.RackCode }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Zone).WithMany(x => x.Racks)
            .HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShelfMap : IEntityTypeConfiguration<Shelf>
{
    public void Configure(EntityTypeBuilder<Shelf> b)
    {
        b.ToTable("Shelves");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.ShelfCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.ShelfLevel).HasMaxLength(50);
        b.HasIndex(x => new { x.RackId, x.ShelfCode }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Rack).WithMany(x => x.Shelves)
            .HasForeignKey(x => x.RackId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BinMap : IEntityTypeConfiguration<Bin>
{
    public void Configure(EntityTypeBuilder<Bin> b)
    {
        b.ToTable("Bins");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Zone).WithMany(x => x.Bins)
            .HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Rack).WithMany(x => x.Bins)
            .HasForeignKey(x => x.RackId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Shelf).WithMany(x => x.Bins)
            .HasForeignKey(x => x.ShelfId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryItemMap : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.ToTable("InventoryItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.QtyOnHand).HasColumnType("decimal(18,4)");
        b.Property(x => x.QtyReserved).HasColumnType("decimal(18,4)");
        b.Property(x => x.QtyOnOrder).HasColumnType("decimal(18,4)");
        b.Property(x => x.ReorderPoint).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)");
        b.Property(x => x.BatchNumber).HasMaxLength(50);
        b.Property(x => x.SerialNumber).HasMaxLength(50);
        b.Property(x => x.ValuationMethod).HasMaxLength(20);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Product).WithMany(x => x.InventoryItems)
            .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Warehouse).WithMany(x => x.InventoryItems)
            .HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Zone).WithMany()
            .HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Bin).WithMany(x => x.InventoryItems)
            .HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.SetNull);
        // Non-unique composite for query performance
        b.HasIndex(x => new { x.ProductId, x.WarehouseId });
        // True uniqueness is enforced at the batch/serial level:
        // (product, warehouse, batch, serial) — NULLs treated as equal in SQL Server unique constraints,
        // so non-tracked items get exactly one row per product/warehouse.
        b.HasIndex(x => new { x.ProductId, x.WarehouseId, x.BatchNumber, x.SerialNumber }).IsUnique();
    }
}

internal sealed class InventoryLedgerEntryMap : IEntityTypeConfiguration<InventoryLedgerEntry>
{
    public void Configure(EntityTypeBuilder<InventoryLedgerEntry> b)
    {
        b.ToTable("InventoryLedgerEntries");
        b.HasKey(x => x.LedgerId);
        b.Property(x => x.LedgerId).ValueGeneratedNever();
        b.Property(x => x.TransactionDate).HasDefaultValueSql("GETUTCDATE()");
        b.Property(x => x.TransactionType).HasMaxLength(50).IsRequired();
        b.Property(x => x.ReferenceType).HasMaxLength(50).IsRequired();
        b.Property(x => x.ReferenceNumber).HasMaxLength(50).IsRequired();
        b.Property(x => x.QuantityIn).HasColumnType("decimal(18,4)");
        b.Property(x => x.QuantityOut).HasColumnType("decimal(18,4)");
        b.Property(x => x.BalanceAfter).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)");
        b.Property(x => x.TransactionValue).HasColumnType("decimal(18,4)");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        b.HasIndex(x => new { x.ProductId, x.WarehouseId, x.CreatedAt });
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Product).WithMany()
            .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Warehouse).WithMany()
            .HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockAdjustmentMap : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> b)
    {
        b.ToTable("StockAdjustments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.AdjNumber).HasMaxLength(20);
        b.Property(x => x.AdjType).HasMaxLength(30);
        b.Property(x => x.Reason).HasMaxLength(50);
        b.Property(x => x.ReferenceDoc).HasMaxLength(100);
        b.Property(x => x.QtyBefore).HasColumnType("decimal(18,4)");
        b.Property(x => x.QtyAdjusted).HasColumnType("decimal(18,4)");
        b.Property(x => x.QtyAfter).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)");
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.RejectionReason).HasMaxLength(1000);
        b.HasIndex(x => x.Uuid).IsUnique();
        // Composite, not global — each org generates its own adjustment number sequence.
        b.HasIndex(x => new { x.OrganizationId, x.AdjNumber }).IsUnique().HasFilter("[AdjNumber] IS NOT NULL");
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.InventoryItem).WithMany(x => x.Adjustments)
            .HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
