using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    internal DbSet<PricingRule> PricingRules => Set<PricingRule>();
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
    // The source-agnostic reservation ledger behind InventoryItem.QtyReserved.
    internal DbSet<StockReservation> StockReservations => Set<StockReservation>();
    // A30 §14 — the allocation engine: who is asking, what is on its way, who was given what, and
    // in what order the organization wants that decided.
    internal DbSet<AllocationDemand> AllocationDemands => Set<AllocationDemand>();
    internal DbSet<AllocationSupply> AllocationSupplies => Set<AllocationSupply>();
    internal DbSet<AllocationRecord> AllocationRecords => Set<AllocationRecord>();
    internal DbSet<AllocationRule> AllocationRules => Set<AllocationRule>();
    // A34 C3 (D-10) — one row per organization; a missing row reads as the system defaults.
    internal DbSet<LeadTimeDefaults> LeadTimeDefaults => Set<LeadTimeDefaults>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        
        // F35 — each of those filters puts WHERE OrganizationId = @org on every query against
        // every one of these tables, and none of them had an index leading with it.
        modelBuilder.ApplyTenantIndexes();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // A38 (INV) — POS change notifications queued inside a transaction go out on commit (PosChangeHooks.cs).
        optionsBuilder.AddInterceptors(PosChangeTransactionInterceptor.Instance);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.StampTenantScopedEntities(_tenantContext);
        this.StampModifiedAt(); // A37 D-16
        var changes = CollectPosChanges();
        var result  = base.SaveChanges(acceptAllChangesOnSuccess);
        if (changes is not null) AfterPosSaveAsync(changes, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        this.StampTenantScopedEntities(_tenantContext);
        this.StampModifiedAt(); // A37 D-16
        var changes = CollectPosChanges();
        var result  = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (changes is not null) await AfterPosSaveAsync(changes, cancellationToken);
        return result;
    }

    // ── A38 (INV, D-25) — POS change hooks. Best-effort: nothing here may fail a save. ─────────────────────────

    private readonly IServiceProvider? _services;
    private bool? _hasPosListener;
    private bool _posTransactionOwned;
    private PosNotifications? _posPending;

    public InventoryDbContext(DbContextOptions<InventoryDbContext> options, ITenantContext tenantContext, IServiceProvider? services)
        : this(options, tenantContext) =>
        _services = services;

    private ILogger? PosLogger => _services?.GetService<ILogger<InventoryDbContext>>();

    private bool HasPosListener =>
        _hasPosListener ??= _services?.GetService<IServiceProviderIsService>()?.IsService(typeof(IPosChangeListener)) == true;

    private PosChangeSet? CollectPosChanges()
    {
        try
        {
            if (!HasPosListener) return null;
            var set = PosChangeSet.Collect(ChangeTracker);
            return set.IsEmpty ? null : set;
        }
        catch (Exception ex)
        {
            PosLogger?.LogWarning(ex, "A38 POS change collection failed; the save continues without notifications.");
            return null;
        }
    }

    private async Task AfterPosSaveAsync(PosChangeSet changes, CancellationToken ct)
    {
        try
        {
            var notifications = await PosNotifications.ResolveAsync(this, changes, ct);
            if (notifications.IsEmpty) return;

            if (Database.CurrentTransaction is null)
            {
                await notifications.DispatchAsync(_services!, PosLogger);
            }
            else if (_posTransactionOwned)
            {
                // Sent by PosTransactionCommittedAsync, dropped by PosTransactionEnded.
                (_posPending ??= new PosNotifications()).Merge(notifications);
            }
            else
            {
                // Another context owns the transaction and its commit is invisible here. In the background the listener's
                // reads (fresh scope, own connection) wait for that commit instead of blocking this unit of work.
                var services = _services!;
                var logger   = PosLogger;
                _ = Task.Run(() => notifications.DispatchAsync(services, logger));
            }
        }
        catch (Exception ex)
        {
            PosLogger?.LogWarning(ex, "A38 POS change notification failed after a successful save; terminals will catch up at their next sync.");
        }
    }

    internal void PosTransactionBegan(bool owned)
    {
        _posTransactionOwned = owned;
        _posPending          = null;
    }

    internal async Task PosTransactionCommittedAsync()
    {
        var pending = _posPending;
        _posPending          = null;
        _posTransactionOwned = false;
        if (pending is null || pending.IsEmpty || _services is null) return;
        await pending.DispatchAsync(_services, PosLogger);
    }

    internal void PosTransactionEnded()
    {
        _posPending          = null;
        _posTransactionOwned = false;
    }
}
