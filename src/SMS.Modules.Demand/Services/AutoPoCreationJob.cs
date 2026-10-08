using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// §6.1's whole flow for one sale order line with a deficit, run as the Hangfire job
/// <c>SaleOrderService.ConfirmAsync</c> enqueues per such line: <b>deficit → supplier selection (per
/// mode) → price → create PO with status per approval mode → email intimation</b>. Everything it
/// decides is somebody else's job — <see cref="ISupplierSelectionService"/> picks the supplier (and
/// notifies the supply team when it cannot), <see cref="IAutoPurchaseOrderService"/> raises the PO —
/// so this is deliberately just the order they run in and the two checks that belong to neither:
/// whether the org wants auto-POs at all, and whether the line still needs one.
/// <para>
/// The price is the selected supplier's active rate — the top tier of §2.4's waterfall. A supplier
/// is only ever selected for having an active rate for the variant (DEFAULT_SUPPLIER requires one
/// too), so the lower tiers (last PO price, the variant's own purchase price) can never apply here.
/// </para>
/// </summary>
internal sealed class AutoPoCreationJob : IAutoPoCreationJob
{
    private readonly DemandDbContext _db;
    private readonly ISaleOrderConfigService _config;
    private readonly ISupplierSelectionService _selection;
    private readonly IAutoPurchaseOrderService _autoPo;
    private readonly ISaleOrderEmailService _email;
    private readonly ILogger<AutoPoCreationJob> _log;
    // A30 Phase 4 Track C / decision D1 — both optional the way PurchaseOrderRepository's own
    // InventoryDbContext is: production DI always supplies them (Inventory and Material both
    // register theirs), and a caller without either (this class's own pre-A30 unit tests) gets
    // exactly the old behaviour — no supply-method check, every deficit is a purchase — rather
    // than a null-reference failure.
    private readonly InventoryDbContext? _inv;
    private readonly ISaleOrderManufacturingService? _manufacturing;

    public AutoPoCreationJob(
        DemandDbContext db, ISaleOrderConfigService config, ISupplierSelectionService selection,
        IAutoPurchaseOrderService autoPo, ISaleOrderEmailService email, ILogger<AutoPoCreationJob> log,
        InventoryDbContext? inv = null, ISaleOrderManufacturingService? manufacturing = null)
    {
        _db            = db;
        _config        = config;
        _selection     = selection;
        _autoPo        = autoPo;
        _email         = email;
        _log           = log;
        _inv           = inv;
        _manufacturing = manufacturing;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task CreateForDeficitAsync(Guid saleOrderUuid, Guid saleOrderLineUuid, int userId)
    {
        // §3.3 — "when auto_po_enabled and stock is short". Off means the supply team raises POs
        // themselves; nothing here. The same flag gates the manufacturing path below — there is no
        // separate "auto-manufacture" setting, and turning auto off should mean the same thing
        // regardless of how the product is supplied: the supply/production team handles it by hand.
        var config = await _config.GetConfigAsync();
        if (!config.AutoPoEnabled)
        {
            _log.LogInformation("Auto-PO is disabled for this organization; sale order {So} line {Line} left for the supply team.",
                saleOrderUuid, saleOrderLineUuid);
            return;
        }

        var line = await _db.SaleOrderLines.AsNoTracking()
            .FirstOrDefaultAsync(l => l.UUID == saleOrderLineUuid && l.SaleOrder.UUID == saleOrderUuid);
        if (line is null)
        {
            _log.LogWarning("Auto-PO: sale order {So} line {Line} not found.", saleOrderUuid, saleOrderLineUuid);
            return;
        }

        // A34 D-2 — a make-to-order line's whole quantity is its production order's (D-1). Confirm never enqueues this job
        // for one; a replayed or hand-run job must not buy or make it a second time either.
        if (line.FulfillmentMode == EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.MakeToOrder))
        {
            _log.LogInformation("Auto-PO: sale order {So} line {Line} is made to order; its production order supplies it.",
                saleOrderUuid, saleOrderLineUuid);
            return;
        }

        // What is short NOW, not what was short when the job was enqueued: stock may have arrived, or
        // a GRN reserved against it, in between.
        var deficit = line.DeficitQty ?? 0m;
        if (deficit <= 0m) return;

        // A30 decision D1 — a manufactured product's deficit becomes a production order, never a
        // purchase order. AvailabilityCheckService does not know the difference (it only ever asks
        // "is there enough on the shelf"), so this is the one place that has to.
        if (_inv is not null && _manufacturing is not null)
        {
            var supplyMethod = await _inv.ProductVariants.AsNoTracking()
                .Where(v => v.Uuid == line.VariantUuid).Select(v => v.Product.SupplyMethod).FirstOrDefaultAsync();
            if (supplyMethod == SupplyMethod.Manufacture)
            {
                await _manufacturing.FulfillDeficitAsync(saleOrderUuid, saleOrderLineUuid, deficit, userId);
                return;
            }
        }

        var choice = await _selection.SelectAsync(line.VariantUuid, deficit, userId);
        if (choice.RequiresManualSelection || choice.SupplierId is not { } supplierId || choice.UnitPrice is not { } price)
            return; // MANUAL — SelectAsync has already told the supply team

        var result = await _autoPo.CreateFromSODeficitAsync(saleOrderLineUuid, supplierId, deficit, price, userId);
        if (result.AlreadyExisted) return; // a retry of work already done — its email went out the first time

        if (result.Source == PurchaseOrderSources.DropShip)
            await _email.SendDropShipAsync(saleOrderUuid);
        else
            await _email.SendPoCreatedAsync(saleOrderUuid, result.PoUuid);
    }
}
