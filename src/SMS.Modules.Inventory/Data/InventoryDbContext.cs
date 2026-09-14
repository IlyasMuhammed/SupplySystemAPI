using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Data;

internal sealed class InventoryDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public InventoryDbContext(DbContextOptions<InventoryDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    internal DbSet<ProductSubCategory> ProductSubCategories => Set<ProductSubCategory>();
    internal DbSet<Product> Products => Set<Product>();
    internal DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    internal DbSet<VariantSupplier> VariantSuppliers => Set<VariantSupplier>();
    internal DbSet<SupplierRateHistory> SupplierRateHistories => Set<SupplierRateHistory>();
    internal DbSet<BulkRateOperation> BulkRateOperations => Set<BulkRateOperation>();
    internal DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    internal DbSet<CategoryAttribute> CategoryAttributes => Set<CategoryAttribute>();
    internal DbSet<VariantAttributeValue> VariantAttributeValues => Set<VariantAttributeValue>();
    internal DbSet<ProductSearchIndex> ProductSearchIndexEntries => Set<ProductSearchIndex>();
    internal DbSet<Warehouse> Warehouses => Set<Warehouse>();
    internal DbSet<Zone> Zones => Set<Zone>();
    internal DbSet<Rack> Racks => Set<Rack>();
    internal DbSet<Shelf> Shelves => Set<Shelf>();
    internal DbSet<Bin> Bins => Set<Bin>();
    internal DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    internal DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    internal DbSet<InventoryLedgerEntry> InventoryLedgerEntries => Set<InventoryLedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
