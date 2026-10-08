using System.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A30 §19. Confirming credits the output variant's stock, then does what a purchase receipt already
/// does for a shortage it covers (A30-P3-09/-13): closes the expected supply this order may have
/// registered and lets the shared allocation engine assign the newly-arrived stock — to another
/// order's material requirement (chained manufacturing) or, once Phase 4 Track C registers them, a
/// sales order's own demand. FGR never hardcodes who gets the stock (§19.3's own rule).
/// <para>
/// A production order can be received in more than one FGR (partial production is normal); it moves
/// to Completed only once every accepted unit its one <see cref="QualityInspection"/> passed has
/// actually arrived — not, as a literal reading of §19.3 step 4 would have it, after the first
/// receipt regardless of how much of the accepted quantity that covered.
/// </para>
/// </summary>
internal sealed class FinishedGoodsReceiptService : IFinishedGoodsReceiptService
{
    private readonly MaterialDbContext          _db;
    private readonly InventoryDbContext         _inv;
    private readonly IInventoryLedgerService    _ledger;
    private readonly IAllocationEngine          _engine;
    private readonly IDocumentNumberGenerator   _numbers;
    private readonly IProductionOrderRepository _orders;
    private readonly IBackgroundJobClient?      _jobs;
    private readonly IManufacturingNotificationService? _notify;
    // A34 (all optional, like every DI-only dependency here): the make-to-order completion hook.
    private readonly DemandDbContext?               _demand;
    private readonly IStockReservationService?      _reservations;
    private readonly ISaleOrderDeliveryQuantities?  _deliveries;
    private readonly IProductionDeliveryHandoff?    _handoff;
    private readonly ILogger<FinishedGoodsReceiptService>? _log;
    // A34 D-30 — Finance's A29 product ledger; optional so a host without Finance still receives.
    private readonly IProductLedgerService?         _productLedger;

    public FinishedGoodsReceiptService(
        MaterialDbContext db, InventoryDbContext inv, IInventoryLedgerService ledger, IAllocationEngine engine,
        IDocumentNumberGenerator numbers, IProductionOrderRepository orders, IBackgroundJobClient? jobs = null,
        IManufacturingNotificationService? notify = null, DemandDbContext? demand = null,
        IStockReservationService? reservations = null, ISaleOrderDeliveryQuantities? deliveries = null,
        IProductionDeliveryHandoff? handoff = null, ILogger<FinishedGoodsReceiptService>? log = null,
        IProductLedgerService? productLedger = null)
    {
        _productLedger = productLedger;
        _demand       = demand;
        _reservations = reservations;
        _deliveries   = deliveries;
        _handoff      = handoff;
        _log          = log;
        _db      = db;
        _inv     = inv;
        _ledger  = ledger;
        _engine  = engine;
        _jobs    = jobs;
        _numbers = numbers;
        _orders  = orders;
        _notify  = notify;
    }

    public async Task<Guid> CreateAsync(Guid productionOrderUuid, CreateFinishedGoodsReceiptRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Quantity <= 0)
            throw new BadRequestException("Quantity must be greater than zero.");

        var po = await _orders.LoadAsync(productionOrderUuid);
        if (po.Status != ProductionOrderStatus.QualityInspection)
            throw new BadRequestException($"Production order {po.ProductionNumber} is {po.Status.ToLowerInvariant()}; only an order in quality inspection can receive finished goods.");

        var qi = await _db.QualityInspections.FirstOrDefaultAsync(q => q.ProductionOrderId == po.Id, ct)
            ?? throw new BadRequestException($"Production order {po.ProductionNumber} has not been inspected yet.");

        var alreadyReceived = await _db.FinishedGoodsReceipts.AsNoTracking()
            .Where(f => f.QualityInspectionId == qi.Id && f.Status != FgrStatus.Reversed)
            .SumAsync(f => (decimal?)f.TotalQuantity, ct) ?? 0m;
        var outstanding = qi.AcceptedQuantity - alreadyReceived;
        if (req.Quantity > outstanding)
            throw new BadRequestException($"Only {outstanding:0.####} of the accepted quantity is left to receive.");

