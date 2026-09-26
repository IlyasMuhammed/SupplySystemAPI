using System.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
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

    public FinishedGoodsReceiptService(
        MaterialDbContext db, InventoryDbContext inv, IInventoryLedgerService ledger, IAllocationEngine engine,
        IDocumentNumberGenerator numbers, IProductionOrderRepository orders, IBackgroundJobClient? jobs = null,
        IManufacturingNotificationService? notify = null)
    {
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

            fgr.Status   = FgrStatus.Confirmed;
            po.AcceptedQuantity += fgr.TotalQuantity;
            po.UpdatedAt = DateTime.UtcNow;
            // §19A.6 — a production order needs at least one credit before it can be Completed; this
            // receipt is that credit, and Completed only once everything QI accepted has arrived.
            if (po.AcceptedQuantity >= qi.AcceptedQuantity)
            {
                po.Status = ProductionOrderStatus.Completed;
                po.ActualEndDate ??= DateTime.UtcNow;
            }
        }, ct);

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
        await _engine.AllocateAsync(po.ProductVariantUuid, null, userId, ct);
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
