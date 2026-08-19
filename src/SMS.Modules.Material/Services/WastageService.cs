using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using System.Transactions;

namespace SMS.Modules.Material.Services;

internal sealed class WastageService : IWastageService
{
    private readonly MaterialDbContext      _db;
    private readonly ICostAllocationService _cost;
    private readonly IUserQueryService      _userQuery;

    public WastageService(MaterialDbContext db, ICostAllocationService cost, IUserQueryService userQuery)
    {
        _db   = db;
        _cost = cost;
        _userQuery = userQuery;
    }

    // ── Create (manual) ───────────────────────────────────────────────────────

    public async Task<Guid> CreateManualAsync(CreateWastageRequest req, int createdBy)
    {
        var mivLine = await _db.MaterialIssueVoucherLines
            .Include(l => l.MaterialIssueVoucher).ThenInclude(v => v.MaterialIssueRequest)
            .FirstOrDefaultAsync(l => l.UUID == req.MivLineUuid)
            ?? throw new NotFoundException("MIV line", req.MivLineUuid);

        if (mivLine.MaterialIssueVoucher.Status != "POSTED")
            throw new UnprocessableEntityException(
                "Wastage can only be recorded against lines from POSTED issue vouchers.");

        if (req.WastedQty <= 0)
            throw new UnprocessableEntityException("Wasted qty must be greater than zero.");

        if (string.IsNullOrWhiteSpace(req.Reason))
            throw new UnprocessableEntityException("A reason is required for wastage records.");

        var mir = mivLine.MaterialIssueVoucher.MaterialIssueRequest;
        var now = DateTime.UtcNow;

        var wastageNo = await GenerateWastageNoAsync(now);

        var wastage = new Wastage
        {
            UUID            = Guid.NewGuid(),
            WastageNo       = wastageNo,
            SourceType      = "MANUAL",
            SourceUuid      = null,
            MivLineId       = mivLine.Id,
            MirId           = mir.Id,
            ProductUuid     = mivLine.VariantUuid,
            ItemDescription = mivLine.ItemDescription,
            UnitOfMeasure   = mivLine.UnitOfMeasure,
            WastedQty       = req.WastedQty,
            UnitCost        = mivLine.UnitCost,
            Amount          = req.WastedQty * mivLine.UnitCost,
            Reason          = req.Reason,
            Status          = "PENDING_APPROVAL",
            RecordedBy      = createdBy,
            Notes           = req.Notes,
            CreatedDate     = now
        };

        _db.Wastages.Add(wastage);
        await _db.SaveChangesAsync();

        return wastage.UUID;
    }

    // ── Approve ───────────────────────────────────────────────────────────────

    public async Task ApproveAsync(Guid uuid, int approvedBy, string? notes)
    {
        var wastage = await _db.Wastages
            .Include(w => w.MIR)
            .FirstOrDefaultAsync(w => w.UUID == uuid)
            ?? throw new NotFoundException("Wastage", uuid);

        if (wastage.Status != "PENDING_APPROVAL")
            throw new UnprocessableEntityException(
                $"Only PENDING_APPROVAL wastage records can be approved. Current status: {wastage.Status}.");

        // Prevent self-approval — a different user must formally sign off the write-off
        if (wastage.RecordedBy == approvedBy)
            throw new UnprocessableEntityException(
                "Self-approval is not permitted. The wastage must be approved by a different user " +
                "from the one who recorded it.");

        var mir = wastage.MIR;
        var now = DateTime.UtcNow;

        using var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions
            {
                IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted,
                Timeout        = TimeSpan.FromSeconds(30)
            },
            TransactionScopeAsyncFlowOption.Enabled);

        // Cost ledger: WASTAGE (positive = additional charge to project/department)
        await _cost.PostCostAsync(new CostPostCommand
        {
            RequestType     = mir.RequestType,
            ProjectId       = mir.ProjectId,
            MirId           = mir.Id,
            Department      = mir.Department,
            ProductUuid     = wastage.ProductUuid,
            ItemDescription = wastage.ItemDescription,
            TransactionType = "WASTAGE",
            ReferenceType   = "WASTAGE",
            ReferenceId     = wastage.UUID,
            ReferenceNumber = wastage.WastageNo,
            Quantity        = wastage.WastedQty,
            UnitCost        = wastage.UnitCost,
            PostedDate      = now,
            PostedBy        = approvedBy,
            // PV-008 — include what was wasted (product + variant), not just why.
            Notes           = $"Wastage {wastage.WastageNo}: {wastage.ItemDescription} ({wastage.Reason})"
        });

