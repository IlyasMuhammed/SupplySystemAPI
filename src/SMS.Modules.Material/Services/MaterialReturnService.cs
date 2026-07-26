using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using System.Data;

namespace SMS.Modules.Material.Services;

internal sealed class MaterialReturnService : IMaterialReturnService
{
    private readonly MaterialDbContext       _db;
    private readonly InventoryDbContext      _inv;
    private readonly IInventoryLedgerService _ledger;
    private readonly ICostAllocationService  _cost;
    private readonly IBackgroundJobClient    _jobs;

    public MaterialReturnService(
        MaterialDbContext       db,
        InventoryDbContext      inv,
        IInventoryLedgerService ledger,
        ICostAllocationService  cost,
        IBackgroundJobClient    jobs)
    {
        _db     = db;
        _inv    = inv;
        _ledger = ledger;
        _cost   = cost;
        _jobs   = jobs;
    }

    // ── Returnable snapshot ───────────────────────────────────────────────────

    public async Task<MivReturnableResponse> GetReturnableAsync(Guid mivUuid)
    {
        var miv = await _db.MaterialIssueVouchers
            .Include(m => m.Lines).ThenInclude(l => l.BatchSerials)
            .FirstOrDefaultAsync(m => m.UUID == mivUuid)
            ?? throw new NotFoundException("MIV", mivUuid);

        if (miv.Status != "POSTED")
            throw new UnprocessableEntityException(
                $"Returns can only be made against POSTED vouchers. Current status: {miv.Status}.");

        var lineIds = miv.Lines.Select(l => l.Id).ToList();

        // How much of each line has already been returned (POSTED returns only)
        var alreadyReturned = await _db.MaterialReturnDetails
            .Where(d => lineIds.Contains(d.MivLineId) && d.Return.Status == "POSTED")
            .GroupBy(d => d.MivLineId)
            .Select(g => new { MivLineId = g.Key, Total = g.Sum(x => x.ReturnedQty) })
            .ToDictionaryAsync(x => x.MivLineId, x => x.Total);

        var lines = miv.Lines
            .Select(l =>
            {
                var returned   = alreadyReturned.TryGetValue(l.Id, out var r) ? r : 0m;
                var returnable = l.IssuedQty - returned;
                return new ReturnableLineModel
                {
                    LineUuid        = l.UUID,
                    ProductUuid     = l.ProductUuid,
                    ItemDescription = l.ItemDescription,
                    UnitOfMeasure   = l.UnitOfMeasure,
                    IssuedQty       = l.IssuedQty,
                    AlreadyReturned = returned,
                    ReturnableQty   = returnable,
                    UnitCost        = l.UnitCost,
                    InventoryItemId = l.InventoryItemId,
                    BatchSerials    = l.BatchSerials.Select(bs => new ReturnableBatchSerialModel
                    {
                        InventoryItemId = bs.InventoryItemId,
                        BatchNumber     = bs.BatchNumber,
                        SerialNumber    = bs.SerialNumber,
                        ExpiryDate      = bs.ExpiryDate,
                        IssuedQty       = bs.IssuedQty,
                        UnitCost        = bs.UnitCost
                    }).ToList()
                };
            })
            .Where(l => l.ReturnableQty > 0)
            .ToList();

        return new MivReturnableResponse
        {
            MivUuid   = miv.UUID,
            IssueNo   = miv.IssueNo,
            MivStatus = miv.Status,
            Lines     = lines
        };
    }

