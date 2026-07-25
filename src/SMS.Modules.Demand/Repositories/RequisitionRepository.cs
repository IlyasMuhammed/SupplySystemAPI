using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Repositories;

internal sealed class RequisitionRepository : IRequisitionRepository
{
    private readonly DemandDbContext _db;

    public RequisitionRepository(DemandDbContext db) => _db = db;

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

    public async Task<Guid> CreateAsync(CreatePrRequest req, int createdBy)
    {
        ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

        var uuid = Guid.NewGuid();
        var now  = DateTime.UtcNow;

        var lines = req.Lines.Select((l, i) => new PrLine
        {
            UUID                = Guid.NewGuid(),
            LineNo              = i + 1,
            ProductId           = l.ProductId,
            ItemDescription     = l.ItemDescription,
            Specification       = l.Specification,
            UnitOfMeasure       = l.UnitOfMeasure,
            Quantity            = l.Quantity,
            EstimatedUnitPrice  = l.EstimatedUnitPrice,
            LineTotal           = l.Quantity * l.EstimatedUnitPrice,
            PreferredSupplierId = l.PreferredSupplierId,
            RequiresQuotation   = l.RequiresQuotation,
            RequiredDate        = l.RequiredDate,
            LineNotes           = l.LineNotes,
            BudgetCode          = l.BudgetCode,
            LineStatus          = "OPEN"
        }).ToList();

        var pr = new PurchaseRequisition
        {
            UUID              = uuid,
            TraceId           = Guid.NewGuid(),
            PrNumber          = await GeneratePrNumberAsync(now.Year),
            PrTitle           = req.PrTitle,
            Department        = req.Department,
            RequesterId       = createdBy,
            RequestedDate     = req.RequestedDate,
            Priority          = req.Priority,
            PrType            = req.PrType,
            RequiresQuotation = req.RequiresQuotation,
            Justification     = req.Justification,
            EstimatedTotal    = lines.Sum(l => l.LineTotal),
            WarehouseUuid     = req.WarehouseUuid,
            Notes             = req.Notes,
            Status            = "DRAFT",
            IsActive          = true,
            CreatedBy         = createdBy,
            CreatedDate       = now,
            Lines             = lines
        };

        _db.PurchaseRequisitions.Add(pr);
        await _db.SaveChangesAsync();
        return uuid;
    }

