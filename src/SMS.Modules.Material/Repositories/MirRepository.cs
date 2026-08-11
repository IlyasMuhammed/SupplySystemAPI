using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Repositories;

internal sealed class MirRepository : IMirRepository
{
    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;
    private readonly DemandDbContext    _demand;
    private readonly ILogger<MirRepository> _logger;

    public MirRepository(MaterialDbContext db, InventoryDbContext inv, DemandDbContext demand, ILogger<MirRepository> logger)
    {
        _db     = db;
        _inv    = inv;
        _demand = demand;
        _logger = logger;
    }

    private static readonly HashSet<string> ValidTypes = ["PROJECT", "DEPARTMENT", "MAINTENANCE"];

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateMirRequest req, int createdBy)
    {
        if (!ValidTypes.Contains(req.RequestType))
            throw new BadRequestException(
                $"Invalid request_type '{req.RequestType}'. Must be PROJECT, DEPARTMENT, or MAINTENANCE.");

        if (req.Lines.Count == 0)
            throw new UnprocessableEntityException("An MIR must have at least one line.");

        ValidateTypeFields(req.RequestType, req.ProjectUuid, req.Department, req.MaintenanceRef);

        int? projectId = null;
        if (req.RequestType == "PROJECT")
        {
            var project = await _db.Projects
                .FirstOrDefaultAsync(p => p.UUID == req.ProjectUuid!.Value && !p.IsDelete)
                ?? throw new NotFoundException("Project", req.ProjectUuid!.Value);
            projectId = project.Id;
        }

        var (lines, inheritedTraceId) = await BuildLinesAsync(req.Lines);
        var uuid  = Guid.NewGuid();

        _db.MaterialIssueRequests.Add(new MaterialIssueRequest
        {
            UUID           = uuid,
            TraceId        = inheritedTraceId ?? Guid.NewGuid(),
            RequestNo      = await GenerateRequestNoAsync(DateTime.UtcNow.Year),
            RequestType    = req.RequestType,
            ProjectId      = projectId,
            Department     = req.Department,
            MaintenanceRef = req.MaintenanceRef,
            RequestedBy    = createdBy,
            RequiredDate   = req.RequiredDate,
            Priority       = req.Priority,
            Purpose        = req.Purpose,
            Status         = "DRAFT",
            EstimatedValue = lines.Sum(l => l.EstimatedLineValue),
            Notes          = req.Notes,
            IsActive       = true,
            CreatedBy      = createdBy,
            CreatedDate    = DateTime.UtcNow,
            Lines          = lines
        });

        await _db.SaveChangesAsync();
        return uuid;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<MirListItemModel>> GetListAsync(MirListFilter filter)
    {
        var query = _db.MaterialIssueRequests
            .Include(m => m.Project)
            .Where(m => !m.IsDelete)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(m => m.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.RequestType))
            query = query.Where(m => m.RequestType == filter.RequestType);
        if (!string.IsNullOrWhiteSpace(filter.Department))
            query = query.Where(m => m.Department == filter.Department);
        if (filter.DateFrom.HasValue)
            query = query.Where(m => m.CreatedDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)
            query = query.Where(m => m.CreatedDate <= filter.DateTo.Value);
        if (filter.ProjectUuid.HasValue)
        {
            var proj = await _db.Projects.FirstOrDefaultAsync(p => p.UUID == filter.ProjectUuid.Value && !p.IsDelete);
            if (proj is not null) query = query.Where(m => m.ProjectId == proj.Id);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(m => m.RequestNo.ToLower().Contains(term)
                                  || (m.Purpose != null && m.Purpose.ToLower().Contains(term)));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(m => m.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new MirListItemModel
            {
                UUID           = m.UUID,
                TraceId        = m.TraceId,
                RequestNo      = m.RequestNo,
                RequestType    = m.RequestType,
                ProjectName    = m.Project != null ? m.Project.ProjectName : null,
                Department     = m.Department,
                MaintenanceRef = m.MaintenanceRef,
                Status         = m.Status,
                Priority       = m.Priority,
                EstimatedValue = m.EstimatedValue,
                RequiredDate   = m.RequiredDate,
                CreatedDate    = m.CreatedDate,
                TotalLines     = m.Lines.Count
            })
            .ToListAsync();

        return new PaginatedResponse<MirListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    // ── Get by UUID ───────────────────────────────────────────────────────────

    public async Task<MirDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var mir = await _db.MaterialIssueRequests
            .Include(m => m.Project)
            .Include(m => m.Lines)
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete);

        if (mir is null) return null;

        // Fetch the latest approved qty per line (highest step number).
        var lineIds     = mir.Lines.Select(l => l.Id).ToList();
        var latestQtys  = await _db.MirLineApprovals
            .Where(a => a.MirId == mir.Id && lineIds.Contains(a.LineId))
            .GroupBy(a => a.LineId)
            .Select(g => new { LineId = g.Key, ApprovedQty = g.OrderByDescending(a => a.StepNumber).First().ApprovedQty })
            .ToDictionaryAsync(x => x.LineId, x => x.ApprovedQty);

        // Resolve linked PR lines' UUIDs for lines that carry a pr_line_id.
        var prLineIds      = mir.Lines.Where(l => l.PrLineId.HasValue).Select(l => l.PrLineId!.Value).Distinct().ToList();
        var prLineUuidsById = prLineIds.Count > 0
            ? await _demand.PrLines.Where(l => prLineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.UUID)
            : new Dictionary<int, Guid>();

        return new MirDetailModel
        {
            UUID            = mir.UUID,
            TraceId         = mir.TraceId,
            RequestNo       = mir.RequestNo,
            RequestType     = mir.RequestType,
            ProjectUuid     = mir.Project?.UUID,
            ProjectName     = mir.Project?.ProjectName,
            Department      = mir.Department,
            MaintenanceRef  = mir.MaintenanceRef,
            RequestedBy     = mir.RequestedBy,
            RequiredDate    = mir.RequiredDate,
            Priority        = mir.Priority,
            Purpose         = mir.Purpose,
            Status          = mir.Status,
            EstimatedValue  = mir.EstimatedValue,
            RejectionReason = mir.RejectionReason,
            ApproverRemarks = mir.ApproverRemarks,
            ApprovedBy      = mir.ApprovedBy,
            ApprovedAt      = mir.ApprovedAt,
            Notes           = mir.Notes,
            CreatedBy       = mir.CreatedBy,
            CreatedDate     = mir.CreatedDate,
            Lines           = mir.Lines.OrderBy(l => l.LineNo).Select(l => new MirLineModel
            {
                UUID               = l.UUID,
                LineNo             = l.LineNo,
                ProductUuid        = l.ProductUuid,
                ItemDescription    = l.ItemDescription,
                UnitOfMeasure      = l.UnitOfMeasure,
                RequestedQty       = l.RequestedQty,
                UnitCost           = l.UnitCost,
                EstimatedLineValue = l.EstimatedLineValue,
                WarehouseId        = l.WarehouseId,
                WarehouseName      = l.WarehouseName,
                PrLineId           = l.PrLineId.HasValue && prLineUuidsById.TryGetValue(l.PrLineId.Value, out var prUuid) ? prUuid : null,
                LatestApprovedQty  = latestQtys.TryGetValue(l.Id, out var aq) ? aq : null
            }).ToList()
        };
    }

    // ── Patch ─────────────────────────────────────────────────────────────────

    public async Task PatchAsync(Guid uuid, PatchMirRequest req, int modifiedBy)
    {
        var mir = await _db.MaterialIssueRequests
            .Include(m => m.Lines)
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", uuid);

        if (mir.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"MIR can only be edited in DRAFT status. Current status: {mir.Status}.");

        if (req.RequiredDate.HasValue) mir.RequiredDate   = req.RequiredDate;
        if (req.Priority      is not null) mir.Priority   = req.Priority;
        if (req.Purpose       is not null) mir.Purpose    = req.Purpose;
        if (req.Notes         is not null) mir.Notes      = req.Notes;
        if (req.Department    is not null) mir.Department = req.Department;
        if (req.MaintenanceRef is not null) mir.MaintenanceRef = req.MaintenanceRef;

        if (req.ProjectUuid.HasValue)
        {
            var project = await _db.Projects
                .FirstOrDefaultAsync(p => p.UUID == req.ProjectUuid.Value && !p.IsDelete)
                ?? throw new NotFoundException("Project", req.ProjectUuid.Value);
            mir.ProjectId = project.Id;
        }

        if (req.Lines is not null)
        {
            if (req.Lines.Count == 0)
                throw new UnprocessableEntityException("An MIR must have at least one line.");

            _db.MaterialIssueRequestDetails.RemoveRange(mir.Lines);
            var (newLines, _) = await BuildLinesAsync(req.Lines);
            foreach (var l in newLines) l.MaterialIssueRequestId = mir.Id;
            mir.Lines          = newLines;
            mir.EstimatedValue = newLines.Sum(l => l.EstimatedLineValue);
            // TL-006: trace_id is immutable after creation — never recomputed on edit,
            // even when the line set (and its PR links) changes.
        }

        mir.ModifiedBy   = modifiedBy;
        mir.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(Guid uuid, int deletedBy)
    {
        var mir = await _db.MaterialIssueRequests
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", uuid);

        if (mir.Status != "DRAFT")
            throw new UnprocessableEntityException("Only DRAFT MIRs can be deleted.");

        mir.IsDelete    = true;
        mir.IsActive    = false;
        mir.ModifiedBy  = deletedBy;
        mir.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── Status transitions ────────────────────────────────────────────────────

    public async Task SubmitAsync(Guid uuid, int userId)
    {
        var mir = await _db.MaterialIssueRequests.Include(m => m.Lines)
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", uuid);

        if (mir.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT MIRs can be submitted. Current status: {mir.Status}.");
        if (mir.Lines.Count == 0)
            throw new UnprocessableEntityException("Cannot submit an MIR with zero lines.");

        mir.Status       = "PENDING_APPROVAL";
        mir.ModifiedBy   = userId;
        mir.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task ApproveAsync(Guid uuid, int userId, string? remarks)
    {
        var mir = await _db.MaterialIssueRequests
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", uuid);

        if (mir.Status != "PENDING_APPROVAL")
            throw new UnprocessableEntityException(
                $"Only PENDING_APPROVAL MIRs can be approved. Current status: {mir.Status}.");

        var now = DateTime.UtcNow;
        mir.Status          = "APPROVED";
        mir.ApprovedBy      = userId;
        mir.ApprovedAt      = now;
        mir.ApproverRemarks = remarks;
        mir.ModifiedBy      = userId;
        mir.ModifiedDate    = now;
        await _db.SaveChangesAsync();
    }

    public async Task RejectAsync(Guid uuid, int userId, string reason)
    {
        var mir = await _db.MaterialIssueRequests
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", uuid);

        if (mir.Status != "PENDING_APPROVAL")
            throw new UnprocessableEntityException(
                $"Only PENDING_APPROVAL MIRs can be rejected. Current status: {mir.Status}.");

        mir.Status          = "REJECTED";
        mir.RejectionReason = reason;
        mir.ModifiedBy      = userId;
        mir.ModifiedDate    = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task IssueAsync(Guid uuid, int userId)
    {
        var mir = await _db.MaterialIssueRequests
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", uuid);

        if (mir.Status != "APPROVED")
            throw new UnprocessableEntityException(
                $"Only APPROVED MIRs can be issued. Current status: {mir.Status}.");

        mir.Status       = "ISSUED";
        mir.ModifiedBy   = userId;
        mir.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task CancelAsync(Guid uuid, int userId)
    {
        var mir = await _db.MaterialIssueRequests
            .FirstOrDefaultAsync(m => m.UUID == uuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", uuid);

        if (mir.Status is "ISSUED" or "CANCELLED")
            throw new UnprocessableEntityException(
                $"MIR in status '{mir.Status}' cannot be cancelled.");

        // Release any active stock reservations when cancelling an approved MIR.
        if (mir.Status is "APPROVED" or "PARTIALLY_APPROVED")
        {
            await ReleaseReservationsAsync(mir.Id, "MIR Cancelled");
        }

        mir.Status       = "CANCELLED";
        mir.ModifiedBy   = userId;
        mir.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task ReleaseReservationsAsync(int mirId, string reason)
    {
        var reservations = await _db.StockReservations
            .Where(r => r.MirId == mirId && r.Status == "ACTIVE")
            .ToListAsync();

        if (reservations.Count == 0) return;

        var now = DateTime.UtcNow;

        foreach (var res in reservations)
        {
            res.Status        = "RELEASED";
            res.ReleasedAt    = now;
            res.ReleaseReason = reason;

            // Decrement QtyReserved on the InventoryItem.
            var item = await _inv.InventoryItems.FindAsync(res.InventoryItemId);
            if (item is not null)
            {
                item.QtyReserved = Math.Max(0, item.QtyReserved - res.ReservedQty);
                item.LastUpdated = now;
            }
        }

        await _inv.SaveChangesAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record PrLineResolution(int Id, Guid TraceId);

    // Returns the built lines plus the trace_id the MIR should inherit from its first
    // pr_line_id-linked line (TL-006), or null when no line links to a PR at all.
    private async Task<(List<MaterialIssueRequestDetail> Lines, Guid? InheritedTraceId)> BuildLinesAsync(List<CreateMirLineRequest> inputs)
    {
        var productUuids  = inputs.Select(l => l.ProductUuid).Distinct().ToList();
        var products      = await _inv.Products
            .Include(p => p.Variants)
            .Where(p => productUuids.Contains(p.Uuid) && p.IsActive)
            .ToListAsync();

        var warehouseIds  = inputs.Where(l => l.WarehouseId.HasValue).Select(l => l.WarehouseId!.Value).Distinct().ToList();
        var warehouseNames = warehouseIds.Count > 0
            ? await _inv.Warehouses
                .Where(w => warehouseIds.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, w => w.Name)
            : new Dictionary<int, string>();

        var prLineUuids   = inputs.Where(l => l.PrLineId.HasValue).Select(l => l.PrLineId!.Value).Distinct().ToList();
        var prLineResolutions = prLineUuids.Count > 0
            ? await ResolveApprovedPrLinesAsync(prLineUuids)
            : new Dictionary<Guid, PrLineResolution>();

        Guid? inheritedTraceId = null;
        var distinctTraceIds   = new List<Guid>();

        var lines = new List<MaterialIssueRequestDetail>();
        int lineNo = 1;
        foreach (var l in inputs)
        {
            var product = products.FirstOrDefault(p => p.Uuid == l.ProductUuid)
                ?? throw new NotFoundException($"Product '{l.ProductUuid}' not found or inactive.");

            int? prLineId = null;
            if (l.PrLineId.HasValue)
            {
                var resolution = prLineResolutions[l.PrLineId.Value];
                prLineId = resolution.Id;

                inheritedTraceId ??= resolution.TraceId;
                if (!distinctTraceIds.Contains(resolution.TraceId))
                    distinctTraceIds.Add(resolution.TraceId);
            }

            // PV-001 — Product no longer carries its own price; the default variant's
            // PurchasePrice is the FSD-aligned stand-in until MIR itself moves to variant_id
            // (FSD Addendum 26 §7.4, a later task).
            var defaultVariantPrice = product.Variants.FirstOrDefault(v => v.IsDefault)?.PurchasePrice ?? 0m;

            lines.Add(new MaterialIssueRequestDetail
            {
                UUID               = Guid.NewGuid(),
                LineNo             = lineNo++,
                ProductUuid        = l.ProductUuid,
                ItemDescription    = product.Name,
                UnitOfMeasure      = product.UomCode,
                RequestedQty       = l.RequestedQty,
                UnitCost           = defaultVariantPrice,
                EstimatedLineValue = l.RequestedQty * defaultVariantPrice,
                WarehouseId        = l.WarehouseId,
                WarehouseName      = l.WarehouseId.HasValue && warehouseNames.TryGetValue(l.WarehouseId.Value, out var wn) ? wn : null,
                Purpose            = l.Purpose,
                Notes              = l.Notes,
                PrLineId           = prLineId
            });
        }

        if (distinctTraceIds.Count > 1)
            _logger.LogWarning(
                "MIR lines reference {ChainCount} different PR trace chains: {TraceIds}. Using the first linked line's trace_id {TraceId}.",
                distinctTraceIds.Count, string.Join(", ", distinctTraceIds), inheritedTraceId);

        return (lines, inheritedTraceId);
    }

    // Resolves pr_line_id UUIDs to internal PrLine ids (+ the owning PR's trace_id),
    // requiring each to exist and its parent purchase requisition to be APPROVED.
    // No other validation is performed.
    private async Task<Dictionary<Guid, PrLineResolution>> ResolveApprovedPrLinesAsync(List<Guid> prLineUuids)
    {
        var prLines = await _demand.PrLines
            .Where(l => prLineUuids.Contains(l.UUID))
            .Select(l => new { l.UUID, l.Id, Status = l.PurchaseRequisition.Status, TraceId = l.PurchaseRequisition.TraceId })
            .ToListAsync();

        var missing = prLineUuids.Except(prLines.Select(l => l.UUID)).ToList();
        if (missing.Count > 0)
            throw new BadRequestException($"pr_line_id '{missing[0]}' does not reference an existing purchase requisition line.");

        var notApproved = prLines.FirstOrDefault(l => l.Status != "APPROVED");
        if (notApproved is not null)
            throw new BadRequestException(
                $"pr_line_id '{notApproved.UUID}' does not reference an APPROVED purchase requisition. Current status: {notApproved.Status}.");

        return prLines.ToDictionary(l => l.UUID, l => new PrLineResolution(l.Id, l.TraceId));
    }

    private async Task<string> GenerateRequestNoAsync(int year)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable);

            var prefix = $"MIR-{year}-";
            var max    = await _db.MaterialIssueRequests
                .Where(m => m.RequestNo.StartsWith(prefix))
                .MaxAsync(m => (string?)m.RequestNo);

            int next = 1;
            if (max is not null)
            {
                var parts = max.Split('-');
                if (parts.Length == 3 && int.TryParse(parts[2], out var last))
                    next = last + 1;
            }

            var number = $"{prefix}{next:D5}";
            await tx.CommitAsync();
            return number;
        });
    }

    private static void ValidateTypeFields(string type, Guid? projectUuid, string? dept, string? mref)
    {
        switch (type)
        {
            case "PROJECT":
                if (!projectUuid.HasValue)
                    throw new UnprocessableEntityException("request_type=PROJECT requires project_uuid.");
                if (!string.IsNullOrWhiteSpace(dept) || !string.IsNullOrWhiteSpace(mref))
                    throw new UnprocessableEntityException(
                        "department and maintenance_ref must be omitted when request_type=PROJECT.");
                break;
            case "DEPARTMENT":
                if (string.IsNullOrWhiteSpace(dept))
                    throw new UnprocessableEntityException("request_type=DEPARTMENT requires department.");
                if (projectUuid.HasValue || !string.IsNullOrWhiteSpace(mref))
                    throw new UnprocessableEntityException(
                        "project_uuid and maintenance_ref must be omitted when request_type=DEPARTMENT.");
                break;
            case "MAINTENANCE":
                if (string.IsNullOrWhiteSpace(mref))
                    throw new UnprocessableEntityException("request_type=MAINTENANCE requires maintenance_ref.");
                if (projectUuid.HasValue || !string.IsNullOrWhiteSpace(dept))
                    throw new UnprocessableEntityException(
                        "project_uuid and department must be omitted when request_type=MAINTENANCE.");
                break;
        }
    }
}