    // ── Create (DRAFT) ────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateReturnRequest req, int createdBy)
    {
        var miv = await _db.MaterialIssueVouchers
            .Include(m => m.Lines).ThenInclude(l => l.BatchSerials)
            .FirstOrDefaultAsync(m => m.UUID == req.MivUuid)
            ?? throw new NotFoundException("MIV", req.MivUuid);

        if (miv.Status != "POSTED")
            throw new UnprocessableEntityException(
                $"Returns can only be made against POSTED vouchers. Current status: {miv.Status}.");

        if (req.Lines.Count == 0)
            throw new UnprocessableEntityException("A return voucher must have at least one line.");

        var lineIds = miv.Lines.Select(l => l.Id).ToList();

        var alreadyReturned = await _db.MaterialReturnDetails
            .Where(d => lineIds.Contains(d.MivLineId) && d.Return.Status == "POSTED")
            .GroupBy(d => d.MivLineId)
            .Select(g => new { MivLineId = g.Key, Total = g.Sum(x => x.ReturnedQty) })
            .ToDictionaryAsync(x => x.MivLineId, x => x.Total);

        var returnDate = req.ReturnDate ?? DateTime.UtcNow;
        var details    = new List<MaterialReturnDetail>();

        foreach (var input in req.Lines)
        {
            var mivLine = miv.Lines.FirstOrDefault(l => l.UUID == input.MivLineUuid)
                ?? throw new BadRequestException(
                    $"MIV line {input.MivLineUuid} not found on this voucher.");

            if (input.ReturnedQty <= 0)
                throw new UnprocessableEntityException(
                    $"Line '{mivLine.ItemDescription}': returned qty must be greater than zero.");

            var alreadyRet    = alreadyReturned.TryGetValue(mivLine.Id, out var ar) ? ar : 0m;
            var returnableQty = mivLine.IssuedQty - alreadyRet;

            if (input.ReturnedQty > returnableQty)
                throw new UnprocessableEntityException(
                    $"Line '{mivLine.ItemDescription}': returned qty {input.ReturnedQty} exceeds " +
                    $"remaining returnable qty {returnableQty} " +
                    $"(issued: {mivLine.IssuedQty}, already returned: {alreadyRet}).");

            if (input.Condition is not ("GOOD" or "DAMAGED"))
                throw new UnprocessableEntityException(
                    $"Line '{mivLine.ItemDescription}': condition must be GOOD or DAMAGED. Got '{input.Condition}'.");

            // Resolve which inventory row to use: caller override → MIV line → first batch/serial
            var invItemId = (input.InventoryItemId.HasValue && input.InventoryItemId.Value > 0)
                ? input.InventoryItemId.Value
                : mivLine.InventoryItemId > 0
                    ? mivLine.InventoryItemId
                    : mivLine.BatchSerials
                        .OrderBy(bs => bs.ExpiryDate ?? DateTime.MaxValue)
                        .FirstOrDefault()?.InventoryItemId ?? 0;

            details.Add(new MaterialReturnDetail
            {
                UUID            = Guid.NewGuid(),
                MivLineId       = mivLine.Id,
                InventoryItemId = invItemId,
                ProductUuid     = mivLine.ProductUuid,
                ItemDescription = mivLine.ItemDescription,
                UnitOfMeasure   = mivLine.UnitOfMeasure,
                ReturnedQty     = input.ReturnedQty,
                Condition       = input.Condition,
                Reason          = input.Reason,
                UnitCost        = mivLine.UnitCost,
                LineValue       = input.ReturnedQty * mivLine.UnitCost,
                Notes           = input.Notes
            });
        }

        var returnNo = await GenerateReturnNoAsync(returnDate);

        var returnVoucher = new MaterialReturn
        {
            UUID        = Guid.NewGuid(),
            ReturnNo    = returnNo,
            MivId       = miv.Id,
            MirId       = miv.MirId,
            Status      = "DRAFT",
            ReturnDate  = returnDate,
            Notes       = req.Notes,
            CreatedBy   = createdBy,
            CreatedDate = DateTime.UtcNow,
            Lines       = details
        };

        _db.MaterialReturns.Add(returnVoucher);
        await _db.SaveChangesAsync();

        var mir = await _db.MaterialIssueRequests.FirstOrDefaultAsync(m => m.Id == miv.MirId);
        if (mir is not null)
        {
            var interfaceCode = mir.RequestType == "PROJECT" ? "MIR_PROJECT" : "MIR_GENERAL";
            var itemSummary   = string.Join(", ", details.Select(d => $"{d.ItemDescription} x{d.ReturnedQty:F4} ({d.Condition})"));

            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                mir.TraceId,
                new TimelineEvent("MATERIAL_RETURN_RECEIVED", interfaceCode, returnVoucher.UUID, returnVoucher.ReturnNo,
                    DateTime.UtcNow, createdBy, $"Received: {itemSummary}."),
                interfaceCode, mir.RequestNo));
        }

        return returnVoucher.UUID;
    }

    // ── Post ──────────────────────────────────────────────────────────────────

    public async Task PostAsync(Guid uuid, int postedBy)
    {
        var ret = await _db.MaterialReturns
            .Include(r => r.MIR).ThenInclude(m => m.Project)
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.UUID == uuid)
            ?? throw new NotFoundException("Return Voucher", uuid);

        if (ret.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT return vouchers can be posted. Current status: {ret.Status}.");

        var mir = ret.MIR;
        var now = DateTime.UtcNow;

        // FSD Addendum 24 (ML-003) — a return flows FROM wherever the material was issued to.
        var sourceType = mir.ProjectId.HasValue ? "PROJECT" : "DEPARTMENT";
        var sourceName = mir.ProjectId.HasValue ? mir.Project?.ProjectName : mir.Department;

        // Share a single SQL connection across both DbContexts to avoid MSDTC escalation.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await _db.Database.OpenConnectionAsync();
            var conn = _db.Database.GetDbConnection();
            await using var sqlTx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            _db.Database.UseTransaction(sqlTx);
            _inv.Database.SetDbConnection(conn);
            _inv.Database.UseTransaction(sqlTx);

            foreach (var line in ret.Lines)
            {
                if (line.Condition == "GOOD")
                {
                    // Restore inventory for GOOD-condition returns
                    var invItem = await _inv.InventoryItems.FindAsync(line.InventoryItemId)
                        ?? throw new UnprocessableEntityException(
                            $"Inventory item {line.InventoryItemId} not found for return line '{line.ItemDescription}'. " +
                            "Ensure the correct batch/serial was selected.");

                    invItem.QtyOnHand  += line.ReturnedQty;
                    invItem.LastUpdated = now;

                    var warehouseName = await _inv.Warehouses
                        .Where(w => w.Id == invItem.WarehouseId)
                        .Select(w => w.Name)
                        .FirstOrDefaultAsync() ?? string.Empty;

                    // Inventory ledger: MATERIAL_RETURN (QuantityIn — stock flows back in)
                    await _ledger.CreateEntryAsync(new LedgerEntryCommand
                    {
                        ProductId       = invItem.ProductId,
                        WarehouseId     = invItem.WarehouseId,
                        TransactionType = "MATERIAL_RETURN",
                        ReferenceType   = "RETURN_VOUCHER",
                        ReferenceId     = ret.UUID,
                        ReferenceNumber = ret.ReturnNo,
                        QuantityIn      = line.ReturnedQty,
                        UnitCost        = invItem.UnitCost ?? line.UnitCost,
                        Notes           = line.Reason,
                        CreatedBy       = postedBy,
                        // FSD Addendum 24 (ML-003): material flows back from the project/department
                        // into this warehouse.
                        SourceType      = sourceType,
                        SourceName      = sourceName,
                        DestinationType = "WAREHOUSE",
                        DestinationName = warehouseName
                    });

                    // Cost ledger: RETURN with negative quantity (credit to project/department)
                    await _cost.PostCostAsync(new CostPostCommand
                    {
                        RequestType     = mir.RequestType,
                        ProjectId       = mir.ProjectId,
                        MirId           = mir.Id,
                        Department      = mir.Department,
                        ProductUuid     = line.ProductUuid,
                        ItemDescription = line.ItemDescription,
                        TransactionType = "RETURN",
                        ReferenceType   = "RETURN_VOUCHER",
                        ReferenceId     = ret.UUID,
                        ReferenceNumber = ret.ReturnNo,
                        Quantity        = -line.ReturnedQty,
                        UnitCost        = line.UnitCost,
                        PostedDate      = now,
                        PostedBy        = postedBy,
                        Notes           = line.Reason
                    });
                }
                else  // DAMAGED — auto-create wastage for supervisor approval
                {
                    var wastageNo = await GenerateWastageNoAsync(now);

                    _db.Wastages.Add(new Wastage
                    {
                        UUID            = Guid.NewGuid(),
                        WastageNo       = wastageNo,
                        SourceType      = "DAMAGED_RETURN",
                        SourceUuid      = line.UUID,
                        MivLineId       = line.MivLineId,
                        MirId           = mir.Id,
                        ProductUuid     = line.ProductUuid,
                        ItemDescription = line.ItemDescription,
                        UnitOfMeasure   = line.UnitOfMeasure,
                        WastedQty       = line.ReturnedQty,
                        UnitCost        = line.UnitCost,
                        Amount          = line.ReturnedQty * line.UnitCost,
                        Reason          = line.Reason ?? "DAMAGED",
                        Status          = "PENDING_APPROVAL",
                        RecordedBy      = postedBy,
                        Notes           = line.Notes,
                        CreatedDate     = now
                    });
                }
            }

            ret.Status     = "POSTED";
            ret.PostedBy   = postedBy;
            ret.PostedDate = now;

            await _inv.SaveChangesAsync();
            await _db.SaveChangesAsync();
            await sqlTx.CommitAsync();
        });
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    public async Task CancelAsync(Guid uuid, int cancelledBy)
    {
        var ret = await _db.MaterialReturns
            .FirstOrDefaultAsync(r => r.UUID == uuid)
            ?? throw new NotFoundException("Return Voucher", uuid);

        if (ret.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT return vouchers can be cancelled. Current status: {ret.Status}.");

        ret.Status = "CANCELLED";
        await _db.SaveChangesAsync();
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    public async Task<ReturnDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var ret = await _db.MaterialReturns
            .Include(r => r.MIV)
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.UUID == uuid);

        if (ret == null) return null;

        return new ReturnDetailModel
        {
            UUID        = ret.UUID,
            ReturnNo    = ret.ReturnNo,
            Status      = ret.Status,
            MivUuid     = ret.MIV.UUID,
            IssueNo     = ret.MIV.IssueNo,
            ReturnDate  = ret.ReturnDate,
            Notes       = ret.Notes,
            CreatedBy   = ret.CreatedBy,
            CreatedDate = ret.CreatedDate,
            PostedBy    = ret.PostedBy,
            PostedDate  = ret.PostedDate,
            Lines       = ret.Lines.Select(l => new ReturnDetailLineModel
            {
                UUID            = l.UUID,
                ProductUuid     = l.ProductUuid,
                ItemDescription = l.ItemDescription,
                UnitOfMeasure   = l.UnitOfMeasure,
                ReturnedQty     = l.ReturnedQty,
                Condition       = l.Condition,
                Reason          = l.Reason,
                UnitCost        = l.UnitCost,
                LineValue       = l.LineValue,
                Notes           = l.Notes
            }).ToList()
        };
    }

    public async Task<PaginatedResponse<ReturnListItemModel>> GetListAsync(ReturnListFilter filter)
    {
        var query = _db.MaterialReturns.Include(r => r.MIV).AsQueryable();

        if (filter.MivUuid.HasValue)
        {
            var mivId = await _db.MaterialIssueVouchers
                .Where(m => m.UUID == filter.MivUuid.Value)
                .Select(m => (int?)m.Id)
                .FirstOrDefaultAsync();
            if (mivId.HasValue)
                query = query.Where(r => r.MivId == mivId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(r => r.Status == filter.Status);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(r => r.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ReturnListItemModel
            {
                UUID        = r.UUID,
                ReturnNo    = r.ReturnNo,
                IssueNo     = r.MIV.IssueNo,
                Status      = r.Status,
                ReturnDate  = r.ReturnDate,
                LineCount   = r.Lines.Count,
                TotalValue  = r.Lines.Sum(l => l.LineValue),
                CreatedDate = r.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<ReturnListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GenerateReturnNoAsync(DateTime date)
    {
        var year  = date.Year;
        var count = await _db.MaterialReturns
            .CountAsync(r => r.ReturnDate.Year == year);
        return $"MRV-{year}-{(count + 1):D5}";
    }

    private async Task<string> GenerateWastageNoAsync(DateTime date)
    {
        var year  = date.Year;
        var count = await _db.Wastages
            .CountAsync(w => w.CreatedDate.Year == year);
        return $"WST-{year}-{(count + 1):D5}";
    }
}
