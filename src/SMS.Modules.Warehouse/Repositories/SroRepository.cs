using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Warehouse.Repositories;

internal sealed class SroRepository : ISroRepository
{
    private readonly WarehouseDbContext      _wh;
    private readonly DemandDbContext         _demand;
    private readonly InventoryDbContext      _inv;
    private readonly IInventoryLedgerService _ledger;
    private readonly IBackgroundJobClient    _jobs;
    private readonly IAuditService           _audit;

    public SroRepository(
        WarehouseDbContext wh,
        DemandDbContext demand,
        InventoryDbContext inv,
        IInventoryLedgerService ledger,
        IBackgroundJobClient jobs,
        IAuditService audit)
    {
        _wh     = wh;
        _demand = demand;
        _inv    = inv;
        _ledger = ledger;
        _jobs   = jobs;
        _audit  = audit;
    }

    // ── Create manual SRO ─────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateSroRequest req, int createdBy)
    {
        var now    = DateTime.UtcNow;
        var number = await GenerateSroNumberAsync(now.Year);

        var supplierName = string.IsNullOrWhiteSpace(req.SupplierName)
            ? "Unknown Supplier"
            : req.SupplierName;

        // Optionally resolve GRN info
        string? grnNumber    = null;
        int?    grnId        = null;
        Guid?   grnUuid      = null;
        Guid?   warehouseUuid = req.WarehouseUuid;
        if (req.OriginalGrnId.HasValue)
        {
            var grn = await _wh.Grns
                .FirstOrDefaultAsync(g => g.UUID == req.OriginalGrnId.Value && !g.IsDelete);
            if (grn is not null)
            {
                grnId        = grn.Id;
                grnUuid      = grn.UUID;
                grnNumber    = grn.GrnNumber;
                // Prefer the GRN's warehouse over anything the caller supplied.
                warehouseUuid = grn.WarehouseUuid ?? warehouseUuid;
            }
        }

        // Optionally resolve PO info
        string? poNumber = null;
        if (req.OriginalPoId.HasValue)
        {
            var po = await _demand.PurchaseOrders
                .FirstOrDefaultAsync(p => p.UUID == req.OriginalPoId.Value && !p.IsDelete);
            poNumber = po?.PoNumber;
        }

        int lineNo = 1;
        var sro = new SupplierReturnOrder
        {
            UUID             = Guid.NewGuid(),
            ReturnNumber     = number,
            SroType          = req.SroType,
            SupplierId       = req.SupplierId,
            SupplierName     = supplierName,
            GrnId            = grnId,
            GrnUuid          = grnUuid,
            GrnNumber        = grnNumber,
            WarehouseUuid    = warehouseUuid,
            OriginalPoUuid   = req.OriginalPoId,
            OriginalPoNumber = poNumber,
            ReturnReason     = req.ReturnReason,
            ReturnReasonDetail = req.ReturnReasonDetail,
            Notes            = req.Notes,
            Status           = "DRAFT",
            IsActive         = true,
            CreatedBy        = createdBy,
            CreatedDate      = now,
            Lines = req.Lines.Select(l => new SupplierReturnOrderLine
            {
                UUID              = Guid.NewGuid(),
                LineNo            = lineNo++,
                GrnLineUuid       = l.GrnLineUuid,
                PoLineUuid        = l.PoLineUuid,
                ProductUuid       = l.ProductUuid,
                ItemDescription   = l.ItemDescription,
                UnitOfMeasure     = l.UnitOfMeasure,
                QtyToReturn       = l.QtyToReturn,
                ReturnReason      = l.ReturnReason,
                ReturnReasonDetail= l.ReturnReasonDetail,
                Condition         = l.Condition,
                UnitCost          = l.UnitCost
            }).ToList()
        };

        _wh.SupplierReturnOrders.Add(sro);
        await _wh.SaveChangesAsync();
        await _audit.LogAsync(createdBy, null, "WAREHOUSE", "CREATE", "SRO", sro.UUID,
            notes: $"SRO {sro.ReturnNumber} created — supplier: {sro.SupplierName}, reason: {sro.ReturnReason}");
        return sro.UUID;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<SroListItemModel>> GetListAsync(SroListFilter filter)
    {
        var query = _wh.SupplierReturnOrders
            .Where(s => s.IsActive)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(s => s.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.SroType))
            query = query.Where(s => s.SroType == filter.SroType);
        if (filter.SupplierId.HasValue)
            query = query.Where(s => s.SupplierId == filter.SupplierId.Value);
        if (filter.DateFrom.HasValue)
            query = query.Where(s => s.CreatedDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(s => s.CreatedDate <= filter.DateTo.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(s =>
                s.ReturnNumber.ToLower().Contains(term) ||
                s.SupplierName.ToLower().Contains(term) ||
                (s.GrnNumber != null && s.GrnNumber.ToLower().Contains(term)));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(s => s.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SroListItemModel
            {
                UUID             = s.UUID,
                SroNumber        = s.ReturnNumber,
                SroType          = s.SroType,
                SupplierId       = s.SupplierId,
                SupplierName     = s.SupplierName,
                OriginalGrnId    = s.GrnUuid,
                OriginalGrnNumber= s.GrnNumber,
                OriginalPoId     = s.OriginalPoUuid,
                OriginalPoNumber = s.OriginalPoNumber,
                Status           = s.Status,
                ReturnReason     = s.ReturnReason,
                RmaNumber        = s.RmaNumber,
                DispatchDate     = s.DispatchDate,
                CreatedDate      = s.CreatedDate,
                TotalLines       = s.Lines.Count,
                Products         = string.Join(", ",
                    s.Lines.OrderBy(l => l.LineNo).Take(3).Select(l => l.ItemDescription))
                    + (s.Lines.Count > 3 ? $" +{s.Lines.Count - 3} more" : "")
            })
            .ToListAsync();

        return new PaginatedResponse<SroListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Get by ID ─────────────────────────────────────────────────────────────

    public async Task<SroDetailModel?> GetByIdAsync(Guid uuid)
    {
        var sro = await _wh.SupplierReturnOrders
            .Include(s => s.Lines.OrderBy(l => l.LineNo))
            .FirstOrDefaultAsync(s => s.UUID == uuid && s.IsActive);

        if (sro is null) return null;

        return MapToDetail(sro);
    }

    // ── Approve (DRAFT → APPROVED) ────────────────────────────────────────────

    public async Task ApproveAsync(Guid uuid, ApproveSroRequest req, int userId)
    {
        var sro = await FindAsync(uuid);

        if (sro.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"SRO must be in DRAFT status to approve. Current status: {sro.Status}.");

        sro.Status      = "APPROVED";
        sro.ApprovedBy  = userId;
        sro.ApprovedAt  = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req.Notes))
            sro.Notes = req.Notes;
        sro.ModifiedBy   = userId;
        sro.ModifiedDate = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(userId, null, "WAREHOUSE", "APPROVE", "SRO", sro.UUID,
            fieldChanged: "Status", oldValue: "DRAFT", newValue: "APPROVED",
            notes: req.Notes);
    }

    // ── Reject (DRAFT → REJECTED) ─────────────────────────────────────────────

    public async Task RejectAsync(Guid uuid, RejectSroRequest req, int userId)
    {
        var sro = await FindAsync(uuid);

        if (sro.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"SRO must be in DRAFT status to reject. Current status: {sro.Status}.");

        sro.Status          = "REJECTED";
        sro.RejectionReason = req.Reason;
        sro.ModifiedBy      = userId;
        sro.ModifiedDate    = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(userId, null, "WAREHOUSE", "REJECT", "SRO", sro.UUID,
            fieldChanged: "Status", oldValue: "DRAFT", newValue: "REJECTED",
            notes: req.Reason);
    }

    // ── Dispatch (APPROVED → DISPATCHED) ─────────────────────────────────────

    public async Task DispatchAsync(Guid uuid, DispatchSroRequest req, int userId)
    {
        var sro = await FindAsync(uuid);

        if (sro.Status != "APPROVED")
            throw new UnprocessableEntityException(
                $"SRO must be in APPROVED status to dispatch. Current status: {sro.Status}.");

        // 1. Decrement inventory (within its own transaction)
        if (sro.WarehouseUuid.HasValue)
        {
            var warehouse = await _inv.Warehouses
                .FirstOrDefaultAsync(w => w.Uuid == sro.WarehouseUuid.Value);

            if (warehouse is not null)
            {
                var strategy = _inv.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await _inv.Database.BeginTransactionAsync();

                    foreach (var line in sro.Lines.Where(l => l.ProductUuid.HasValue && l.QtyToReturn > 0))
                    {
                        var product = await _inv.Products
                            .FirstOrDefaultAsync(p => p.Uuid == line.ProductUuid!.Value);
                        if (product is null) continue;

                        var item = await _inv.InventoryItems
                            .FirstOrDefaultAsync(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id);
                        if (item is null) continue;

                        item.QtyOnHand  -= line.QtyToReturn;
                        item.LastUpdated = DateTime.UtcNow;

                        await _ledger.CreateEntryAsync(new LedgerEntryCommand
                        {
                            ProductId       = product.Id,
                            WarehouseId     = warehouse.Id,
                            TransactionType = "RETURN_DISPATCH",
                            ReferenceType   = "SRO",
                            ReferenceId     = sro.UUID,
                            ReferenceNumber = sro.ReturnNumber,
                            QuantityOut     = line.QtyToReturn,
                            UnitCost        = line.UnitCost ?? 0m,
                            Notes           = $"Supplier return dispatch: {line.ItemDescription}",
                            CreatedBy       = userId,
                            // FSD Addendum 24 (ML-003): stock leaves this warehouse back to the supplier.
                            SourceType      = "WAREHOUSE",
                            SourceName      = warehouse.Name,
                            DestinationType = "SUPPLIER",
                            DestinationName = sro.SupplierName
                        }, tx);
                    }

                    await _inv.SaveChangesAsync();
                    await tx.CommitAsync();
                });
            }
        }