    public async Task<PaginatedResponse<PrListItemModel>> GetListAsync(PrListFilter filter)
    {
        var query = _db.PurchaseRequisitions.Where(r => !r.IsDelete).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(r => r.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Department))
            query = query.Where(r => r.Department == filter.Department);
        if (filter.RequesterId.HasValue)
            query = query.Where(r => r.RequesterId == filter.RequesterId.Value);
        if (filter.DateFrom.HasValue)
            query = query.Where(r => r.RequestedDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(r => r.RequestedDate <= filter.DateTo.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(r => r.PrNumber.ToLower().Contains(term)
                                  || r.PrTitle.ToLower().Contains(term));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(r => r.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new PrListItemModel
            {
                UUID           = r.UUID,
                TraceId        = r.TraceId,
                PrNumber       = r.PrNumber,
                PrTitle        = r.PrTitle,
                Department     = r.Department,
                Status         = r.Status,
                RequestedDate  = r.RequestedDate,
                EstimatedTotal = r.EstimatedTotal,
                RequesterId    = r.RequesterId,
                CreatedDate    = r.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<PrListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<PrDetailModel?> GetByIdAsync(Guid uuid)
    {
        var pr = await _db.PurchaseRequisitions
            .Include(r => r.Lines.OrderBy(l => l.LineNo))
            .FirstOrDefaultAsync(r => r.UUID == uuid && !r.IsDelete);

        if (pr is null) return null;

        // Check if there is an awarded quotation linked to this PR
        var linkedAwardedQuotation = await ResolveLinkedAwardedQuotationAsync(pr.UUID);

        return new PrDetailModel
        {
            UUID                    = pr.UUID,
            TraceId                 = pr.TraceId,
            LinkedAwardedQuotation  = linkedAwardedQuotation,
            PrNumber          = pr.PrNumber,
            PrTitle           = pr.PrTitle,
            Department        = pr.Department,
            RequesterId       = pr.RequesterId,
            RequestedDate     = pr.RequestedDate,
            Priority          = pr.Priority,
            PrType            = pr.PrType,
            RequiresQuotation = pr.RequiresQuotation,
            Justification     = pr.Justification,
            EstimatedTotal    = pr.EstimatedTotal,
            WarehouseUuid     = pr.WarehouseUuid,
            Status            = pr.Status,
            ApprovedBy        = pr.ApprovedBy,
            ApprovedAt        = pr.ApprovedAt,
            RejectionReason   = pr.RejectionReason,
            Notes             = pr.Notes,
            CreatedDate       = pr.CreatedDate,
            CreatedBy         = pr.CreatedBy,
            Lines             = pr.Lines.Select(l => new PrLineModel
            {
                UUID                = l.UUID,
                LineNo              = l.LineNo,
                ProductId           = l.ProductId,
                ItemDescription     = l.ItemDescription,
                Specification       = l.Specification,
                UnitOfMeasure       = l.UnitOfMeasure,
                Quantity            = l.Quantity,
                EstimatedUnitPrice  = l.EstimatedUnitPrice,
                LineTotal           = l.LineTotal,
                PreferredSupplierId = l.PreferredSupplierId,
                RequiresQuotation   = l.RequiresQuotation,
                QuotationStatus     = l.QuotationStatus,
                LineStatus          = l.LineStatus,
                RequiredDate        = l.RequiredDate,
                LineNotes           = l.LineNotes,
                BudgetCode          = l.BudgetCode,
                DisbursedQty        = l.DisbursedQty
            }).ToList()
        };
    }

    public async Task UpdateAsync(Guid uuid, PatchPrRequest req, int modifiedBy)
    {
        var pr = await _db.PurchaseRequisitions
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.UUID == uuid && !r.IsDelete)
            ?? throw new NotFoundException("PurchaseRequisition", uuid);

        if (pr.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT requisitions can be edited. Current status: {pr.Status}.");

        if (req.PrTitle is not null)           pr.PrTitle           = req.PrTitle;
        if (req.Department is not null)        pr.Department        = req.Department;
        if (req.RequestedDate.HasValue)        pr.RequestedDate     = req.RequestedDate.Value;
        if (req.Priority is not null)          pr.Priority          = req.Priority;
        if (req.PrType is not null)            pr.PrType            = req.PrType;
        if (req.RequiresQuotation.HasValue)    pr.RequiresQuotation = req.RequiresQuotation.Value;
        if (req.Justification is not null)     pr.Justification     = req.Justification;
        pr.WarehouseUuid = req.ClearWarehouse ? null : req.WarehouseUuid ?? pr.WarehouseUuid;
        if (req.Notes is not null)             pr.Notes             = req.Notes;

        if (req.Lines is not null)
        {
            ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

            _db.PrLines.RemoveRange(pr.Lines);
            var newLines = req.Lines.Select((l, i) => new PrLine
            {
                UUID                  = Guid.NewGuid(),
                PurchaseRequisitionId = pr.Id,
                LineNo                = i + 1,
                ProductId             = l.ProductId,
                ItemDescription       = l.ItemDescription,
                Specification         = l.Specification,
                UnitOfMeasure         = l.UnitOfMeasure,
                Quantity              = l.Quantity,
                EstimatedUnitPrice    = l.EstimatedUnitPrice,
                LineTotal             = l.Quantity * l.EstimatedUnitPrice,
                PreferredSupplierId   = l.PreferredSupplierId,
                RequiresQuotation     = l.RequiresQuotation,
                RequiredDate          = l.RequiredDate,
                LineNotes             = l.LineNotes,
                BudgetCode            = l.BudgetCode,
                LineStatus            = "OPEN"
            }).ToList();
            _db.PrLines.AddRange(newLines);
            pr.EstimatedTotal = newLines.Sum(l => l.LineTotal);
        }

        pr.ModifiedBy   = modifiedBy;
        pr.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    private async Task<string> GeneratePrNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _db.PurchaseRequisitions
            .CountAsync(r => r.CreatedDate >= yearStart && r.CreatedDate < yearEnd);
        return $"PR-{year}-{(count + 1):D5}";
    }

    private async Task<LinkedAwardedQuotationInfo?> ResolveLinkedAwardedQuotationAsync(Guid prUuid)
    {
        var awardedQuotation = await _db.Quotations
            .Where(q => q.SourceType == "PR" && q.SourceId == prUuid
                     && q.Status == "AWARDED" && !q.IsDelete)
            .Select(q => new { q.Id, q.QuotationNumber })
            .FirstOrDefaultAsync();

        if (awardedQuotation is null) return null;

        var awardedResponse = await _db.VendorResponses
            .Where(r => r.QuotationId == awardedQuotation.Id && r.Status == "AWARDED")
            .Select(r => new { r.SupplierId, r.SupplierName })
            .FirstOrDefaultAsync();

        if (awardedResponse is null) return null;

        return new LinkedAwardedQuotationInfo
        {
            QuotationNumber     = awardedQuotation.QuotationNumber,
            AwardedSupplierId   = awardedResponse.SupplierId,
            AwardedSupplierName = awardedResponse.SupplierName
        };
    }
}
