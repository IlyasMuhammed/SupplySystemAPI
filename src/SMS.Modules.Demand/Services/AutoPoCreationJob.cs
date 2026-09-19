using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;

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

    public AutoPoCreationJob(
        DemandDbContext db, ISaleOrderConfigService config, ISupplierSelectionService selection,
        IAutoPurchaseOrderService autoPo, ISaleOrderEmailService email, ILogger<AutoPoCreationJob> log)
    {
        _db        = db;
        _config    = config;
        _selection = selection;
        _autoPo    = autoPo;
        _email     = email;
        _log       = log;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task CreateForDeficitAsync(Guid saleOrderUuid, Guid saleOrderLineUuid, int userId)
    {
        // §3.3 — "when auto_po_enabled and stock is short". Off means the supply team raises POs
        // themselves; nothing here.
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

        // What is short NOW, not what was short when the job was enqueued: stock may have arrived, or
        // a GRN reserved against it, in between.
        var deficit = line.DeficitQty ?? 0m;
        if (deficit <= 0m) return;

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
