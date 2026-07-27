using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Repositories;

internal sealed class PurchaseOrderRepository : IPurchaseOrderRepository
{
    private readonly DemandDbContext _db;
    private readonly ILogger<PurchaseOrderRepository> _logger;

    public PurchaseOrderRepository(DemandDbContext db, ILogger<PurchaseOrderRepository> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── Single-vendor conversion: all PR lines → one PO ──────────────────────

    public async Task<Guid> CreateFromPrAsync(Guid prUuid, ConvertPrToPoRequest req, int createdBy)
    {
        var pr = await _db.PurchaseRequisitions
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == prUuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseRequisition", prUuid);

        if (pr.Status != "APPROVED" && pr.Status != "PARTIALLY_CONVERTED")
            throw new UnprocessableEntityException(
                $"Only APPROVED requisitions can be converted to PO. Current status: {pr.Status}.");

        ValidateQuotationRequirements(pr.Lines);

        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now.Year);

        var poLines = new List<PurchaseOrderLine>();
        int lineNo  = 1;
        foreach (var prLine in pr.Lines.OrderBy(l => l.LineNo))
        {
            poLines.Add(new PurchaseOrderLine
            {
                UUID             = Guid.NewGuid(),
                LineNo           = lineNo++,
                SourcePrLineUuid = prLine.UUID,
                ProductUuid      = prLine.ProductId,
                ItemDescription  = prLine.ItemDescription,
                Specification    = prLine.Specification,
                UnitOfMeasure    = prLine.UnitOfMeasure,
                Quantity         = prLine.Quantity,
                UnitPrice        = prLine.EstimatedUnitPrice,
                LineTotal        = prLine.LineTotal,
                RequiredDate     = prLine.RequiredDate,
                BudgetCode       = prLine.BudgetCode
            });
            prLine.LineStatus = "FULLY_CONVERTED";
        }

        var po = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            TraceId      = pr.TraceId,
            PoNumber     = poNumber,
            Title        = pr.PrTitle,
            SupplierId   = req.SupplierId,
            SupplierName = req.SupplierName,
            Status       = "DRAFT",
            TotalAmount  = poLines.Sum(l => l.LineTotal),
            DeliveryDate = req.DeliveryDate,
            Notes               = req.Notes,
            DeliveryWarehouseId = pr.WarehouseUuid,
            IsActive            = true,
            CreatedBy    = createdBy,
            CreatedDate  = now,
            Lines        = poLines,
            PrLinks      = [new PurchaseOrderPrLink { PrUuid = prUuid }]
        };

        _db.PurchaseOrders.Add(po);

