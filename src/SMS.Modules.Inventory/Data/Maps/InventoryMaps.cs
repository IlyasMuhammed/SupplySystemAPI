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
        b.Property(x => x.IsAvailableForRetail).HasDefaultValue(false);
        b.Property(x => x.IsAvailableForPos).HasDefaultValue(false);
        b.Property(x => x.IsAvailableForMirMiv).HasDefaultValue(false);
        b.Property(x => x.IsAvailableForProduction).HasDefaultValue(false);
        b.Property(x => x.IsAvailableForServices).HasDefaultValue(false);

        // Composite, not global — each org curates its own SKU/barcode catalog. Barcode is
        // nullable, so the unique index is filtered to only non-null values (a NULL barcode
        // must not collide with another NULL barcode).
        b.HasIndex(x => new { x.OrganizationId, x.Sku }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Barcode }).IsUnique().HasFilter("[Barcode] IS NOT NULL");
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.ProductId);
        b.HasIndex(x => x.DefaultSupplierId);

        b.HasOne(x => x.Product).WithMany(x => x.Variants)
            .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VariantSupplierMap : IEntityTypeConfiguration<VariantSupplier>
{
    public void Configure(EntityTypeBuilder<VariantSupplier> b)
    {
        b.ToTable("VariantSuppliers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.VendorUnitCost).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.MinOrderValue).HasColumnType("decimal(18,4)");
        b.Property(x => x.DiscountTiers).HasColumnType("nvarchar(max)");
        b.Property(x => x.QuotationRef).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.EffectiveFrom).IsRequired();
        b.Property(x => x.CurrencyId).IsRequired();
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.VendorPartNo).HasMaxLength(100);
        b.Property(x => x.IsPreferred).HasDefaultValue(false);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.VariantId, x.SupplierId });

        b.HasOne(x => x.Variant).WithMany()
            .HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PricingRuleMap : IEntityTypeConfiguration<PricingRule>
{
    public void Configure(EntityTypeBuilder<PricingRule> b)
    {
        b.ToTable("PricingRules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.PriceType).HasMaxLength(20).IsRequired();
        b.Property(x => x.MinQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.MaxQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.CurrencyId).IsRequired();
        b.Property(x => x.EffectiveFrom).IsRequired();
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.VariantId, x.PartnerId });
        b.HasIndex(x => x.PartnerId);

        b.HasOne(x => x.Variant).WithMany()
            .HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierRateHistoryMap : IEntityTypeConfiguration<SupplierRateHistory>
{
    public void Configure(EntityTypeBuilder<SupplierRateHistory> b)
    {
        b.ToTable("SupplierRateHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();

        b.Property(x => x.FieldChanged).HasMaxLength(100).IsRequired();
        b.Property(x => x.OldValue).HasMaxLength(500);
        b.Property(x => x.NewValue).HasMaxLength(500);
        b.Property(x => x.ChangeReason).HasMaxLength(500);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.VariantSupplierId);
        b.HasIndex(x => x.BulkOperationId);

        b.HasOne(x => x.VariantSupplier).WithMany()
            .HasForeignKey(x => x.VariantSupplierId).OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.BulkOperation).WithMany()
            .HasForeignKey(x => x.BulkOperationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BulkRateOperationMap : IEntityTypeConfiguration<BulkRateOperation>
{
    public void Configure(EntityTypeBuilder<BulkRateOperation> b)
    {
        b.ToTable("BulkRateOperations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.Method).HasMaxLength(20).IsRequired();
        b.Property(x => x.Value).HasColumnType("decimal(18,4)");
        b.Property(x => x.TotalImpactAmount).HasColumnType("decimal(18,4)");
        b.Property(x => x.ChangeReason).HasMaxLength(500).IsRequired();

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.PerformedAt });
    }
}

internal sealed class AttributeDefinitionMap : IEntityTypeConfiguration<AttributeDefinition>
{
    public void Configure(EntityTypeBuilder<AttributeDefinition> b)
    {
        b.ToTable("AttributeDefinitions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.AttributeName).HasMaxLength(50).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        b.Property(x => x.DataType).HasMaxLength(20).IsRequired();
        b.Property(x => x.ControlType).HasMaxLength(20).IsRequired();
        b.Property(x => x.DropdownOptions).HasColumnType("nvarchar(max)");
        b.Property(x => x.DefaultValue).HasMaxLength(200);
        b.Property(x => x.ValidationRegex).HasMaxLength(200);
        b.Property(x => x.IsRequired).HasDefaultValue(false);
        b.Property(x => x.IsSearchable).HasDefaultValue(false);
        b.Property(x => x.IsFilterable).HasDefaultValue(false);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        // Composite, not global — each org defines its own attribute catalog.
        b.HasIndex(x => new { x.OrganizationId, x.AttributeName }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
    }
}

internal sealed class CategoryAttributeMap : IEntityTypeConfiguration<CategoryAttribute>
{
    public void Configure(EntityTypeBuilder<CategoryAttribute> b)
    {
        b.ToTable("CategoryAttributes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.OrganizationId).IsRequired();

        // One link per (category, attribute) pair per org.
        b.HasIndex(x => new { x.OrganizationId, x.CategoryId, x.AttributeId }).IsUnique();

        b.HasOne(x => x.Category).WithMany(x => x.CategoryAttributes)
            .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Attribute).WithMany(x => x.CategoryAttributes)
            .HasForeignKey(x => x.AttributeId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VariantAttributeValueMap : IEntityTypeConfiguration<VariantAttributeValue>
{
    public void Configure(EntityTypeBuilder<VariantAttributeValue> b)
    {
        b.ToTable("VariantAttributeValues");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Value).HasMaxLength(500).IsRequired();
        b.Property(x => x.OrganizationId).IsRequired();

        // One value per (variant, attribute) pair — re-saving the same attribute updates it.
        b.HasIndex(x => new { x.VariantId, x.AttributeId }).IsUnique();

        b.HasOne(x => x.Variant).WithMany(x => x.AttributeValues)
            .HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Attribute).WithMany(x => x.VariantValues)
            .HasForeignKey(x => x.AttributeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductSearchIndexMap : IEntityTypeConfiguration<ProductSearchIndex>
{
    public void Configure(EntityTypeBuilder<ProductSearchIndex> b)
    {
        b.ToTable("ProductSearchIndex");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ProductCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.Sku).HasMaxLength(50).IsRequired();
        b.Property(x => x.Barcode).HasMaxLength(50);
        b.Property(x => x.VariantName).HasMaxLength(200).IsRequired();
        b.Property(x => x.CategoryName).HasMaxLength(200);
        b.Property(x => x.Brand).HasMaxLength(100);
        // The SQL Server Full-Text index lives on this column — created via raw SQL in the
        // migration (EF Core has no fluent API for FULLTEXT INDEX).
        b.Property(x => x.SearchableText).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        // One row per variant — RebuildForVariantAsync upserts against this.
        b.HasIndex(x => new { x.OrganizationId, x.VariantId }).IsUnique();

        // Cascades via VariantId only — Product already cascades to ProductVariant, so a second
        // cascade path through ProductId here would give SQL Server two ways to reach this row
        // from the same Product delete, which it rejects (Cascade Paths error).
        b.HasOne(x => x.Variant).WithMany()
            .HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Product).WithMany()
            .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
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
        b.HasOne(x => x.Variant).WithMany(x => x.InventoryItems)
            .HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Warehouse).WithMany(x => x.InventoryItems)
            .HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Zone).WithMany()
            .HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Bin).WithMany(x => x.InventoryItems)
            .HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.SetNull);
        // Non-unique composite for query performance (summing across batches, etc.)
        b.HasIndex(x => new { x.VariantId, x.WarehouseId })
            .HasDatabaseName("IX_InventoryItems_VariantId_WarehouseId_Lookup");
        // PV-005 — exactly one non-tracked row per variant per warehouse. Filtered (not a plain
        // composite unique) so batch/serial-tracked variants can still hold multiple rows per
        // variant+warehouse, one per batch/serial — see the composite unique below.
        b.HasIndex(x => new { x.VariantId, x.WarehouseId })
            .IsUnique()
            .HasDatabaseName("IX_InventoryItems_VariantId_WarehouseId")
            .HasFilter("[BatchNumber] IS NULL AND [SerialNumber] IS NULL");
        // Uniqueness among tracked rows: (variant, warehouse, batch, serial).
        b.HasIndex(x => new { x.VariantId, x.WarehouseId, x.BatchNumber, x.SerialNumber }).IsUnique();
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
        b.HasIndex(x => new { x.VariantId, x.WarehouseId, x.CreatedAt });
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasOne(x => x.Variant).WithMany()
            .HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
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
