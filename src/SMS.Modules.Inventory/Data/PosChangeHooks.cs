using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Data;

/// <summary>
/// A38 C11 (INV, D-25) — tells POS terminals about Inventory changes through <see cref="IPosChangeListener"/>, from one place:
/// <see cref="InventoryDbContext"/>'s SaveChanges. Every writer (repository, services, other modules that write through this
/// context: GRN, MIV, production, deliveries, POS) is covered without each one remembering to call it.
/// <list type="bullet">
/// <item>Collected before the save: InventoryItem rows whose QtyOnHand/QtyReserved changed (or were added/deleted) → stock;
/// ProductVariant SellingPrice changes → price (old/new); any other variant edit, a new or deleted variant, or an edit of its
/// product → variants changed (the PWA re-reads the item and drops it when inactive).</item>
/// <item>Delivered after the change is durable: right after a save outside any transaction; on commit of a transaction this
/// context began (dropped on rollback); for a transaction enlisted from another context (UseTransaction — MIV, production
/// issues) the commit is invisible here, so delivery runs in the background and its reads wait for that commit.</item>
/// <item>Always in a fresh DI scope (the listener never shares this context or its connection), best-effort: every failure is
/// logged and swallowed, never failing the save. Skipped entirely when no listener is registered.</item>
/// </list>
/// </summary>
internal sealed class PosChangeSet
{
    public readonly HashSet<(Guid Org, int WarehouseId, int VariantId)> Stock = [];
    public readonly List<(Guid Org, Guid VariantUuid, decimal? Old, decimal? New)> Prices = [];
    public readonly HashSet<(Guid Org, Guid VariantUuid)> Variants = [];
    public readonly HashSet<(Guid Org, int ProductId)> Products = [];
    /// <summary>Variants added in this save: their Id is known only after it, their Uuid before.</summary>
    public readonly List<ProductVariant> AddedVariants = [];

    public bool IsEmpty => Stock.Count == 0 && Prices.Count == 0 && Variants.Count == 0 && Products.Count == 0 && AddedVariants.Count == 0;

    private static readonly HashSet<string> IgnoredVariantProperties = [nameof(ProductVariant.ModifiedAt)];
    private static readonly HashSet<string> IgnoredProductProperties = [nameof(Product.ModifiedAt), nameof(Product.UpdatedDate)];

    public static PosChangeSet Collect(ChangeTracker tracker)
    {
        var set = new PosChangeSet();

        foreach (var entry in tracker.Entries())
        {
            switch (entry.Entity)
            {
                case InventoryItem item when entry.State is EntityState.Added or EntityState.Deleted
                                          || (entry.State == EntityState.Modified
                                              && (entry.Property(nameof(InventoryItem.QtyOnHand)).IsModified
                                                  || entry.Property(nameof(InventoryItem.QtyReserved)).IsModified)):
                    set.Stock.Add((item.OrganizationId, item.WarehouseId, item.VariantId));
                    break;

                case ProductVariant variant when entry.State == EntityState.Added:
                    set.AddedVariants.Add(variant);
                    break;

                case ProductVariant variant when entry.State == EntityState.Deleted:
                    set.Variants.Add((variant.OrganizationId, variant.Uuid));
                    break;

                case ProductVariant variant when entry.State == EntityState.Modified:
                {
                    var modified = entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name)
                        .Where(n => !IgnoredVariantProperties.Contains(n)).ToList();
                    var price = entry.Property(nameof(ProductVariant.SellingPrice));
                    var oldPrice = (decimal?)price.OriginalValue;
                    if (price.IsModified && oldPrice != variant.SellingPrice)
                        set.Prices.Add((variant.OrganizationId, variant.Uuid, oldPrice, variant.SellingPrice));
                    if (modified.Any(n => n != nameof(ProductVariant.SellingPrice)))
                        set.Variants.Add((variant.OrganizationId, variant.Uuid));
                    break;
                }

                case Product product when entry.State == EntityState.Modified
                                       && entry.Properties.Any(p => p.IsModified && !IgnoredProductProperties.Contains(p.Metadata.Name)):
                    set.Products.Add((product.OrganizationId, product.Id));
                    break;
            }
        }

        return set;
    }
}

/// <summary>What goes to the listener, UUIDs resolved (after the save, while the rows are certainly readable).</summary>
internal sealed class PosNotifications
{
    public readonly List<(Guid Org, Guid VariantUuid, decimal? Old, decimal? New)> Prices = [];
    public readonly HashSet<(Guid Org, Guid VariantUuid)> Variants = [];
    public readonly HashSet<(Guid Org, Guid WarehouseUuid, Guid VariantUuid)> Stock = [];