        wastage.Status     = "APPROVED";
        wastage.ApprovedBy = approvedBy;
        wastage.ApprovedAt = now;
        if (!string.IsNullOrWhiteSpace(notes))
            wastage.Notes = string.IsNullOrWhiteSpace(wastage.Notes)
                ? notes
                : wastage.Notes + "\n" + notes;

        await _db.SaveChangesAsync();
        scope.Complete();
    }

    // ── Reject ────────────────────────────────────────────────────────────────

    public async Task RejectAsync(Guid uuid, int rejectedBy, string reason)
    {
        var wastage = await _db.Wastages
            .FirstOrDefaultAsync(w => w.UUID == uuid)
            ?? throw new NotFoundException("Wastage", uuid);

        if (wastage.Status != "PENDING_APPROVAL")
            throw new UnprocessableEntityException(
                $"Only PENDING_APPROVAL wastage records can be rejected. Current status: {wastage.Status}.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new UnprocessableEntityException("A rejection reason is required.");

        wastage.Status          = "REJECTED";
        wastage.RejectedBy      = rejectedBy;
        wastage.RejectedAt      = DateTime.UtcNow;
        wastage.RejectionReason = reason;

        await _db.SaveChangesAsync();
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<WastageDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var w = await _db.Wastages
            .Include(x => x.MivLine).ThenInclude(l => l.MaterialIssueVoucher)
            .FirstOrDefaultAsync(x => x.UUID == uuid);

        if (w == null) return null;

        var userIds = new List<int> { w.RecordedBy };
        if (w.ApprovedBy.HasValue) userIds.Add(w.ApprovedBy.Value);
        if (w.RejectedBy.HasValue) userIds.Add(w.RejectedBy.Value);
        var nameMap = (await _userQuery.GetUsersAsync(userIds.Distinct().ToList()))
            .ToDictionary(u => u.UserId, u => u.DisplayName);

        return new WastageDetailModel
        {
            UUID            = w.UUID,
            WastageNo       = w.WastageNo,
            SourceType      = w.SourceType,
            SourceUuid      = w.SourceUuid,
            MivUuid         = w.MivLine.MaterialIssueVoucher.UUID,
            IssueNo         = w.MivLine.MaterialIssueVoucher.IssueNo,
            ProductUuid     = w.ProductUuid,
            ItemDescription = w.ItemDescription,
            UnitOfMeasure   = w.UnitOfMeasure,
            WastedQty       = w.WastedQty,
            UnitCost        = w.UnitCost,
            Amount          = w.Amount,
            Reason          = w.Reason,
            Status          = w.Status,
            RecordedBy      = w.RecordedBy,
            RecordedByName  = nameMap.GetValueOrDefault(w.RecordedBy),
            ApprovedBy      = w.ApprovedBy,
            ApprovedByName  = w.ApprovedBy.HasValue ? nameMap.GetValueOrDefault(w.ApprovedBy.Value) : null,
            ApprovedAt      = w.ApprovedAt,
            RejectedBy      = w.RejectedBy,
            RejectedByName  = w.RejectedBy.HasValue ? nameMap.GetValueOrDefault(w.RejectedBy.Value) : null,
            RejectedAt      = w.RejectedAt,
            RejectionReason = w.RejectionReason,
            Notes           = w.Notes,
            CreatedDate     = w.CreatedDate
        };
    }

    public async Task<PaginatedResponse<WastageListItemModel>> GetListAsync(WastageListFilter filter)
    {
        var query = _db.Wastages.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(w => w.Status == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.SourceType))
            query = query.Where(w => w.SourceType == filter.SourceType);

        if (filter.ProductUuid.HasValue)
            query = query.Where(w => w.ProductUuid == filter.ProductUuid.Value);

        if (filter.DateFrom.HasValue)
            query = query.Where(w => w.CreatedDate >= filter.DateFrom.Value);

        if (filter.DateTo.HasValue)
            query = query.Where(w => w.CreatedDate <= filter.DateTo.Value);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(w => w.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new WastageListItemModel
            {
                UUID            = w.UUID,
                WastageNo       = w.WastageNo,
                SourceType      = w.SourceType,
                ProductUuid     = w.ProductUuid,
                ItemDescription = w.ItemDescription,
                WastedQty       = w.WastedQty,
                Amount          = w.Amount,
                Reason          = w.Reason,
                Status          = w.Status,
                RecordedBy      = w.RecordedBy,
                CreatedDate     = w.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<WastageListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GenerateWastageNoAsync(DateTime date)
    {
        var year  = date.Year;
        var count = await _db.Wastages
            .CountAsync(w => w.CreatedDate.Year == year);
        return $"WST-{year}-{(count + 1):D5}";
    }
}