        // 2. Reverse POLine.QtyReceived and recalculate PO status
        await ReversePOLineQuantitiesAsync(sro);

        // 3. Mark SRO dispatched with SLA deadline
        sro.Status              = "DISPATCHED";
        sro.RmaNumber           = req.RmaNumber;
        sro.DispatchDate        = req.DispatchDate;
        sro.DispatchCarrier     = req.DispatchCarrier;
        sro.DispatchTrackingRef = req.DispatchTrackingRef;
        sro.SlaDeadline         = req.DispatchDate.AddDays(14);
        sro.ModifiedBy          = userId;
        sro.ModifiedDate        = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(userId, null, "WAREHOUSE", "DISPATCH", "SRO", sro.UUID,
            fieldChanged: "Status", oldValue: "APPROVED", newValue: "DISPATCHED",
            notes: $"Carrier: {req.DispatchCarrier}, Tracking: {req.DispatchTrackingRef}, RMA: {req.RmaNumber}");

        // 4. Schedule SLA escalation job to fire in 14 days
        _jobs.Schedule<ISroEscalationJob>(
            j => j.EscalateIfOverdue(sro.UUID),
            TimeSpan.FromDays(14));
    }

    // Reverses POLine.QtyReceived for each SRO line that traces back to a GRN line,
    // then recalculates the PO header status based on the updated line quantities.
    private async Task ReversePOLineQuantitiesAsync(SupplierReturnOrder sro)
    {
        var grnLineUuids = sro.Lines
            .Where(l => l.GrnLineUuid.HasValue)
            .Select(l => l.GrnLineUuid!.Value)
            .Distinct()
            .ToList();

        if (grnLineUuids.Count == 0) return;

        // Resolve GRN lines to get their PoLineUuid mappings
        var grnLines = await _wh.GrnLines
            .Where(l => grnLineUuids.Contains(l.UUID))
            .ToListAsync();

        // Build a list of (PoLineUuid, QtyToReturn) reversals
        var reversals = sro.Lines
            .Where(l => l.GrnLineUuid.HasValue)
            .Select(l => new
            {
                GrnLine     = grnLines.FirstOrDefault(g => g.UUID == l.GrnLineUuid!.Value),
                QtyToReturn = l.QtyToReturn
            })
            .Where(x => x.GrnLine is not null)
            .Select(x => (PoLineUuid: x.GrnLine!.PoLineUuid, x.QtyToReturn))
            .ToList();

        if (reversals.Count == 0) return;

        var poLineUuids = reversals.Select(r => r.PoLineUuid).Distinct().ToList();

        // Load the affected PO lines
        var poLines = await _demand.PurchaseOrderLines
            .Where(l => poLineUuids.Contains(l.UUID))
            .ToListAsync();

        foreach (var (poLineUuid, qtyToReturn) in reversals)
        {
            var poLine = poLines.FirstOrDefault(l => l.UUID == poLineUuid);
            if (poLine is null) continue;
            poLine.QtyReceived = Math.Max(0m, poLine.QtyReceived - qtyToReturn);
        }

        // Recalculate PO header status for each affected PO
        var affectedPoIds = poLines.Select(l => l.PurchaseOrderId).Distinct().ToList();
        var affectedPos   = await _demand.PurchaseOrders
            .Include(p => p.Lines)
            .Where(p => affectedPoIds.Contains(p.Id) && !p.IsDelete)
            .ToListAsync();

        foreach (var po in affectedPos)
        {
            po.Status = po.Lines.Count > 0 && po.Lines.All(l => l.QtyReceived >= l.Quantity)
                ? "RECEIVED"
                : "PARTIALLY_RECEIVED";
            po.ModifiedDate = DateTime.UtcNow;
        }

        await _demand.SaveChangesAsync();
    }

    // ── Confirm Receipt (DISPATCHED → SUPPLIER_RECEIVED) ─────────────────────

    public async Task ConfirmReceiptAsync(Guid uuid, ConfirmReceiptSroRequest req, int userId)
    {
        var sro = await FindAsync(uuid);

        if (sro.Status != "DISPATCHED")
            throw new UnprocessableEntityException(
                $"SRO must be in DISPATCHED status to confirm receipt. Current status: {sro.Status}.");

        sro.Status = "SUPPLIER_RECEIVED";
        if (!string.IsNullOrWhiteSpace(req.Notes))
            sro.Notes = req.Notes;
        sro.ModifiedBy   = userId;
        sro.ModifiedDate = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(userId, null, "WAREHOUSE", "CONFIRM_RECEIPT", "SRO", sro.UUID,
            fieldChanged: "Status", oldValue: "DISPATCHED", newValue: "SUPPLIER_RECEIVED",
            notes: req.Notes);

        // Schedule a second escalation window: if not resolved within 14 days of supplier receipt, escalate
        _jobs.Schedule<ISroEscalationJob>(
            j => j.EscalateIfOverdue(sro.UUID),
            TimeSpan.FromDays(14));
    }

    // ── Resolve (SUPPLIER_RECEIVED | AWAITING_REPLACEMENT → RESOLVED_*) ───────

    public async Task ResolveAsync(Guid uuid, ResolveSroRequest req, int userId)
    {
        var sro = await FindAsync(uuid);

        if (sro.Status != "SUPPLIER_RECEIVED" && sro.Status != "AWAITING_REPLACEMENT")
            throw new UnprocessableEntityException(
                $"SRO must be in SUPPLIER_RECEIVED or AWAITING_REPLACEMENT status to resolve. Current status: {sro.Status}.");

        // CREDIT and DEBIT are deliberately not accepted here — they must go through
        // POST /api/credit-notes or POST /api/debit-notes, which actually create the note,
        // post the ledger entry, and reduce the linked invoice. This endpoint only ever
        // updates the SRO's own status/audit fields, so allowing those types here used to
        // give a false impression that a financial adjustment had happened.
        var fromStatus     = sro.Status;
        sro.ResolutionType = req.ResolutionType;
        sro.ResolvedAt     = DateTime.UtcNow;
        sro.Status         = req.ResolutionType switch
        {
            "REPLACEMENT" => "RESOLVED_REPLACEMENT",
            "CREDIT" or "DEBIT" => throw new BadRequestException(
                $"Resolution type '{req.ResolutionType}' must be raised via POST /api/{req.ResolutionType.ToLowerInvariant()}-notes, not this endpoint."),
            _             => throw new BadRequestException($"Unknown resolution type: {req.ResolutionType}")
        };
        if (!string.IsNullOrWhiteSpace(req.Notes))
            sro.Notes = req.Notes;
        sro.ModifiedBy   = userId;
        sro.ModifiedDate = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(userId, null, "WAREHOUSE", "RESOLVE", "SRO", sro.UUID,
            fieldChanged: "Status", oldValue: fromStatus, newValue: sro.Status,
            notes: $"Resolution type: {req.ResolutionType}. {req.Notes}");

        // Decrement supplier rating asynchronously via Hangfire
        _jobs.Enqueue<ISupplierRatingJob>(j =>
            j.DecrementRatingAsync(sro.SupplierId, sro.ReturnReason, req.ResolutionType));
    }

    // ── Escalate → ESCALATED ─────────────────────────────────────────────────

    public async Task EscalateAsync(Guid uuid, EscalateSroRequest req, int userId)
    {
        var sro = await FindAsync(uuid);

        var preEscalateStatus = sro.Status;
        sro.Status          = "ESCALATED";
        sro.RejectionReason = req.Reason;
        sro.ModifiedBy      = userId;
        sro.ModifiedDate    = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(userId, null, "WAREHOUSE", "ESCALATE", "SRO", sro.UUID,
            fieldChanged: "Status", oldValue: preEscalateStatus, newValue: "ESCALATED",
            notes: req.Reason);
    }

    // ── Expect Replacement (SUPPLIER_RECEIVED → AWAITING_REPLACEMENT) ─────────

    public async Task ExpectReplacementAsync(Guid uuid, int userId)
    {
        var sro = await FindAsync(uuid);

        if (sro.Status != "SUPPLIER_RECEIVED")
            throw new UnprocessableEntityException(
                $"SRO must be in SUPPLIER_RECEIVED status to expect replacement. Current status: {sro.Status}.");

        sro.Status       = "AWAITING_REPLACEMENT";
        sro.ModifiedBy   = userId;
        sro.ModifiedDate = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(userId, null, "WAREHOUSE", "EXPECT_REPLACEMENT", "SRO", sro.UUID,
            fieldChanged: "Status", oldValue: "SUPPLIER_RECEIVED", newValue: "AWAITING_REPLACEMENT");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<SupplierReturnOrder> FindAsync(Guid uuid)
    {
        return await _wh.SupplierReturnOrders
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.UUID == uuid && s.IsActive)
            ?? throw new NotFoundException("SupplierReturnOrder", uuid);
    }

    private static SroDetailModel MapToDetail(SupplierReturnOrder sro) => new()
    {
        UUID             = sro.UUID,
        SroNumber        = sro.ReturnNumber,
        SroType          = sro.SroType,
        SupplierId       = sro.SupplierId,
        SupplierName     = sro.SupplierName,
        OriginalGrnId    = sro.GrnUuid,
        OriginalGrnNumber= sro.GrnNumber,
        OriginalPoId     = sro.OriginalPoUuid,
        OriginalPoNumber = sro.OriginalPoNumber,
        Status           = sro.Status,
        ReturnReason     = sro.ReturnReason,
        ReturnReasonDetail = sro.ReturnReasonDetail,
        RmaNumber        = sro.RmaNumber,
        DispatchDate     = sro.DispatchDate,
        DispatchCarrier  = sro.DispatchCarrier,
        DispatchTrackingRef = sro.DispatchTrackingRef,
        ResolutionType   = sro.ResolutionType,
        ResolvedAt       = sro.ResolvedAt,
        ApprovedBy       = sro.ApprovedBy,
        ApprovedAt       = sro.ApprovedAt,
        RejectionReason  = sro.RejectionReason,
        SlaDeadline      = sro.SlaDeadline,
        Notes            = sro.Notes,
        CreatedBy        = sro.CreatedBy,
        CreatedDate      = sro.CreatedDate,
        Lines = sro.Lines.Select(l => new SroLineModel
        {
            UUID              = l.UUID,
            LineNo            = l.LineNo,
            GrnLineUuid       = l.GrnLineUuid,
            ProductUuid       = l.ProductUuid,
            ItemDescription   = l.ItemDescription,
            UnitOfMeasure     = l.UnitOfMeasure,
            QtyToReturn       = l.QtyToReturn,
            ReturnReason      = l.ReturnReason,
            ReturnReasonDetail= l.ReturnReasonDetail,
            Condition         = l.Condition,
            UnitCost          = l.UnitCost
        }).ToList()
    };

    private async Task<string> GenerateSroNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _wh.SupplierReturnOrders
            .CountAsync(s => s.CreatedDate >= yearStart && s.CreatedDate < yearEnd);
        return $"SRO-{year}-{(count + 1):D5}";
    }
}
