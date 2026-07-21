using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Warehouse.Repositories;

internal sealed class GrnRepository : IGrnRepository
{
    private readonly WarehouseDbContext  _wh;
    private readonly DemandDbContext     _demand;
    private readonly IAuditService       _audit;
    private const decimal OverReceiptTolerance = 1.03m;
    private const decimal VarianceThreshold    = 0.05m;

    public GrnRepository(WarehouseDbContext wh, DemandDbContext demand, IAuditService audit)
    {
        _wh     = wh;
        _demand = demand;
        _audit  = audit;
    }

    // Test-only convenience constructor — audit logging is a no-op.
    public GrnRepository(WarehouseDbContext wh, DemandDbContext demand)
    {
        _wh     = wh;
        _demand = demand;
        _audit  = new NoOpAuditService();
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(
            int? userId, string? userName, string module, string action, string entityType, Guid? entityId,
            string ipAddress = "", string? fieldChanged = null, string? oldValue = null, string? newValue = null,
            string? notes = null) => Task.CompletedTask;
    }

    // ── Create GRN from a SENT/PARTIALLY_RECEIVED PO ─────────────────────────

    public async Task<Guid> CreateAsync(CreateGrnRequest req, int createdBy)
    {
        var po = await _demand.PurchaseOrders
            .Include(p => p.Lines.OrderBy(l => l.LineNo))
            .FirstOrDefaultAsync(p => p.UUID == req.PoUuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", req.PoUuid);

        if (po.Status != "SENT" && po.Status != "PARTIALLY_RECEIVED")
            throw new UnprocessableEntityException(
                $"Purchase order must be in SENT or PARTIALLY_RECEIVED status to receive goods. Current status: {po.Status}.");

        var now       = DateTime.UtcNow;
        var grnNumber = await GenerateGrnNumberAsync(now.Year);

        // Addendum 5A: if no warehouse specified, infer from line effective warehouses.
        // When all pending lines share a single effective warehouse, use it automatically.
        var effectiveWarehouseUuid = req.WarehouseUuid;
        if (effectiveWarehouseUuid is null)
        {
            var distinctEffective = po.Lines
                .Where(l => l.Quantity - l.QtyReceived > 0)
                .Select(l => l.WarehouseId ?? po.DeliveryWarehouseId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
            if (distinctEffective.Count == 1)
                effectiveWarehouseUuid = distinctEffective[0];
        }

        var lines = new List<GrnLine>();
        int lineNo = 1;
        foreach (var poLine in po.Lines)
        {
            var pending = poLine.Quantity - poLine.QtyReceived;
            if (pending <= 0) continue;

            var lineInput = req.Lines?.FirstOrDefault(l => l.PoLineUuid == poLine.UUID);

            if (lineInput is not null && lineInput.QtyReceived > pending * OverReceiptTolerance)
                throw new BadRequestException(
                    $"Line '{poLine.ItemDescription}': quantity received ({lineInput.QtyReceived}) exceeds " +
                    $"over-receipt tolerance of 3% above ordered quantity ({pending}).");

            var qtyReceived = lineInput?.QtyReceived ?? 0;
            var qtyOrdered  = pending;

            lines.Add(new GrnLine
            {
                UUID            = Guid.NewGuid(),
                PoLineUuid      = poLine.UUID,
                ProductUuid     = poLine.ProductUuid,
                LineNo          = lineNo++,
                ItemDescription = poLine.ItemDescription,
                UnitOfMeasure   = poLine.UnitOfMeasure,
                QtyOrdered      = qtyOrdered,
                QtyReceived     = qtyReceived,
                QtyAccepted     = lineInput?.QtyAccepted ?? 0,
                QtyRejected     = lineInput?.QtyRejected ?? 0,
                RejectionReason = lineInput?.RejectionReason,
                BatchNumber     = lineInput?.BatchNumber,
                ExpiryDate      = lineInput?.ExpiryDate,
                UnitCost        = poLine.UnitPrice,
                HasVariance     = qtyReceived > 0 && qtyOrdered > 0 &&
                                  Math.Abs(qtyReceived - qtyOrdered) / qtyOrdered > VarianceThreshold
            });
        }

        var grn = new Grn
        {
            UUID               = Guid.NewGuid(),
            TraceId            = po.TraceId,
            GrnNumber          = grnNumber,
            PoUuid             = po.UUID,
            PoNumber           = po.PoNumber,
            SupplierId         = po.SupplierId,
            SupplierName       = po.SupplierName,
            WarehouseUuid      = effectiveWarehouseUuid,
            ReceivedAt         = req.ReceivedAt,
            DeliveryNoteNo     = req.DeliveryNoteNo,
            VehicleNo          = req.VehicleNo,
            DriverName         = req.DriverName,
            InvoiceNo          = req.InvoiceNo,
            Notes              = req.Notes,
            RequiresInspection = req.RequiresInspection,
            Status             = "DRAFT",
            ReceivedBy         = createdBy,
            IsActive           = true,
            CreatedBy          = createdBy,
            CreatedDate        = now,
            Lines              = lines
        };

        _wh.Grns.Add(grn);
        await _wh.SaveChangesAsync();
        await _audit.LogAsync(createdBy, null, "WAREHOUSE", "CREATE", "GRN", grn.UUID,
            notes: $"GRN {grn.GrnNumber} created from PO {po.PoNumber}");
        return grn.UUID;
    }

    // ── Update GRN header (DRAFT only) ───────────────────────────────────────

    public async Task UpdateAsync(Guid uuid, PatchGrnRequest req, int modifiedBy)
    {
        var grn = await _wh.Grns
            .FirstOrDefaultAsync(g => g.UUID == uuid && !g.IsDelete)
            ?? throw new NotFoundException("GRN", uuid);

        if (grn.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT GRNs can be edited. Current status: {grn.Status}.");

        if (req.WarehouseUuid.HasValue)      grn.WarehouseUuid      = req.WarehouseUuid.Value;
        if (req.ReceivedAt.HasValue)         grn.ReceivedAt         = req.ReceivedAt.Value;
        if (req.DeliveryNoteNo is not null)  grn.DeliveryNoteNo     = req.DeliveryNoteNo;
        if (req.VehicleNo      is not null)  grn.VehicleNo          = req.VehicleNo;
        if (req.DriverName     is not null)  grn.DriverName         = req.DriverName;
        if (req.InvoiceNo      is not null)  grn.InvoiceNo          = req.InvoiceNo;
        if (req.Notes          is not null)  grn.Notes              = req.Notes;
        if (req.RequiresInspection.HasValue) grn.RequiresInspection = req.RequiresInspection.Value;

        grn.ModifiedBy   = modifiedBy;
        grn.ModifiedDate = DateTime.UtcNow;
        await _wh.SaveChangesAsync();
        await _audit.LogAsync(modifiedBy, null, "WAREHOUSE", "UPDATE", "GRN", grn.UUID,
            notes: $"GRN {grn.GrnNumber} header updated");
    }

    // ── Delete GRN (DRAFT only, soft-delete) ─────────────────────────────────

    public async Task DeleteAsync(Guid uuid, int deletedBy)
    {
        var grn = await _wh.Grns
            .FirstOrDefaultAsync(g => g.UUID == uuid && !g.IsDelete)
            ?? throw new NotFoundException("GRN", uuid);

        if (grn.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT GRNs can be deleted. Current status: {grn.Status}.");

        grn.IsDelete     = true;
        grn.IsActive     = false;
        grn.ModifiedBy   = deletedBy;
        grn.ModifiedDate = DateTime.UtcNow;
        await _wh.SaveChangesAsync();
        await _audit.LogAsync(deletedBy, null, "WAREHOUSE", "DELETE", "GRN", grn.UUID,
            notes: $"GRN {grn.GrnNumber} deleted");
    }

    // ── Update a GRN line (record received, accepted, rejected quantities) ─────

    public async Task UpdateLineAsync(Guid grnUuid, Guid lineUuid, UpdateGrnLineRequest req, int modifiedBy)
    {
        var grn = await _wh.Grns
            .Include(g => g.Lines)
            .FirstOrDefaultAsync(g => g.UUID == grnUuid && !g.IsDelete)
            ?? throw new NotFoundException("GRN", grnUuid);

        if (grn.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"GRN lines can only be updated while in DRAFT status. Current status: {grn.Status}.");

        var line = grn.Lines.FirstOrDefault(l => l.UUID == lineUuid)
            ?? throw new NotFoundException("GrnLine", lineUuid);

        if (req.QtyReceived > line.QtyOrdered * OverReceiptTolerance)
            throw new BadRequestException(
                $"Quantity received ({req.QtyReceived}) exceeds over-receipt tolerance of 3% above ordered quantity ({line.QtyOrdered}).");

        if (req.ProductUuid.HasValue) line.ProductUuid = req.ProductUuid.Value;
        line.QtyReceived     = req.QtyReceived;
        line.QtyAccepted     = req.QtyAccepted;
        line.QtyRejected     = req.QtyRejected;
        line.RejectionReason = req.RejectionReason;
        line.BinUuid         = req.BinUuid;
        line.BatchNumber     = req.BatchNumber;
        line.ExpiryDate      = req.ExpiryDate;
        line.UnitCost        = req.UnitCost;
        line.QcResult        = req.QcResult;
        line.HasVariance     = line.QtyOrdered > 0 &&
            Math.Abs(req.QtyReceived - line.QtyOrdered) / line.QtyOrdered > VarianceThreshold;

        grn.ModifiedBy   = modifiedBy;
        grn.ModifiedDate = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(modifiedBy, null, "WAREHOUSE", "UPDATE", "GRN", grn.UUID,
            notes: $"GRN {grn.GrnNumber} line updated: {line.ItemDescription} qty={line.QtyReceived}");
    }

    // Narrow, single-purpose alternative to UpdateLineAsync: only ever touches ProductUuid, nothing
    // else on the line. Linking a line to its catalogue product doesn't affect quantities, cost, or
    // any already-recorded QC outcome, so — unlike UpdateLineAsync — this is allowed at any point
    // before the GRN reaches a terminal state, not just while still DRAFT. This exists because
    // "Cannot post stock: line not linked to a catalogue product" was otherwise only fixable by
    // rejecting and recreating the whole GRN once past DRAFT.
    public async Task LinkLineProductAsync(Guid grnUuid, Guid lineUuid, Guid productUuid, int modifiedBy)
    {
        var grn = await _wh.Grns
            .Include(g => g.Lines)
            .FirstOrDefaultAsync(g => g.UUID == grnUuid && !g.IsDelete)
            ?? throw new NotFoundException("GRN", grnUuid);

        if (grn.Status is "APPROVED" or "REJECTED")
            throw new UnprocessableEntityException(
                $"GRN lines can no longer be edited once the GRN is {grn.Status}.");

        var line = grn.Lines.FirstOrDefault(l => l.UUID == lineUuid)
            ?? throw new NotFoundException("GrnLine", lineUuid);

        line.ProductUuid = productUuid;
        grn.ModifiedBy   = modifiedBy;
        grn.ModifiedDate = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(modifiedBy, null, "WAREHOUSE", "UPDATE", "GRN", grn.UUID,
            notes: $"GRN {grn.GrnNumber} line '{line.ItemDescription}' linked to catalogue product {productUuid}.");
    }

    // ── Record formal inspection result per line (PENDING_QC only) ───────────

    public async Task InspectLineAsync(Guid grnUuid, Guid lineUuid, InspectGrnLineRequest req, int inspectedBy)
    {
        var grn = await _wh.Grns
            .Include(g => g.Lines)
            .FirstOrDefaultAsync(g => g.UUID == grnUuid && !g.IsDelete)
            ?? throw new NotFoundException("GRN", grnUuid);

        if (grn.Status != "PENDING_QC")
            throw new UnprocessableEntityException(
                $"Inspection can only be recorded while GRN is in PENDING_QC status. Current status: {grn.Status}.");

        var line = grn.Lines.FirstOrDefault(l => l.UUID == lineUuid)
            ?? throw new NotFoundException("GrnLine", lineUuid);

        // Resolve accepted/rejected quantities based on result, then validate.
        decimal accepted;
        decimal rejected;
        switch (req.InspectionResult)
        {
            case "Pass":
                accepted = line.QtyReceived;
                rejected = 0m;
                break;
            case "Fail":
                accepted = 0m;
                rejected = line.QtyReceived;
                break;
            case "PartialPass":
                accepted = req.QtyAccepted;
                rejected = req.QtyRejected;
                if (Math.Abs(accepted + rejected - line.QtyReceived) > 0.0001m)
                    throw new UnprocessableEntityException(
                        $"Partial Pass: Qty Accepted ({accepted}) + Qty Rejected ({rejected}) must equal Qty Received ({line.QtyReceived}).");
                break;
            default:
                throw new BadRequestException(
                    $"Invalid InspectionResult '{req.InspectionResult}'. Allowed values: Pass, Fail, PartialPass.");
        }

        if (rejected > 0 && string.IsNullOrWhiteSpace(req.RejectionReason))
            throw new UnprocessableEntityException(
                "Rejection Reason is required when any quantity is rejected.");

        line.QtyAccepted      = accepted;
        line.QtyRejected      = rejected;
        line.RejectionReason  = req.RejectionReason;
        line.InspectionResult = req.InspectionResult;
        line.InspectorRemarks = req.InspectorRemarks;
        line.InspectedBy      = inspectedBy;
        line.InspectedAt      = DateTime.UtcNow;

        // Transition InspectionCompletedAt when the last uninspected line is recorded
        var allInspected = grn.Lines.All(l => l.InspectionResult is not null);
        if (allInspected && grn.InspectionCompletedAt is null)
            grn.InspectionCompletedAt = DateTime.UtcNow;
        else if (!allInspected)
            grn.InspectionCompletedAt = null;

        grn.ModifiedBy   = inspectedBy;
        grn.ModifiedDate = DateTime.UtcNow;
        await _wh.SaveChangesAsync();
        await _audit.LogAsync(inspectedBy, null, "WAREHOUSE", "INSPECT", "GRN", grn.UUID,
            notes: $"GRN {grn.GrnNumber} line inspected: {line.ItemDescription} result={line.InspectionResult}");
    }

    // ── Submit GRN (validates DRAFT, sets IsPartialReceipt; status driven by workflow engine) ─

    public async Task SubmitAsync(Guid grnUuid, int modifiedBy)
    {
        var grn = await _wh.Grns
            .Include(g => g.Lines)
            .FirstOrDefaultAsync(g => g.UUID == grnUuid && !g.IsDelete)
            ?? throw new NotFoundException("GRN", grnUuid);

        if (grn.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT GRNs can be submitted. Current status: {grn.Status}.");

        grn.IsPartialReceipt = grn.Lines.Any(l => l.QtyReceived < l.QtyOrdered);
        grn.ModifiedBy       = modifiedBy;
        grn.ModifiedDate     = DateTime.UtcNow;

        await _wh.SaveChangesAsync();
        await _audit.LogAsync(modifiedBy, null, "WAREHOUSE", "SUBMIT", "GRN", grn.UUID,
            fieldChanged: "Status", oldValue: "DRAFT", newValue: "SUBMITTED",
            notes: $"GRN {grn.GrnNumber} submitted for approval");
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<GrnListItemModel>> GetListAsync(GrnListFilter filter)
    {
        var query = _wh.Grns.Where(g => !g.IsDelete).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(g => g.Status == filter.Status);
        if (filter.SupplierId.HasValue)
            query = query.Where(g => g.SupplierId == filter.SupplierId.Value);
        if (filter.PoUuid.HasValue)
            query = query.Where(g => g.PoUuid == filter.PoUuid.Value);
        if (filter.DateFrom.HasValue)
            query = query.Where(g => g.ReceivedAt >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(g => g.ReceivedAt <= filter.DateTo.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(g => g.GrnNumber.ToLower().Contains(term)
                                  || g.PoNumber.ToLower().Contains(term)
                                  || g.SupplierName.ToLower().Contains(term));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(g => g.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new GrnListItemModel
            {
                UUID                  = g.UUID,
                TraceId               = g.TraceId,
                GrnNumber             = g.GrnNumber,
                PoUuid                = g.PoUuid,
                PoNumber              = g.PoNumber,
                SupplierId            = g.SupplierId,
                SupplierName          = g.SupplierName,
                Status                = g.Status,
                IsPartialReceipt      = g.IsPartialReceipt,
                FinanceApprovalRequired = g.FinanceApprovalRequired,
                ReceivedAt          = g.ReceivedAt,
                CreatedDate           = g.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<GrnListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<GrnDetailModel?> GetByIdAsync(Guid uuid)
    {
        var grn = await _wh.Grns
            .Include(g => g.Lines.OrderBy(l => l.LineNo))
            .FirstOrDefaultAsync(g => g.UUID == uuid && !g.IsDelete);

        if (grn is null) return null;

        return new GrnDetailModel
        {
            UUID                  = grn.UUID,
            TraceId               = grn.TraceId,
            GrnNumber             = grn.GrnNumber,
            PoUuid                = grn.PoUuid,
            PoNumber              = grn.PoNumber,
            SupplierId            = grn.SupplierId,
            SupplierName          = grn.SupplierName,
            WarehouseUuid         = grn.WarehouseUuid,
            ReceivedAt          = grn.ReceivedAt,
            DeliveryNoteNo        = grn.DeliveryNoteNo,
            VehicleNo             = grn.VehicleNo,
            DriverName            = grn.DriverName,
            Status                = grn.Status,
            QcPassed              = grn.QcPassed
                                    || (grn.RequiresInspection
                                        && (grn.Status == "PENDING_APPROVAL" || grn.Status == "APPROVED")),
            QcNotes               = grn.QcNotes,
            QcDoneBy              = grn.QcDoneBy,
            QcConfirmedBy         = grn.QcConfirmedBy,
            QcConfirmedAt         = grn.QcConfirmedAt,
            FinanceApprovalRequired = grn.FinanceApprovalRequired,
            FinanceApprovedBy     = grn.FinanceApprovedBy,
            FinanceApprovedAt     = grn.FinanceApprovedAt,
            ReceivedBy            = grn.ReceivedBy,
            ApprovedBy            = grn.ApprovedBy,
            ApprovedAt            = grn.ApprovedAt,
            ApprovalDeadline      = grn.ApprovalDeadline,
            RejectionReason        = grn.RejectionReason,
            InvoiceNo              = grn.InvoiceNo,
            Notes                  = grn.Notes,
            RequiresInspection     = grn.RequiresInspection,
            InspectionCompletedAt  = grn.InspectionCompletedAt,
            InspectionComplete     = grn.Lines.Any() && grn.Lines.All(l => l.InspectionResult is not null),
            InspectedLineCount     = grn.Lines.Count(l => l.InspectionResult is not null),
            TotalLineCount         = grn.Lines.Count,
            IsPartialReceipt       = grn.IsPartialReceipt,
            CreatedBy              = grn.CreatedBy,
            CreatedDate            = grn.CreatedDate,
            Lines = grn.Lines.Select(l => new GrnLineModel
            {
                UUID             = l.UUID,
                PoLineUuid       = l.PoLineUuid,
                ProductUuid      = l.ProductUuid,
                LineNo           = l.LineNo,
                ItemDescription  = l.ItemDescription,
                UnitOfMeasure    = l.UnitOfMeasure,
                QtyOrdered       = l.QtyOrdered,
                QtyReceived      = l.QtyReceived,
                QtyAccepted      = l.QtyAccepted,
                QtyRejected      = l.QtyRejected,
                RejectionReason  = l.RejectionReason,
                BinUuid          = l.BinUuid,
                BatchNumber      = l.BatchNumber,
                ExpiryDate       = l.ExpiryDate,
                UnitCost         = l.UnitCost,
                HasVariance      = l.HasVariance,
                QcResult         = l.QcResult,
                InspectionResult = l.InspectionResult,
                InspectorRemarks = l.InspectorRemarks,
                InspectedBy      = l.InspectedBy,
                InspectedAt      = l.InspectedAt
            }).ToList()
        };
    }

    // ── Number generators ─────────────────────────────────────────────────────

    private async Task<string> GenerateGrnNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _wh.Grns
            .CountAsync(g => g.CreatedDate >= yearStart && g.CreatedDate < yearEnd);
        return $"GRN-{year}-{(count + 1):D5}";
    }

}