        var warehouseUuid = req.WarehouseUuid ?? po.OutputWarehouseUuid ?? po.WarehouseUuid;
        if (!await _inv.Warehouses.AsNoTracking().AnyAsync(w => w.Uuid == warehouseUuid && w.IsActive, ct))
            throw new BadRequestException($"Warehouse {warehouseUuid} does not exist or is inactive.");

        var now = DateTime.UtcNow;
        var fgr = new FinishedGoodsReceipt
        {
            FgrNumber           = await _numbers.NextAsync(ManufacturingDocumentPrefix.FinishedGoodsReceipt, now),
            ProductionOrderId   = po.Id,
            QualityInspectionId = qi.Id,
            WarehouseUuid       = warehouseUuid,
            TotalQuantity       = req.Quantity,
            Status              = FgrStatus.Draft,
            ReceivedBy          = userId,
            ReceivedAt          = now,
            Notes               = req.Notes?.Trim(),
            CreatedAt           = now
        };
        _db.FinishedGoodsReceipts.Add(fgr);
        await _db.SaveChangesAsync(ct);

        if (req.Confirm) await ConfirmCoreAsync(fgr, po, qi, userId, ct);
        return fgr.UUID;
    }

    public async Task ConfirmAsync(Guid uuid, int userId, CancellationToken ct = default)
    {
        var fgr = await _db.FinishedGoodsReceipts.FirstOrDefaultAsync(f => f.UUID == uuid, ct)
            ?? throw new NotFoundException("Finished goods receipt", uuid);
        if (fgr.Status != FgrStatus.Draft)
            throw new BadRequestException($"Receipt {fgr.FgrNumber} is already {fgr.Status.ToLowerInvariant()}.");

        var po = await _orders.LoadAsync(fgr.ProductionOrderId);
        var qi = await _db.QualityInspections.FirstAsync(q => q.Id == fgr.QualityInspectionId, ct);
        await ConfirmCoreAsync(fgr, po, qi, userId, ct);
    }

    private async Task ConfirmCoreAsync(FinishedGoodsReceipt fgr, ProductionOrder po, QualityInspection qi, int userId, CancellationToken ct)
    {
        string productName = po.ProductUuid.ToString(), warehouseName = fgr.WarehouseUuid.ToString();
        // Decided before the cross-context work, which an execution strategy may run more than once.
        var completedBefore = po.Status == ProductionOrderStatus.Completed;

        await RunAcrossContextsAsync(async () =>
        {
            var variant = await _inv.ProductVariants.Include(v => v.Product).FirstOrDefaultAsync(v => v.Uuid == po.ProductVariantUuid, ct)
                ?? throw new BadRequestException("The output variant no longer exists.");
            var warehouse = await _inv.Warehouses.FirstOrDefaultAsync(w => w.Uuid == fgr.WarehouseUuid, ct)
                ?? throw new BadRequestException("The warehouse no longer exists.");
            productName   = variant.IsDefault ? variant.Product.Name : $"{variant.Product.Name} – {variant.VariantName}";
            warehouseName = warehouse.Name;

            var item = await _inv.InventoryItems.FirstOrDefaultAsync(i =>
                i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == null && i.SerialNumber == null, ct);
            if (item is null)
            {
                item = new InventoryItem { VariantId = variant.Id, WarehouseId = warehouse.Id, QtyOnHand = 0, QtyReserved = 0, QtyOnOrder = 0, LastUpdated = DateTime.UtcNow };
                _inv.InventoryItems.Add(item);
            }
            item.QtyOnHand  += fgr.TotalQuantity;
            item.LastUpdated = DateTime.UtcNow;

            await _ledger.CreateEntryAsync(new LedgerEntryCommand
            {
                VariantId       = variant.Id,
                WarehouseId     = warehouse.Id,
                TransactionType = InventoryTransactionType.FinishedGoodsReceipt,
                ReferenceType   = "FGR",
                ReferenceId     = fgr.UUID,
                ReferenceNumber = fgr.FgrNumber,
                QuantityIn      = fgr.TotalQuantity,
                UnitCost        = item.UnitCost ?? 0m,
                Notes           = $"Finished goods receipt {fgr.FgrNumber} for {po.ProductionNumber}",
                CreatedBy       = userId
            });

            // A34 D-30 — the A29 product ledger too, or a manufactured good's ledger holds nothing and its sales invoice
            // is refused. ADJUSTMENT / IN (the closed vocabulary has no production type) at the same unit cost as the
            // inventory ledger above, inside this receipt's transaction so it commits or rolls back with it. Every FGR
            // posts: make-to-order, A30 make-to-shortage and standalone alike. (No FGR reversal exists, so there is no
            // matching OUT; material consumption posts no OUT either — a recorded gap.)
            if (_productLedger is not null)
                await _productLedger.AppendEntryAsync(new ProductLedgerPosting(
                    po.ProductVariantUuid, ProductLedgerEntryTypes.Adjustment, ProductLedgerDirections.In, fgr.TotalQuantity,
                    decimal.Round(item.UnitCost ?? 0m, 4), "FGR", fgr.UUID, fgr.FgrNumber, userId,
                    ProductUuid: po.ProductUuid,
                    Narration: $"Finished goods receipt {fgr.FgrNumber} for {po.ProductionNumber}"),
                    _db.Database.IsRelational() ? _db.Database.CurrentTransaction?.GetDbTransaction() : null);

            fgr.Status   = FgrStatus.Confirmed;
            po.AcceptedQuantity += fgr.TotalQuantity;
            po.UpdatedAt = DateTime.UtcNow;
            // §19A.6 — a production order needs at least one credit before it can be Completed; this
            // receipt is that credit, and Completed only once everything QI accepted has arrived.
            if (po.AcceptedQuantity >= qi.AcceptedQuantity)
            {
                po.Status = ProductionOrderStatus.Completed;
                po.ActualEndDate ??= DateTime.UtcNow;
                // A34 D-20 — in this same commit, so a crash between here and the handoff below leaves the order
                // visible to ProductionDeliverySweepJob rather than silently without a delivery.
                if (!completedBefore && po.IsMakeToOrder) po.DeliveryCreationPendingSince = DateTime.UtcNow;
            }
        }, ct);
        var turnedCompleted = !completedBefore && po.Status == ProductionOrderStatus.Completed;

        // A30-P5-07 — on the order's own trace.
        _jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            po.TraceId,
            new TimelineEvent(ManufacturingTimelineEventTypes.FgrConfirmed, ManufacturingInterfaceCodes.ProductionOrder, fgr.UUID, fgr.FgrNumber, DateTime.UtcNow, userId,
                $"{fgr.TotalQuantity:0.####} received for {po.ProductionNumber}."),
            ManufacturingInterfaceCodes.ProductionOrder, po.ProductionNumber));

        if (_notify is not null)
        {
            await _notify.FinishedGoodsReceiptConfirmedAsync(po, fgr, productName, warehouseName);
            if (po.Status == ProductionOrderStatus.Completed) await _notify.ProductionOrderCompletedAsync(po);
        }

        // After the commit: close whatever expected supply this order registered (harmless no-op if
        // none — see SupplyRequirementEngine), then let the engine assign the newly-arrived stock.
        await _engine.SupplyReceivedAsync(AllocationSupplyType.ProductionOrder, po.UUID, null, fgr.TotalQuantity, ct);
        // A34 D-17a — a make-to-order line asks the engine for exactly what production has put into stock, just before
        // the run, so the run holds this output for the line (and free stock can never fill it beyond that).
        if (po.IsMakeToOrder) await RegisterMakeToOrderDemandAsync(po, fgr, userId, ct);
        await _engine.AllocateAsync(po.ProductVariantUuid, null, userId, ct);

        // A34 D-20 — the delivery of a make-to-order order, once it turns COMPLETED. Only here, after RunAcrossContextsAsync
        // cleared both contexts' transactions (A30 P4-21 bug 3): nothing is held, so Logistics can take the sale order lock
        // on its own connection. The handoff never throws; a failure stays pending for the sweep.
        if (turnedCompleted && po.IsMakeToOrder && _handoff is not null)
            await _handoff.RunAsync(po.UUID, userId, ct);
    }

    /// <summary>
    /// A34 D-17a (REV-01) — registers or extends the sale order line's SALES_ORDER allocation demand by this receipt, never
    /// beyond what the line still needs (its quantity less what was fulfilled, what the line already holds and what its
    /// deliveries hold). The demand's required quantity is cumulative and capped at what this order has received
    /// (<see cref="ProductionOrder.AcceptedQuantity"/>), so free stock can never fill the line beyond production's output.
    /// Only while the order is CONFIRMED / PARTIALLY_FULFILLED and the line is not cancelled, read with the PO's own
    /// organization. Best effort: the receipt is already committed, so a failure is logged and the delivery's release
    /// reserves at ship time instead.
    /// </summary>
    private async Task RegisterMakeToOrderDemandAsync(ProductionOrder po, FinishedGoodsReceipt fgr, int userId, CancellationToken ct)
    {
        if (_demand is null || po.SourceUuid is not { } soUuid || po.SourceLineUuid is not { } lineUuid) return;
        // REV-09 (R-11) — the engine stamps a new demand with the caller's tenant, not the PO's. A super admin confirming
        // another organization's receipt would leave the demand in the wrong organization (invisible to its owner's runs),
        // so register only in the PO's own organization; otherwise the delivery's release reserves at ship time instead.
        if (_db.TenantContext.OrganizationId != po.OrganizationId)
        {
            _log?.LogInformation("Receipt {FgrNumber} of {ProductionNumber} was confirmed from another organization; the sale order demand is left to the delivery's release.",
                fgr.FgrNumber, po.ProductionNumber);
            return;
        }
        try
        {
            var open = new[]
            {
                EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed),
                EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled)
            };
            var cancelledLine = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
            var line = await _demand.SaleOrderLines.IgnoreQueryFilters().AsNoTracking().Include(l => l.SaleOrder)
                .FirstOrDefaultAsync(l => l.UUID == lineUuid && l.OrganizationId == po.OrganizationId &&
                                          l.SaleOrder.UUID == soUuid && !l.SaleOrder.IsDeleted, ct);
            if (line is null || !open.Contains(line.SaleOrder.Status) || line.Status == cancelledLine) return;

            var existing = (await _engine.GetDemandsAsync(demandType: AllocationDemandType.SalesOrder, demandUuid: soUuid, ct: ct))
                .FirstOrDefault(d => d.DemandLineUuid == lineUuid);

            var lineHolds = 0m;
            if (_reservations is not null)
                lineHolds = (await _reservations.GetBySourceAsync(ReservationSourceType.SalesOrder, soUuid, ct))
                    .Where(r => r.SourceLineUuid == lineUuid && r.Status == "ACTIVE").Sum(r => r.ReservedQty);
            var deliveryHeld = 0m;
            if (_deliveries is not null)
                deliveryHeld = (await _deliveries.GetHeldBySoLineAsync(soUuid, ct)).GetValueOrDefault(lineUuid);

            var stillNeeded = Math.Max(0m, line.Quantity - line.FulfilledQty - lineHolds - deliveryHeld);
            var current     = existing?.RequiredQty ?? 0m;
            var required    = Math.Min(Math.Min(current + Math.Min(fgr.TotalQuantity, stillNeeded), po.AcceptedQuantity), line.Quantity);
            if (required <= current) return;

            await _engine.RegisterDemandAsync(new AllocationDemandRegistration(
                AllocationDemandType.SalesOrder, soUuid, lineUuid, line.SaleOrder.SoNumber, po.ProductVariantUuid,
                fgr.WarehouseUuid, required, po.RequiredDate, po.Priority), userId, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "The sale order demand for receipt {FgrNumber} of {ProductionNumber} could not be registered.",
                fgr.FgrNumber, po.ProductionNumber);
        }
    }

    public async Task<FinishedGoodsReceiptModel?> GetAsync(Guid uuid)
    {
        var fgr = await _db.FinishedGoodsReceipts.AsNoTracking().Include(f => f.ProductionOrder).Include(f => f.QualityInspection)
            .FirstOrDefaultAsync(f => f.UUID == uuid);
        if (fgr is null) return null;
        var names = await WarehouseNamesAsync([fgr.WarehouseUuid]);
        return ToModel(fgr, names);
    }

    public async Task<IReadOnlyList<FinishedGoodsReceiptModel>> GetForOrderAsync(Guid productionOrderUuid)
    {
        var rows = await _db.FinishedGoodsReceipts.AsNoTracking().Include(f => f.ProductionOrder).Include(f => f.QualityInspection)
            .Where(f => f.ProductionOrder.UUID == productionOrderUuid)
            .OrderByDescending(f => f.Id).ToListAsync();
        var names = await WarehouseNamesAsync(rows.Select(f => f.WarehouseUuid).Distinct().ToList());
        return rows.Select(f => ToModel(f, names)).ToList();
    }

    private async Task<Dictionary<Guid, string>> WarehouseNamesAsync(IReadOnlyList<Guid> uuids) =>
        uuids.Count == 0
            ? []
            : await _inv.Warehouses.AsNoTracking().Where(w => uuids.Contains(w.Uuid)).ToDictionaryAsync(w => w.Uuid, w => w.Name);

    private static FinishedGoodsReceiptModel ToModel(FinishedGoodsReceipt f, IReadOnlyDictionary<Guid, string> warehouseNames) => new()
    {
        UUID                  = f.UUID,
        FgrNumber             = f.FgrNumber,
        ProductionOrderUuid   = f.ProductionOrder.UUID,
        ProductionNumber      = f.ProductionOrder.ProductionNumber,
        QualityInspectionUuid = f.QualityInspection.UUID,
        WarehouseUuid         = f.WarehouseUuid,
        WarehouseName         = warehouseNames.GetValueOrDefault(f.WarehouseUuid, string.Empty),
        TotalQuantity         = f.TotalQuantity,
        Status                = f.Status,
        ReceivedBy            = f.ReceivedBy,
        ReceivedAt            = f.ReceivedAt,
        Notes                 = f.Notes
    };

    /// <summary>Same shape as ProductionMaterialIssueService's — shared connection when relational, plain calls otherwise (unit tests' InMemory provider).</summary>
    private async Task RunAcrossContextsAsync(Func<Task> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            await work();
            await _db.SaveChangesAsync(ct);
            await _inv.SaveChangesAsync(ct);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await _db.Database.OpenConnectionAsync(ct);
            var conn = _db.Database.GetDbConnection();
            await using var sqlTx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            _db.Database.UseTransaction(sqlTx);
            _inv.Database.SetDbConnection(conn);
            _inv.Database.UseTransaction(sqlTx);

            await work();

            await _db.SaveChangesAsync(ct);
            await _inv.SaveChangesAsync(ct);
            await sqlTx.CommitAsync(ct);

            // UseTransaction(sqlTx) above left both contexts' Database.CurrentTransaction pointing at
            // this transaction; neither is ever cleared automatically just because the underlying
            // sqlTx is committed/disposed by this method rather than through EF's own wrapper. Left
            // stale, it survives past this method's return for the rest of the request/job — the next
            // unrelated call against either context (e.g. ConfirmCoreAsync's own post-commit
            // AllocationEngine calls) then finds CurrentTransaction non-null and assumes it's safely
            // joining an already-active ambient transaction, skipping its own retry-safe
            // BeginTransactionAsync wrapping — which a retrying execution strategy then rejects.
            _db.Database.UseTransaction(null);
            _inv.Database.UseTransaction(null);
            await _db.Database.CloseConnectionAsync();
        });
    }
}
