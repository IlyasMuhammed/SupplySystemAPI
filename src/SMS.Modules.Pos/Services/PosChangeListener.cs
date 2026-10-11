using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Hubs;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 C11 / API-CONTRACT §8 — <see cref="IPosChangeListener"/> over the hub (D-32 groups): price, product and customer
/// balance changes go to the organization's POS group; stock to the group of every active store whose warehouse it is,
/// with the current available quantity. Called by Inventory after commit in a fresh scope (no tenant: every read is
/// explicit-org with the filter off) and by POS itself for balances. Best-effort: failures are logged, never thrown.
/// </summary>
internal sealed class PosChangeListener : IPosChangeListener
{
    private readonly PosDbContext            _db;
    private readonly IPosHubNotifier         _hub;
    private readonly IPosCatalogSource?      _catalog;
    private readonly IPosStockPoster?        _stock;
    private readonly IPosCustomerSource?     _customers;
    private readonly ICustomerBalanceLookup? _balances;
    private readonly ILogger                 _log;

    public PosChangeListener(
        PosDbContext db, IPosHubNotifier hub, IPosCatalogSource? catalog = null, IPosStockPoster? stock = null,
        IPosCustomerSource? customers = null, ICustomerBalanceLookup? balances = null, ILogger<PosChangeListener>? log = null)
    {
        _db = db; _hub = hub; _catalog = catalog; _stock = stock; _customers = customers; _balances = balances;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>ProductUpdated while usable on POS, else ProductDeactivated (also for a variant that no longer exists).</summary>
    public async Task OnVariantsChangedAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        if (variantUuids.Count == 0) return;
        try
        {
            var variants = _catalog is null ? [] : await _catalog.GetByVariantUuidsAsync(organizationId, variantUuids, ct);
            var products = variants.Select(v => v.ProductUuid).Distinct().ToList();
            var settings = await _db.ProductSettings.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == organizationId && products.Contains(s.ProductUuid)).ToDictionaryAsync(s => s.ProductUuid, ct);
            foreach (var uuid in variantUuids.Distinct())
            {
                var v = variants.FirstOrDefault(x => x.VariantUuid == uuid);
                if (v is not null && PosOrderService.IsUsable(v, settings.GetValueOrDefault(v.ProductUuid)))
                    await _hub.ProductUpdatedAsync(organizationId, uuid);
                else
                    await _hub.ProductDeactivatedAsync(organizationId, uuid);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "POS: product change push failed for organization {Org}.", organizationId);
        }
    }

    public Task OnPriceChangedAsync(Guid organizationId, Guid variantUuid, decimal? oldPrice, decimal? newPrice, CancellationToken ct = default) =>
        _hub.PriceChangedAsync(organizationId, variantUuid, oldPrice, newPrice);

    public async Task OnStockChangedAsync(Guid organizationId, Guid warehouseUuid, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        if (variantUuids.Count == 0) return;
        try
        {
            var stores = await _db.Stores.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == organizationId && s.WarehouseUuid == warehouseUuid && s.IsActive)
                .Select(s => s.Uuid).ToListAsync(ct);
            if (stores.Count == 0 || _stock is null) return;
            var levels = (await _stock.GetLevelsAsync(organizationId, warehouseUuid, variantUuids, ct)).ToDictionary(l => l.VariantUuid, l => l.Available);
            foreach (var store in stores)
                foreach (var variant in variantUuids.Distinct())
                    await _hub.StockUpdatedAsync(organizationId, store, variant, levels.GetValueOrDefault(variant));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "POS: stock push failed for organization {Org}.", organizationId);
        }
    }

    public async Task OnCustomerBalanceChangedAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default)
    {
        try
        {
            var customer = _customers is null ? null : await _customers.GetAsync(organizationId, customerUuid, ct);
            var balance = _balances is null ? 0m
                : (await _balances.GetAsync(organizationId, [customerUuid], ct)).GetValueOrDefault(customerUuid)?.Balance ?? 0m;
            await _hub.CustomerBalanceChangedAsync(organizationId, customerUuid, balance, customer?.CreditLimit ?? 0m);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "POS: customer balance push failed for organization {Org}.", organizationId);
        }
    }
}