    public bool IsEmpty => Prices.Count == 0 && Variants.Count == 0 && Stock.Count == 0;

    public void Merge(PosNotifications other)
    {
        Prices.AddRange(other.Prices);
        Variants.UnionWith(other.Variants);
        Stock.UnionWith(other.Stock);
    }

    public static async Task<PosNotifications> ResolveAsync(InventoryDbContext db, PosChangeSet set, CancellationToken ct)
    {
        var n = new PosNotifications();
        n.Prices.AddRange(set.Prices);
        n.Variants.UnionWith(set.Variants);
        foreach (var v in set.AddedVariants) n.Variants.Add((v.OrganizationId, v.Uuid));

        if (set.Products.Count > 0)
        {
            var productIds = set.Products.Select(p => p.ProductId).Distinct().ToList();
            var variants = await db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(v => productIds.Contains(v.ProductId))
                .Select(v => new { v.OrganizationId, v.Uuid })
                .ToListAsync(ct);
            foreach (var v in variants) n.Variants.Add((v.OrganizationId, v.Uuid));
        }

        if (set.Stock.Count > 0)
        {
            var variantIds   = set.Stock.Select(s => s.VariantId).Distinct().ToList();
            var warehouseIds = set.Stock.Select(s => s.WarehouseId).Distinct().ToList();
            var variantUuids = await db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(v => variantIds.Contains(v.Id)).Select(v => new { v.Id, v.Uuid }).ToDictionaryAsync(v => v.Id, v => v.Uuid, ct);
            var warehouseUuids = await db.Warehouses.IgnoreQueryFilters().AsNoTracking()
                .Where(w => warehouseIds.Contains(w.Id)).Select(w => new { w.Id, w.Uuid }).ToDictionaryAsync(w => w.Id, w => w.Uuid, ct);

            foreach (var (org, warehouseId, variantId) in set.Stock)
                if (variantUuids.TryGetValue(variantId, out var v) && warehouseUuids.TryGetValue(warehouseId, out var w))
                    n.Stock.Add((org, w, v));
        }

        return n;
    }

    public async Task DispatchAsync(IServiceProvider services, ILogger? logger)
    {
        try
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var listener = scope.ServiceProvider.GetService<IPosChangeListener>();
            if (listener is null) return;

            foreach (var (org, variant, oldPrice, newPrice) in Prices)
                await Safe(() => listener.OnPriceChangedAsync(org, variant, oldPrice, newPrice), "price");

            foreach (var group in Variants.GroupBy(v => v.Org))
                await Safe(() => listener.OnVariantsChangedAsync(group.Key, group.Select(v => v.VariantUuid).ToList()), "variants");

            foreach (var group in Stock.GroupBy(s => (s.Org, s.WarehouseUuid)))
                await Safe(() => listener.OnStockChangedAsync(group.Key.Org, group.Key.WarehouseUuid,
                    group.Select(s => s.VariantUuid).Distinct().ToList()), "stock");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "A38 POS change notification could not be delivered; terminals will catch up at their next sync.");
        }

        async Task Safe(Func<Task> call, string kind)
        {
            try { await call(); }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "A38 POS {Kind} change notification failed; terminals will catch up at their next sync.", kind);
            }
        }
    }
}

/// <summary>
/// Stateless (one instance for every context): routes transaction events back to the <see cref="InventoryDbContext"/> that
/// raised them, so POS notifications queued inside a transaction are sent on commit and dropped on rollback.
/// </summary>
internal sealed class PosChangeTransactionInterceptor : DbTransactionInterceptor
{
    public static readonly PosChangeTransactionInterceptor Instance = new();

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        (eventData.Context as InventoryDbContext)?.PosTransactionBegan(owned: true);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        (eventData.Context as InventoryDbContext)?.PosTransactionBegan(owned: true);
        return ValueTask.FromResult(result);
    }

    public override DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData, DbTransaction result)
    {
        (eventData.Context as InventoryDbContext)?.PosTransactionBegan(owned: false);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        (eventData.Context as InventoryDbContext)?.PosTransactionBegan(owned: false);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as InventoryDbContext)?.PosTransactionCommittedAsync().GetAwaiter().GetResult();

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        (eventData.Context as InventoryDbContext)?.PosTransactionCommittedAsync() ?? Task.CompletedTask;

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as InventoryDbContext)?.PosTransactionEnded();

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as InventoryDbContext)?.PosTransactionEnded();
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        (eventData.Context as InventoryDbContext)?.PosTransactionEnded();

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as InventoryDbContext)?.PosTransactionEnded();
        return Task.CompletedTask;
    }
}