        pr.Status       = "FULLY_CONVERTED";
        pr.ModifiedBy   = createdBy;
        pr.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return po.UUID;
    }

    // ── Split-by-vendor conversion: each vendor gets their own PO ─────────────

    public async Task<List<Guid>> CreateFromPrSplitAsync(Guid prUuid, ConvertPrSplitRequest req, int createdBy)
    {
        var pr = await _db.PurchaseRequisitions
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == prUuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseRequisition", prUuid);

        if (pr.Status != "APPROVED" && pr.Status != "PARTIALLY_CONVERTED")
            throw new UnprocessableEntityException(
                $"Only APPROVED requisitions can be converted to PO. Current status: {pr.Status}.");

        var lineMap = pr.Lines.ToDictionary(l => l.UUID);

        var assignments = req.Lines
            .Where(a => lineMap.ContainsKey(a.PrLineUuid))
            .Select(a => (Assignment: a, PrLine: lineMap[a.PrLineUuid]))
            .ToList();

        if (assignments.Count == 0)
            throw new BadRequestException("No valid PR lines were specified for conversion.");

        ValidateQuotationRequirements(assignments.Select(x => x.PrLine));

        var grouped    = assignments.GroupBy(x => x.Assignment.SupplierId).ToList();
        var now        = DateTime.UtcNow;
        var poNumbers  = await GeneratePoNumbersAsync(now.Year, grouped.Count);
        var poUuids    = new List<Guid>();
        int poIndex    = 0;

        foreach (var group in grouped)
        {
            var firstAssignment = group.First().Assignment;
            var poLines         = new List<PurchaseOrderLine>();
            int lineNo          = 1;

            foreach (var (_, prLine) in group)
            {
                poLines.Add(new PurchaseOrderLine
                {
                    UUID             = Guid.NewGuid(),
                    LineNo           = lineNo++,
                    SourcePrLineUuid = prLine.UUID,
                    ProductUuid      = prLine.ProductId,
                    ItemDescription  = prLine.ItemDescription,
                    Specification    = prLine.Specification,
                    UnitOfMeasure    = prLine.UnitOfMeasure,
                    Quantity         = prLine.Quantity,
                    UnitPrice        = prLine.EstimatedUnitPrice,
                    LineTotal        = prLine.LineTotal,
                    RequiredDate     = prLine.RequiredDate,
                    BudgetCode       = prLine.BudgetCode
                });
                prLine.LineStatus = "FULLY_CONVERTED";
            }

            var po = new PurchaseOrder
            {
                UUID         = Guid.NewGuid(),
                TraceId      = pr.TraceId,
                PoNumber     = poNumbers[poIndex++],
                Title        = pr.PrTitle,
                SupplierId   = group.Key,
                SupplierName = firstAssignment.SupplierName,
                Status       = "DRAFT",
                TotalAmount  = poLines.Sum(l => l.LineTotal),
                DeliveryDate = req.DeliveryDate,
                Notes        = req.Notes,
                IsActive     = true,
                CreatedBy    = createdBy,
                CreatedDate  = now,
                Lines        = poLines,
                PrLinks      = [new PurchaseOrderPrLink { PrUuid = prUuid }]
            };

            _db.PurchaseOrders.Add(po);
            poUuids.Add(po.UUID);
        }

        pr.Status = pr.Lines.All(l => l.LineStatus == "FULLY_CONVERTED")
            ? "FULLY_CONVERTED"
            : "PARTIALLY_CONVERTED";
        pr.ModifiedBy   = createdBy;
        pr.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return poUuids;
    }

    // ── Manual PO creation (with optional multi-PR consolidation) ─────────────

    public async Task<Guid> CreateAsync(CreatePoRequest req, int createdBy)
    {
        var now      = DateTime.UtcNow;
        var poNumber = await GeneratePoNumberAsync(now.Year);

        var po = new PurchaseOrder
        {
            UUID                  = req.PoUuid is { } gid && gid != Guid.Empty ? gid : Guid.NewGuid(),
            TraceId               = Guid.NewGuid(),
            PoNumber              = poNumber,
            Title                 = req.Title,
            SupplierId            = req.SupplierId,
            SupplierName          = req.SupplierName,
            Status                = "DRAFT",
            DeliveryDate          = req.DeliveryDate,
            DeliveryWarehouseId   = req.DeliveryWarehouseId,
            DeliveryWarehouseName = req.DeliveryWarehouseName,
            Notes                 = req.Notes,
            InternalNotes         = req.InternalNotes,
            IsActive              = true,
            CreatedBy             = createdBy,
            CreatedDate           = now
        };

        if (req.PrIds?.Count > 0)
        {
            var prs = await _db.PurchaseRequisitions
                .Include(p => p.Lines)
                .Where(p => req.PrIds.Contains(p.UUID) && !p.IsDelete)
                .ToListAsync();

            var invalidPrs = prs
                .Where(p => p.Status != "APPROVED" && p.Status != "PARTIALLY_CONVERTED")
                .Select(p => p.PrNumber)
                .ToList();
            if (invalidPrs.Count > 0)
                throw new UnprocessableEntityException(
                    $"Requisitions must be APPROVED before conversion: {string.Join(", ", invalidPrs)}");

            // TL-006: inherit trace_id from the first linked PR (request order); warn if the
            // consolidated PRs don't all belong to the same trace chain.
            var orderedPrs = req.PrIds
                .Select(id => prs.FirstOrDefault(p => p.UUID == id))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();
            po.TraceId = orderedPrs[0].TraceId;
            var distinctTraceIds = orderedPrs.Select(p => p.TraceId).Distinct().ToList();
            if (distinctTraceIds.Count > 1)
                _logger.LogWarning(
                    "Manual PO consolidates {Count} purchase requisitions spanning {ChainCount} different trace chains: {TraceIds}. Using the first PR's trace_id {TraceId}.",
                    orderedPrs.Count, distinctTraceIds.Count, string.Join(", ", distinctTraceIds), po.TraceId);

            foreach (var pr in prs)
                ValidateQuotationRequirements(pr.Lines);

            int lineNo = 1;
            foreach (var pr in prs)
            {
                foreach (var prLine in pr.Lines.OrderBy(l => l.LineNo))
                {
                    po.Lines.Add(new PurchaseOrderLine
                    {
                        UUID             = Guid.NewGuid(),
                        LineNo           = lineNo++,
                        SourcePrLineUuid = prLine.UUID,
                        ProductUuid      = prLine.ProductId,
                        ItemDescription  = prLine.ItemDescription,
                        Specification    = prLine.Specification,
                        UnitOfMeasure    = prLine.UnitOfMeasure,
                        Quantity         = prLine.Quantity,
                        UnitPrice        = prLine.EstimatedUnitPrice,
                        LineTotal        = prLine.LineTotal,
                        RequiredDate     = prLine.RequiredDate,
                        BudgetCode       = prLine.BudgetCode
                    });
                    prLine.LineStatus = "FULLY_CONVERTED";
                }

                po.PrLinks.Add(new PurchaseOrderPrLink { PrUuid = pr.UUID });

                pr.Status       = "FULLY_CONVERTED";
                pr.ModifiedBy   = createdBy;
                pr.ModifiedDate = now;
            }
        }
        else if (req.Lines?.Count > 0)
        {
            ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

            int lineNo = 1;
            foreach (var line in req.Lines)
            {
                po.Lines.Add(new PurchaseOrderLine
                {
                    UUID             = Guid.NewGuid(),
                    LineNo           = lineNo++,
                    SourcePrLineUuid = line.SourcePrLineUuid,
                    ProductUuid      = line.ProductUuid,
                    ItemDescription  = line.ItemDescription,
                    Specification    = line.Specification,
                    UnitOfMeasure    = line.UnitOfMeasure,
                    Quantity         = line.Quantity,
                    UnitPrice        = line.UnitPrice,
                    LineTotal        = line.Quantity * line.UnitPrice,
                    RequiredDate     = line.RequiredDate,
                    LineNotes        = line.LineNotes,
                    BudgetCode       = line.BudgetCode,
                    WarehouseId      = line.WarehouseId,
                    WarehouseName    = line.WarehouseName,
                    RequiresInspection = line.RequiresInspection
                });
            }
        }

        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        _db.PurchaseOrders.Add(po);
        await _db.SaveChangesAsync();
        return po.UUID;
    }

    // ── Update DRAFT PO ───────────────────────────────────────────────────────

    public async Task UpdateAsync(Guid uuid, PatchPoRequest req, int modifiedBy)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT purchase orders can be edited. Current status: {po.Status}.");

        if (req.SupplierName is not null)          po.SupplierName          = req.SupplierName;
        if (req.SupplierId.HasValue)               po.SupplierId            = req.SupplierId.Value;
        if (req.Title is not null)                 po.Title                 = req.Title;
        if (req.DeliveryDate.HasValue)             po.DeliveryDate          = req.DeliveryDate;
        if (req.DeliveryWarehouseId.HasValue)      po.DeliveryWarehouseId   = req.DeliveryWarehouseId;
        if (req.DeliveryWarehouseName is not null) po.DeliveryWarehouseName = req.DeliveryWarehouseName;
        if (req.Notes is not null)                 po.Notes                 = req.Notes;

        if (req.Lines is not null)
        {
            ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

            _db.PurchaseOrderLines.RemoveRange(po.Lines);
            int lineNo = 1;
            foreach (var l in req.Lines)
            {
                po.Lines.Add(new PurchaseOrderLine
                {
                    UUID             = Guid.NewGuid(),
                    LineNo           = lineNo++,
                    SourcePrLineUuid = l.SourcePrLineUuid,
                    ProductUuid      = l.ProductUuid,
                    ItemDescription  = l.ItemDescription,
                    Specification    = l.Specification,
                    UnitOfMeasure    = l.UnitOfMeasure,
                    Quantity         = l.Quantity,
                    UnitPrice        = l.UnitPrice,
                    LineTotal        = l.Quantity * l.UnitPrice,
                    RequiredDate     = l.RequiredDate,
                    LineNotes        = l.LineNotes,
                    BudgetCode       = l.BudgetCode,
                    WarehouseId      = l.WarehouseId,
                    WarehouseName    = l.WarehouseName,
                    RequiresInspection = l.RequiresInspection
                });
            }
            po.TotalAmount = po.Lines.Sum(l => l.Quantity * l.UnitPrice);
        }

        po.ModifiedBy   = modifiedBy;
        po.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── Send PO to supplier (APPROVED → SENT) ─────────────────────────────────

    public async Task SendAsync(Guid uuid, string? contactMobile, int modifiedBy)
    {
        var po = await _db.PurchaseOrders
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "APPROVED")
            throw new UnprocessableEntityException(
                $"Only APPROVED purchase orders can be sent. Current status: {po.Status}.");

        po.Status                = "SENT";
        po.SupplierContactMobile = contactMobile;
        po.ModifiedBy            = modifiedBy;
        po.ModifiedDate          = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // ── Query methods ─────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<PoListItemModel>> GetListAsync(PoListFilter filter)
    {
        var query = _db.PurchaseOrders.Where(p => !p.IsDelete).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(p => p.Status == filter.Status);
        if (filter.SupplierId.HasValue)
            query = query.Where(p => p.SupplierId == filter.SupplierId.Value);
        if (filter.DateFrom.HasValue)
            query = query.Where(p => p.CreatedDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(p => p.CreatedDate <= filter.DateTo.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(p => p.PoNumber.ToLower().Contains(term)
                                  || (p.Title != null && p.Title.ToLower().Contains(term))
                                  || p.SupplierName.ToLower().Contains(term));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(p => p.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PoListItemModel
            {
                UUID         = p.UUID,
                TraceId      = p.TraceId,
                PoNumber     = p.PoNumber,
                Title        = p.Title,
                SupplierId   = p.SupplierId,
                SupplierName = p.SupplierName,
                Status       = p.Status,
                TotalAmount  = p.TotalAmount,
                DeliveryDate = p.DeliveryDate,
                CreatedDate  = p.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<PoListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<PoDetailModel?> GetByIdAsync(Guid uuid)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines.OrderBy(l => l.LineNo))
            .Include(p => p.PrLinks)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete);

        if (po is null) return null;

        return new PoDetailModel
        {
            UUID                  = po.UUID,
            TraceId               = po.TraceId,
            PoNumber              = po.PoNumber,
            Title                 = po.Title,
            SupplierId            = po.SupplierId,
            SupplierName          = po.SupplierName,
            SupplierContactMobile = po.SupplierContactMobile,
            Status                = po.Status,
            TotalAmount           = po.TotalAmount,
            DeliveryDate          = po.DeliveryDate,
            DeliveryWarehouseId   = po.DeliveryWarehouseId,
            DeliveryWarehouseName = po.DeliveryWarehouseName,
            Notes                 = po.Notes,
            InternalNotes         = po.InternalNotes,
            CreatedBy             = po.CreatedBy,
            CreatedDate           = po.CreatedDate,
            LinkedPrUuids         = po.PrLinks.Select(l => l.PrUuid).ToList(),
            Lines = po.Lines.Select(l => new PoLineModel
            {
                UUID                  = l.UUID,
                LineNo                = l.LineNo,
                SourcePrLineUuid      = l.SourcePrLineUuid,
                ProductUuid           = l.ProductUuid,
                ItemDescription       = l.ItemDescription,
                Specification         = l.Specification,
                UnitOfMeasure         = l.UnitOfMeasure,
                Quantity              = l.Quantity,
                UnitPrice             = l.UnitPrice,
                LineTotal             = l.LineTotal,
                QtyReceived           = l.QtyReceived,
                QtyInvoiced           = l.QtyInvoiced,
                RequiredDate          = l.RequiredDate,
                LineNotes             = l.LineNotes,
                BudgetCode            = l.BudgetCode,
                WarehouseId           = l.WarehouseId,
                WarehouseName         = l.WarehouseName,
                EffectiveWarehouseId  = l.EffectiveWarehouseId,
                EffectiveWarehouseName = l.EffectiveWarehouseName,
                RequiresInspection    = l.RequiresInspection
            }).ToList()
        };
    }

    public async Task<List<PoSearchItemModel>> SearchForGrnAsync(string? q, bool receivableOnly)
    {
        var query = _db.PurchaseOrders.Where(p => !p.IsDelete).AsQueryable();

        if (receivableOnly)
            query = query.Where(p => p.Status == "SENT" || p.Status == "PARTIALLY_RECEIVED");

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(p => p.PoNumber.ToLower().Contains(term)
                                  || p.SupplierName.ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(p => p.ModifiedDate ?? p.CreatedDate)
            .Take(20)
            .Select(p => new PoSearchItemModel
            {
                UUID         = p.UUID,
                PoNumber     = p.PoNumber,
                SupplierName = p.SupplierName,
                Status       = p.Status,
                GrandTotal   = p.TotalAmount,
                Currency     = null,
                QtyPending   = p.Lines.Where(l => l.Quantity > l.QtyReceived)
                                       .Sum(l => l.Quantity - l.QtyReceived)
            })
            .ToListAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Frontend p-inputNumber widgets already soft-clamp to this range, but that's only a UI
    // hint — nothing stopped a pasted value, a race before blur, or a direct API call from
    // submitting an out-of-range quantity. This is the actual enforcement.
    private const decimal MaxLineQuantity = 999999m;

    private static void ValidateLineQuantities(IEnumerable<decimal> quantities)
    {
        foreach (var qty in quantities)
        {
            if (qty <= 0)
                throw new BadRequestException("Line quantity must be greater than zero.");
            if (qty > MaxLineQuantity)
                throw new BadRequestException($"Line quantity must not exceed {MaxLineQuantity:N0}.");
        }
    }

    private static void ValidateQuotationRequirements(IEnumerable<PrLine> lines)
    {
        var blockers = lines
            .Where(l => l.RequiresQuotation && l.QuotationStatus != "AWARDED")
            .Select(l => l.ItemDescription)
            .ToList();
        if (blockers.Count > 0)
            throw new BadRequestException(
                $"The following lines require an awarded quotation before conversion: {string.Join(", ", blockers)}");
    }

    private async Task<string> GeneratePoNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _db.PurchaseOrders
            .CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        return $"PO-{year}-{(count + 1):D5}";
    }

    private async Task<string[]> GeneratePoNumbersAsync(int year, int n)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var existing  = await _db.PurchaseOrders
            .CountAsync(p => p.CreatedDate >= yearStart && p.CreatedDate < yearEnd);
        return Enumerable.Range(1, n)
            .Select(i => $"PO-{year}-{(existing + i):D5}")
            .ToArray();
    }
}
