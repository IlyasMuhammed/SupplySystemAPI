using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
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

// Batch/serial-aware Material Issue Voucher service.
// For batch-tracked products, MIV lines carry MivLineBatchSerial sub-rows (one per batch consumed).
// For serial-tracked products, each sub-row represents exactly one serial unit (IssuedQty = 1).
// Non-tracked products use the pre-existing InventoryItemId flow.

namespace SMS.Modules.Material.Services;

internal sealed class MivService : IMivService
{
    private readonly MaterialDbContext       _db;
    private readonly InventoryDbContext      _inv;
    private readonly IInventoryLedgerService _ledger;
    private readonly ICostAllocationService  _cost;
    private readonly IBackgroundJobClient    _jobs;

    public MivService(
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

    // ── Issuable snapshot ─────────────────────────────────────────────────────

    public async Task<MirIssuableResponse> GetIssuableAsync(Guid mirUuid)
    {
        var mir = await _db.MaterialIssueRequests
            .Include(m => m.Lines)
            .Include(m => m.Project)
            .FirstOrDefaultAsync(m => m.UUID == mirUuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", mirUuid);

        if (mir.Status is not ("APPROVED" or "PARTIALLY_APPROVED" or "PARTIALLY_ISSUED"))
            throw new UnprocessableEntityException(
                $"MIR must be APPROVED, PARTIALLY_APPROVED, or PARTIALLY_ISSUED to issue. Current status: {mir.Status}.");

        var lineIds = mir.Lines.Select(l => l.Id).ToList();

        var approvedQtys = await _db.MirLineApprovals
            .Where(a => a.MirId == mir.Id && lineIds.Contains(a.LineId))
            .GroupBy(a => a.LineId)
            .Select(g => new { LineId = g.Key, Qty = g.OrderByDescending(a => a.StepNumber).First().ApprovedQty })
            .ToDictionaryAsync(x => x.LineId, x => x.Qty);

        var productUuids = mir.Lines.Select(l => l.ProductUuid).Distinct().ToList();

        var unitCosts = await _inv.InventoryItems
            .Where(i => productUuids.Contains(i.Product.Uuid))
            .GroupBy(i => i.Product.Uuid)
            .Select(g => new { ProductUuid = g.Key, UnitCost = g.Max(i => (decimal?)i.UnitCost) ?? 0m })
            .ToDictionaryAsync(x => x.ProductUuid, x => x.UnitCost);

        var trackingFlags = await _inv.Products
            .Where(p => productUuids.Contains(p.Uuid))
            .Select(p => new { p.Uuid, p.IsBatchTracked, p.IsSerialTracked })
            .ToDictionaryAsync(x => x.Uuid, x => (x.IsBatchTracked, x.IsSerialTracked));

        var lines = mir.Lines
            .Where(l => approvedQtys.TryGetValue(l.Id, out var aq) && aq > 0)
            .OrderBy(l => l.LineNo)
            .Select(l =>
            {
                var approved = approvedQtys.TryGetValue(l.Id, out var aq) ? aq : 0m;
                var pending  = Math.Max(0, approved - l.IssuedQty);
                var unitCost = unitCosts.TryGetValue(l.ProductUuid, out var uc) ? uc : l.UnitCost;
                var flags    = trackingFlags.TryGetValue(l.ProductUuid, out var tf) ? tf : default;
                return new MirLineIssuableModel
                {
                    LineUuid         = l.UUID,
                    ProductUuid      = l.ProductUuid,
                    ItemDescription  = l.ItemDescription,
                    UnitOfMeasure    = l.UnitOfMeasure,
                    RequestedQty     = l.RequestedQty,
                    ApprovedQty      = approved,
                    IssuedQty        = l.IssuedQty,
                    PendingQty       = pending,
                    LineStatus       = l.LineStatus,
                    UnitCost         = unitCost,
                    AvailableToIssue = pending,
                    IsBatchTracked   = flags.IsBatchTracked,
                    IsSerialTracked  = flags.IsSerialTracked
                };
            })
            .Where(l => l.PendingQty > 0)
            .ToList();

        return new MirIssuableResponse
        {
            MirUuid    = mir.UUID,
            RequestNo  = mir.RequestNo,
            Status     = mir.Status,
            ProjectName = mir.Project?.ProjectName,
            Department = mir.Department,
            Lines      = lines
        };
    }

    // ── Create MIV (DRAFT) ────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateMivRequest req, int createdBy)
    {
        var mir = await _db.MaterialIssueRequests
            .Include(m => m.Lines)
            .FirstOrDefaultAsync(m => m.UUID == req.MirUuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", req.MirUuid);

        if (mir.Status is not ("APPROVED" or "PARTIALLY_APPROVED" or "PARTIALLY_ISSUED"))
            throw new UnprocessableEntityException(
                $"MIR must be in an approved or partially-issued state. Current: {mir.Status}.");

        if (req.Lines.Count == 0)
            throw new UnprocessableEntityException("An MIV must have at least one line.");

        var lineIds      = mir.Lines.Select(l => l.Id).ToList();
        var approvedQtys = await _db.MirLineApprovals
            .Where(a => a.MirId == mir.Id && lineIds.Contains(a.LineId))
            .GroupBy(a => a.LineId)
            .Select(g => new { LineId = g.Key, Qty = g.OrderByDescending(a => a.StepNumber).First().ApprovedQty })
            .ToDictionaryAsync(x => x.LineId, x => x.Qty);

        var reservations = await _db.StockReservations
            .Where(r => r.MirId == mir.Id && r.Status == "ACTIVE")
            .ToDictionaryAsync(r => r.MirLineId, r => r);

        var productUuids = mir.Lines.Select(l => l.ProductUuid).Distinct().ToList();

        // Unit cost and tracking flags — load by product UUID, covering all batch/serial rows
        var invItems = await _inv.InventoryItems
            .Where(i => productUuids.Contains(i.Product.Uuid))
            .Select(i => new { i.Id, ProductUuid = i.Product.Uuid, i.UnitCost,
                               i.BatchNumber, i.SerialNumber, i.ExpiryDate })
            .ToListAsync();
        var unitCostByProduct = invItems
            .GroupBy(i => i.ProductUuid)
            .ToDictionary(g => g.Key, g => g.Max(x => x.UnitCost) ?? 0m);
        var invItemById = invItems.ToDictionary(i => i.Id);

        var trackingFlagsCreate = await _inv.Products
            .Where(p => productUuids.Contains(p.Uuid))
            .Select(p => new { p.Uuid, p.IsBatchTracked, p.IsSerialTracked })
            .ToDictionaryAsync(x => x.Uuid, x => (x.IsBatchTracked, x.IsSerialTracked));

        // Check for serials already claimed in another DRAFT MIV (double-selection prevention)
        var allSerialIds = req.Lines.SelectMany(l => l.SerialSelections.Select(s => s.InventoryItemId)).ToList();
        if (allSerialIds.Count > 0)
        {
            var alreadyDraft = await _db.MivLineBatchSerials
                .Where(bs => allSerialIds.Contains(bs.InventoryItemId)
                          && bs.MivLine.MaterialIssueVoucher.Status == "DRAFT")
                .AnyAsync();
            if (alreadyDraft)
                throw new UnprocessableEntityException(
                    "One or more selected serial numbers are already reserved in another draft issue voucher. " +
                    "Post or cancel that voucher before issuing the same serial.");
        }

        var issueDate    = req.IssueDate ?? DateTime.UtcNow;
        var voucherLines = new List<MaterialIssueVoucherLine>();

        foreach (var input in req.Lines)
        {
            var mirLine = mir.Lines.FirstOrDefault(l => l.UUID == input.MirLineUuid)
                ?? throw new BadRequestException($"MIR line {input.MirLineUuid} not found on this MIR.");

            if (input.IssuedQty <= 0)
                throw new UnprocessableEntityException(
                    $"Line '{mirLine.ItemDescription}': issued qty must be greater than zero.");

            var approvedQty = approvedQtys.TryGetValue(mirLine.Id, out var aq) ? aq : 0m;
            var pendingQty  = Math.Max(0, approvedQty - mirLine.IssuedQty);

            if (input.IssuedQty > pendingQty)
                throw new UnprocessableEntityException(
                    $"Line '{mirLine.ItemDescription}': issued qty {input.IssuedQty} exceeds " +
                    $"remaining pending qty {pendingQty} (approved: {approvedQty}, already issued: {mirLine.IssuedQty}).");

            if (!reservations.TryGetValue(mirLine.Id, out var res) || res.ReservedQty <= 0)
                throw new UnprocessableEntityException(
                    $"Line '{mirLine.ItemDescription}': no active stock reservation found.");

            if (input.IssuedQty > res.ReservedQty)
                throw new UnprocessableEntityException(
                    $"Line '{mirLine.ItemDescription}': issued qty {input.IssuedQty} exceeds reservation {res.ReservedQty}.");

            var unitCost = unitCostByProduct.TryGetValue(mirLine.ProductUuid, out var uc) ? uc : mirLine.UnitCost;
            var flags    = trackingFlagsCreate.TryGetValue(mirLine.ProductUuid, out var tf) ? tf : default;

            // ── Batch/serial sub-row construction ──────────────────────────────────
            var batchSerials = new List<MivLineBatchSerial>();

            if (flags.IsBatchTracked && input.BatchSelections.Count > 0)
            {
                var totalSelected = input.BatchSelections.Sum(b => b.IssuedQty);
                if (Math.Abs(totalSelected - input.IssuedQty) > 0.0001m)
                    throw new UnprocessableEntityException(
                        $"Line '{mirLine.ItemDescription}': batch selection quantities ({totalSelected}) " +
                        $"must sum to issued qty ({input.IssuedQty}).");

                foreach (var bs in input.BatchSelections)
                {
                    if (!invItemById.TryGetValue(bs.InventoryItemId, out var batchItem))
                        throw new BadRequestException(
                            $"Inventory item {bs.InventoryItemId} not found.");

                    batchSerials.Add(new MivLineBatchSerial
                    {
                        UUID            = Guid.NewGuid(),
                        InventoryItemId = bs.InventoryItemId,
                        BatchNumber     = batchItem.BatchNumber,
                        ExpiryDate      = batchItem.ExpiryDate,
                        IssuedQty       = bs.IssuedQty,
                        UnitCost        = batchItem.UnitCost ?? unitCost
                    });
                }
            }
            else if (flags.IsSerialTracked && input.SerialSelections.Count > 0)
            {
                if (input.SerialSelections.Count != (int)input.IssuedQty)
                    throw new UnprocessableEntityException(
                        $"Line '{mirLine.ItemDescription}': you must select exactly {(int)input.IssuedQty} " +
                        $"serial number(s) (selected: {input.SerialSelections.Count}).");

                var distinctIds = input.SerialSelections.Select(s => s.InventoryItemId).Distinct().ToList();
                if (distinctIds.Count != input.SerialSelections.Count)
                    throw new UnprocessableEntityException(
                        $"Line '{mirLine.ItemDescription}': duplicate serial numbers selected.");

                foreach (var ss in input.SerialSelections)
                {
                    if (!invItemById.TryGetValue(ss.InventoryItemId, out var serialItem))
                        throw new BadRequestException(
                            $"Inventory item {ss.InventoryItemId} not found.");

                    batchSerials.Add(new MivLineBatchSerial
                    {
                        UUID            = Guid.NewGuid(),
                        InventoryItemId = ss.InventoryItemId,
                        SerialNumber    = serialItem.SerialNumber,
                        IssuedQty       = 1,
                        UnitCost        = serialItem.UnitCost ?? unitCost
                    });
                }
            }

            // For batch/serial tracked lines, InventoryItemId = 0 (sentinel — see BatchSerials).
            // For non-tracked lines, InventoryItemId = the reservation's inventory item.
            var lineInvItemId = batchSerials.Count > 0 ? 0 : res.InventoryItemId;

            var voucherLine = new MaterialIssueVoucherLine
            {
                UUID            = Guid.NewGuid(),
                MirLineId       = mirLine.Id,
                InventoryItemId = lineInvItemId,
                ProductUuid     = mirLine.ProductUuid,
                ItemDescription = mirLine.ItemDescription,
                UnitOfMeasure   = mirLine.UnitOfMeasure,
                IssuedQty       = input.IssuedQty,
                UnitCost        = unitCost,
                LineValue       = input.IssuedQty * unitCost,
                Notes           = input.Notes,
                BatchSerials    = batchSerials
            };
            voucherLines.Add(voucherLine);
        }

        var mivUuid = Guid.NewGuid();
        _db.MaterialIssueVouchers.Add(new MaterialIssueVoucher
        {
            UUID        = mivUuid,
            IssueNo     = await GenerateIssueNoAsync(issueDate.Year),
            MirId       = mir.Id,
            Status      = "DRAFT",
            IssuedTo    = req.IssuedTo?.Trim(),
            IssueDate   = issueDate,
            TotalValue  = voucherLines.Sum(l => l.LineValue),
            Notes       = req.Notes,
            CreatedBy   = createdBy,
            CreatedDate = DateTime.UtcNow,
            Lines       = voucherLines
        });

        await _db.SaveChangesAsync();
        return mivUuid;
    }

    // ── Get by UUID ───────────────────────────────────────────────────────────

    public async Task<MivDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var miv = await _db.MaterialIssueVouchers
            .Include(m => m.MaterialIssueRequest).ThenInclude(m => m.Project)
            .Include(m => m.Lines).ThenInclude(l => l.BatchSerials)
            .FirstOrDefaultAsync(m => m.UUID == uuid);

        if (miv is null) return null;

        var mirLineIds  = miv.Lines.Select(l => l.MirLineId).ToList();
        var lineUuidMap = await _db.MaterialIssueRequestDetails
            .Where(d => mirLineIds.Contains(d.Id))
            .Select(d => new { d.Id, d.UUID })
            .ToDictionaryAsync(x => x.Id, x => x.UUID);

        // Resolve warehouse names for every inventory item referenced by this MIV
        var allInvItemIds = miv.Lines
            .SelectMany(l => l.BatchSerials.Count > 0
                ? l.BatchSerials.Select(bs => bs.InventoryItemId)
                : Enumerable.Repeat(l.InventoryItemId, 1))
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var warehouseByInvItemId = allInvItemIds.Count > 0
            ? await _inv.InventoryItems
                .Where(i => allInvItemIds.Contains(i.Id))
                .Select(i => new { i.Id, i.Warehouse.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name)
            : new Dictionary<int, string>();

        var mir = miv.MaterialIssueRequest;

        return new MivDetailModel
        {
            UUID           = miv.UUID,
            IssueNo        = miv.IssueNo,
            MirUuid        = mir.UUID,
            MirRequestNo   = mir.RequestNo,
            MirProjectName = mir.Project?.ProjectName,
            Status         = miv.Status,
            IssuedTo       = miv.IssuedTo,
            IssueDate      = miv.IssueDate,
            TotalValue     = miv.TotalValue,
            Notes          = miv.Notes,
            CreatedBy      = miv.CreatedBy,
            CreatedDate    = miv.CreatedDate,
            PostedBy       = miv.PostedBy,
            PostedDate     = miv.PostedDate,
            Lines          = miv.Lines.Select(l =>
            {
                string? warehouseName;
                if (l.BatchSerials.Count > 0)
                {
                    var names = l.BatchSerials
                        .Select(bs => warehouseByInvItemId.TryGetValue(bs.InventoryItemId, out var n) ? n : null)
                        .OfType<string>()
                        .Distinct();
                    warehouseName = string.Join(", ", names);
                    if (string.IsNullOrEmpty(warehouseName)) warehouseName = null;
                }
                else
                {
                    warehouseName = l.InventoryItemId > 0 &&
                                    warehouseByInvItemId.TryGetValue(l.InventoryItemId, out var wn) ? wn : null;
                }

                return new MivLineModel
                {
                    UUID            = l.UUID,
                    MirLineUuid     = lineUuidMap.TryGetValue(l.MirLineId, out var lu) ? lu : Guid.Empty,
                    ProductUuid     = l.ProductUuid,
                    ItemDescription = l.ItemDescription,
                    UnitOfMeasure   = l.UnitOfMeasure,
                    IssuedQty       = l.IssuedQty,
                    UnitCost        = l.UnitCost,
                    LineValue       = l.LineValue,
                    Notes           = l.Notes,
                    WarehouseName   = warehouseName,
                    BatchSerials    = l.BatchSerials.Select(bs => new MivLineBatchSerialModel
                    {
                        UUID            = bs.UUID,
                        InventoryItemId = bs.InventoryItemId,
                        BatchNumber     = bs.BatchNumber,
                        SerialNumber    = bs.SerialNumber,
                        ExpiryDate      = bs.ExpiryDate,
                        IssuedQty       = bs.IssuedQty,
                        UnitCost        = bs.UnitCost
                    }).ToList()
                };
            }).ToList()
        };
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<MivListItemModel>> GetListAsync(MivListFilter filter)
    {
        var query = _db.MaterialIssueVouchers
            .Include(m => m.MaterialIssueRequest)
            .Include(m => m.Lines)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(m => m.Status == filter.Status);

        if (filter.MirUuid.HasValue)
        {
            var mirId = await _db.MaterialIssueRequests
                .Where(m => m.UUID == filter.MirUuid.Value)
                .Select(m => (int?)m.Id)
                .FirstOrDefaultAsync();
            if (mirId.HasValue)
                query = query.Where(m => m.MirId == mirId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(m => m.IssueNo.ToLower().Contains(term)
                                  || (m.IssuedTo != null && m.IssuedTo.ToLower().Contains(term)));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(m => m.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new MivListItemModel
            {
                UUID         = m.UUID,
                IssueNo      = m.IssueNo,
                MirRequestNo = m.MaterialIssueRequest.RequestNo,
                MirUuid      = m.MaterialIssueRequest.UUID,
                Status       = m.Status,
                IssuedTo     = m.IssuedTo,
                IssueDate    = m.IssueDate,
                TotalValue   = m.TotalValue,
                TotalLines   = m.Lines.Count,
                CreatedDate  = m.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<MivListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    // ── Post (all 7 atomic effects) ───────────────────────────────────────────

    public async Task PostAsync(Guid uuid, int postedBy)
    {
        var miv = await _db.MaterialIssueVouchers
            .Include(m => m.MaterialIssueRequest).ThenInclude(m => m.Lines)
            .Include(m => m.MaterialIssueRequest).ThenInclude(m => m.Project)
            .Include(m => m.Lines).ThenInclude(l => l.BatchSerials)
            .FirstOrDefaultAsync(m => m.UUID == uuid)
            ?? throw new NotFoundException("MIV", uuid);

        if (miv.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT vouchers can be posted. Current status: {miv.Status}.");

        var mir        = miv.MaterialIssueRequest;
        var mirLineIds = miv.Lines.Select(l => l.MirLineId).ToList();

        // Pre-load StockReservations (effect 3)
        var reservations = await _db.StockReservations
            .Where(r => mirLineIds.Contains(r.MirLineId) && r.Status == "ACTIVE")
            .ToDictionaryAsync(r => r.MirLineId, r => r);

        // Pre-load MIRLines (effects 4, 5) — same entities tracked in mir.Lines
        var mirLineMap = mir.Lines
            .Where(l => mirLineIds.Contains(l.Id))
            .ToDictionary(l => l.Id, l => l);

        // Approved qty per line (effect 5 ceiling + effect 6)
        var allLineIds   = mir.Lines.Select(l => l.Id).ToList();
        var approvedQtys = await _db.MirLineApprovals
            .Where(a => a.MirId == mir.Id && allLineIds.Contains(a.LineId))
            .GroupBy(a => a.LineId)
            .Select(g => new { LineId = g.Key, Qty = g.OrderByDescending(a => a.StepNumber).First().ApprovedQty })
            .ToDictionaryAsync(x => x.LineId, x => x.Qty);

        // ── MIC-008 pre-loads ─────────────────────────────────────────────────

        // Resolve site warehouse integer Id (null when project has no site warehouse)
        int? siteWarehouseIntId = null;
        if (mir.Project?.SiteWarehouseId.HasValue == true)
        {
            siteWarehouseIntId = await _inv.Warehouses
                .Where(w => w.Uuid == mir.Project.SiteWarehouseId.Value)
                .Select(w => (int?)w.Id)
                .FirstOrDefaultAsync();
        }

        // Direct-consumption product set
        var productUuids        = miv.Lines.Select(l => l.ProductUuid).Distinct().ToList();
        var directConsumptionSet = (await _inv.Products
            .Where(p => productUuids.Contains(p.Uuid) && p.IsDirectConsumption)
            .Select(p => p.Uuid)
            .ToListAsync())
            .ToHashSet();

        // Cache for site warehouse InventoryItems (avoids duplicate-add for same product)
        var siteItemCache = new Dictionary<int, InventoryItem>();

        // FSD Addendum 24 (ML-003) — resolved once per posting, reused across every line.
        var warehouseNameCache = new Dictionary<int, string>();
        async Task<string> WarehouseNameAsync(int warehouseId)
        {
            if (!warehouseNameCache.TryGetValue(warehouseId, out var name))
            {
                name = await _inv.Warehouses.Where(w => w.Id == warehouseId)
                    .Select(w => w.Name).FirstOrDefaultAsync() ?? string.Empty;
                warehouseNameCache[warehouseId] = name;
            }
            return name;
        }
        // Destination when material issues straight to a project/department (no site warehouse).
        var issueDestinationType = mir.ProjectId.HasValue ? "PROJECT" : "DEPARTMENT";
        var issueDestinationName = mir.ProjectId.HasValue ? mir.Project?.ProjectName : mir.Department;
        var siteWarehouseName    = siteWarehouseIntId.HasValue
            ? await WarehouseNameAsync(siteWarehouseIntId.Value)
            : null;

        // Counter for MCR number generation within this posting (avoids per-line DB query)
        var mcrYear  = DateTime.UtcNow.Year;
        var mcrCount = await _db.MaterialConsumptions
            .CountAsync(c => c.CreatedDate.Year == mcrYear);

        var now = DateTime.UtcNow;

        // Wrap all effects in the EF execution strategy so EnableRetryOnFailure does not
        // conflict with the user-managed transaction.  Both DbContexts share a single
        // SqlConnection so the transaction is local (no MSDTC escalation).
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await _db.Database.OpenConnectionAsync();
            var conn = _db.Database.GetDbConnection();
            await using var sqlTx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            _db.Database.UseTransaction(sqlTx);
            _inv.Database.SetDbConnection(conn);
            _inv.Database.UseTransaction(sqlTx);

        foreach (var line in miv.Lines)
        {
            int lineProductId = 0;
            int mainWarehouseIdForLine = 0;

            if (line.BatchSerials.Count > 0)
            {
                // ── Batch/serial tracked flow ────────────────────────────────────
                foreach (var bs in line.BatchSerials)
                {
                    var invItem = await _inv.InventoryItems.FindAsync(bs.InventoryItemId)
                        ?? throw new UnprocessableEntityException(
                            $"Inventory item {bs.InventoryItemId} not found for batch/serial on " +
                            $"line '{line.ItemDescription}'.");

                    if (invItem.QtyOnHand < bs.IssuedQty)
                        throw new UnprocessableEntityException(
                            $"Line '{line.ItemDescription}'" +
                            (bs.BatchNumber  != null ? $" batch '{bs.BatchNumber}'"  : "") +
                            (bs.SerialNumber != null ? $" serial '{bs.SerialNumber}'" : "") +
                            $": on-hand qty {invItem.QtyOnHand:F4} is less than issued qty {bs.IssuedQty:F4}.");

                    invItem.QtyOnHand  -= bs.IssuedQty;
                    invItem.QtyReserved = Math.Max(0, invItem.QtyReserved - bs.IssuedQty);
                    invItem.LastUpdated = now;
                    lineProductId       = invItem.ProductId;
                    mainWarehouseIdForLine = invItem.WarehouseId;

                    // Effect 7: one ledger entry per batch/serial consumed
                    // When a site warehouse is configured, the main-warehouse entry becomes TRANSFER_OUT
                    var mainTxType = siteWarehouseIntId.HasValue ? "TRANSFER_OUT" : "MATERIAL_ISSUE";
                    var notes      = siteWarehouseIntId.HasValue
                        ? $"Transfer to site warehouse: {line.ItemDescription}"
                        : $"Material issue: {line.ItemDescription}";
                    if (bs.BatchNumber  != null) notes += $" [Batch: {bs.BatchNumber}]";
                    if (bs.SerialNumber != null) notes += $" [S/N: {bs.SerialNumber}]";

                    await _ledger.CreateEntryAsync(new LedgerEntryCommand
                    {
                        ProductId       = invItem.ProductId,
                        WarehouseId     = invItem.WarehouseId,
                        TransactionType = mainTxType,
                        ReferenceType   = "MIV",
                        ReferenceId     = miv.UUID,
                        ReferenceNumber = miv.IssueNo,
                        QuantityOut     = bs.IssuedQty,
                        UnitCost        = bs.UnitCost,
                        Notes           = notes,
                        CreatedBy       = postedBy,
                        // FSD Addendum 24 (ML-003): straight issue goes to the project/department;
                        // when a site warehouse exists, this leg only goes as far as that warehouse.
                        SourceType      = "WAREHOUSE",
                        SourceName      = await WarehouseNameAsync(invItem.WarehouseId),
                        DestinationType = siteWarehouseIntId.HasValue ? "WAREHOUSE" : issueDestinationType,
                        DestinationName = siteWarehouseIntId.HasValue ? siteWarehouseName : issueDestinationName
                    });
                }
            }
            else
            {
                // ── Non-tracked flow ─────────────────────────────────────────────
                var invItem = await _inv.InventoryItems.FindAsync(line.InventoryItemId)
                    ?? throw new UnprocessableEntityException(
                        $"Inventory item {line.InventoryItemId} not found. Cannot post MIV.");

                if (invItem.QtyOnHand < line.IssuedQty)
                    throw new UnprocessableEntityException(
                        $"Line '{line.ItemDescription}': on-hand qty {invItem.QtyOnHand:F4} is less than " +
                        $"issued qty {line.IssuedQty:F4}. Cannot post.");

                invItem.QtyOnHand  -= line.IssuedQty;
                invItem.QtyReserved = Math.Max(0, invItem.QtyReserved - line.IssuedQty);
                invItem.LastUpdated = now;
                lineProductId       = invItem.ProductId;
                mainWarehouseIdForLine = invItem.WarehouseId;

                var mainTxType = siteWarehouseIntId.HasValue ? "TRANSFER_OUT" : "MATERIAL_ISSUE";
                var mainNotes  = siteWarehouseIntId.HasValue
                    ? $"Transfer to site warehouse: {line.ItemDescription}"
                    : $"Material issue: {line.ItemDescription}";

                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    ProductId       = invItem.ProductId,
                    WarehouseId     = invItem.WarehouseId,
                    TransactionType = mainTxType,
                    ReferenceType   = "MIV",
                    ReferenceId     = miv.UUID,
                    ReferenceNumber = miv.IssueNo,
                    QuantityOut     = line.IssuedQty,
                    UnitCost        = line.UnitCost,
                    Notes           = mainNotes,
                    CreatedBy       = postedBy,
                    // FSD Addendum 24 (ML-003) — see the batch/serial branch above for the rationale.
                    SourceType      = "WAREHOUSE",
                    SourceName      = await WarehouseNameAsync(invItem.WarehouseId),
                    DestinationType = siteWarehouseIntId.HasValue ? "WAREHOUSE" : issueDestinationType,
                    DestinationName = siteWarehouseIntId.HasValue ? siteWarehouseName : issueDestinationName
                });
            }

            // ── Effect (site warehouse TRANSFER_IN) ──────────────────────────────
            // When the MIR's project has a site warehouse, write a TRANSFER_IN entry
            // and increment the site warehouse InventoryItem QtyOnHand.
            if (siteWarehouseIntId.HasValue && lineProductId > 0)
            {
                if (!siteItemCache.TryGetValue(lineProductId, out var siteItem))
                {
                    siteItem = await _inv.InventoryItems.FirstOrDefaultAsync(i =>
                        i.ProductId   == lineProductId &&
                        i.WarehouseId == siteWarehouseIntId.Value &&
                        i.BatchNumber  == null &&
                        i.SerialNumber == null);

                    if (siteItem == null)
                    {
                        siteItem = new InventoryItem
                        {
                            ProductId   = lineProductId,
                            WarehouseId = siteWarehouseIntId.Value,
                            QtyOnHand   = 0,
                            QtyReserved = 0,
                            QtyOnOrder  = 0,
                            LastUpdated = now
                        };
                        _inv.InventoryItems.Add(siteItem);
                    }
                    siteItemCache[lineProductId] = siteItem;
                }

                siteItem.QtyOnHand  += line.IssuedQty;
                siteItem.LastUpdated = now;

                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    ProductId       = lineProductId,
                    WarehouseId     = siteWarehouseIntId.Value,
                    TransactionType = "TRANSFER_IN",
                    ReferenceType   = "MIV",
                    ReferenceId     = miv.UUID,
                    ReferenceNumber = miv.IssueNo,
                    QuantityIn      = line.IssuedQty,
                    UnitCost        = line.UnitCost,
                    Notes           = $"Site warehouse receipt: {line.ItemDescription}",
                    CreatedBy       = postedBy,
                    // FSD Addendum 24 (ML-003): the paired end of the same TRANSFER_OUT movement.
                    SourceType      = "WAREHOUSE",
                    SourceName      = mainWarehouseIdForLine > 0 ? await WarehouseNameAsync(mainWarehouseIdForLine) : null,
                    DestinationType = "WAREHOUSE",
                    DestinationName = siteWarehouseName
                });
            }

            // ── Effect (direct consumption auto-record) ──────────────────────────
            // When the product is flagged IsDirectConsumption, auto-create a MaterialConsumption
            // so the item shows balance = 0 immediately after issue (no manual step required).
            if (directConsumptionSet.Contains(line.ProductUuid))
            {
                mcrCount++;
                _db.MaterialConsumptions.Add(new Domain.MaterialConsumption
                {
                    UUID            = Guid.NewGuid(),
                    ConsumptionNo   = $"MCR-{mcrYear}-{mcrCount:D5}",
                    MivLineId       = line.Id,
                    MirId           = mir.Id,
                    ProductUuid     = line.ProductUuid,
                    ItemDescription = line.ItemDescription,
                    UnitOfMeasure   = line.UnitOfMeasure,
                    ConsumedQty     = line.IssuedQty,
                    SourceType      = "DIRECT",
                    ConsumedDate    = now,
                    ConsumedBy      = postedBy,
                    CreatedDate     = now
                });
            }

            // Effect 3: Decrement StockReservation.ReservedQty (all tracking modes)
            if (reservations.TryGetValue(line.MirLineId, out var res))
            {
                res.ReservedQty -= line.IssuedQty;
                if (res.ReservedQty <= 0)
                {
                    res.ReservedQty   = 0;
                    res.Status        = "CONSUMED";
                    res.ReleasedAt    = now;
                    res.ReleaseReason = $"Consumed by MIV {miv.IssueNo}";
                }
            }

            // Effects 4 + 5: Update MIRLine issued tracking (all tracking modes)
            if (mirLineMap.TryGetValue(line.MirLineId, out var mirLine))
            {
                mirLine.IssuedQty += line.IssuedQty;
                var approvedQty    = approvedQtys.TryGetValue(mirLine.Id, out var aq) ? aq : mirLine.RequestedQty;
                mirLine.PendingQty = Math.Max(0, approvedQty - mirLine.IssuedQty);
                mirLine.LineStatus = mirLine.IssuedQty >= approvedQty ? "FULLY_ISSUED" : "PARTIALLY_ISSUED";
            }

            // Cost allocation (Effect 8): post to ProjectCostLedger or DepartmentCostLedger.
            await _cost.PostCostAsync(new CostPostCommand
            {
                RequestType     = mir.RequestType,
                ProjectId       = mir.ProjectId,
                MirId           = mir.Id,
                Department      = mir.Department,
                ProductUuid     = line.ProductUuid,
                ItemDescription = line.ItemDescription,
                TransactionType = "ISSUE",
                ReferenceType   = "MIV",
                ReferenceId     = miv.UUID,
                ReferenceNumber = miv.IssueNo,
                Quantity        = line.IssuedQty,
                UnitCost        = line.UnitCost,
                PostedDate      = now,
                PostedBy        = postedBy,
                Notes           = $"Issue from MIV {miv.IssueNo}: {line.ItemDescription}"
            });
        }

        // Effect 6: Recompute MIR header status from updated mir.Lines
        var issuableLineIds = approvedQtys
            .Where(kv => kv.Value > 0)
            .Select(kv => kv.Key)
            .ToHashSet();

        bool allFullyIssued = mir.Lines
            .Where(l => issuableLineIds.Contains(l.Id))
            .All(l => l.LineStatus == "FULLY_ISSUED");

        mir.Status       = allFullyIssued ? "FULLY_ISSUED" : "PARTIALLY_ISSUED";
        mir.ModifiedDate = now;

        miv.Status     = "POSTED";
        miv.PostedBy   = postedBy;
        miv.PostedDate = now;

            // Save Material side: MIV, MIR, MIRLines, StockReservations, MaterialConsumptions
            await _db.SaveChangesAsync();

            // Save Inventory side: InventoryItems, LedgerEntries
            await _inv.SaveChangesAsync();

            await sqlTx.CommitAsync();
        });

        var itemSummary = string.Join(", ", miv.Lines.Select(l => $"{l.ItemDescription} x{l.IssuedQty:F4}"));
        var issueNotes  = $"Issued to {(string.IsNullOrWhiteSpace(miv.IssuedTo) ? "—" : miv.IssuedTo)}: {itemSummary}.";
        var mirInterfaceCode = mir.RequestType == "PROJECT" ? "MIR_PROJECT" : "MIR_GENERAL";

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            mir.TraceId,
            new TimelineEvent("MIR_ISSUED", mirInterfaceCode, mir.UUID, mir.RequestNo, DateTime.UtcNow, postedBy, issueNotes),
            mirInterfaceCode, mir.RequestNo));
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    public async Task CancelAsync(Guid uuid, int cancelledBy)
    {
        var miv = await _db.MaterialIssueVouchers
            .FirstOrDefaultAsync(m => m.UUID == uuid)
            ?? throw new NotFoundException("MIV", uuid);

        if (miv.Status != "DRAFT")
            throw new UnprocessableEntityException("Only DRAFT vouchers can be cancelled.");

        miv.Status = "CANCELLED";
        await _db.SaveChangesAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GenerateIssueNoAsync(int year)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable);

            var prefix = $"MIV-{year}-";
            var max    = await _db.MaterialIssueVouchers
                .Where(m => m.IssueNo.StartsWith(prefix))
                .MaxAsync(m => (string?)m.IssueNo);

            int next = 1;
            if (max is not null)
            {
                var parts = max.Split('-');
                if (parts.Length == 3 && int.TryParse(parts[2], out var last))
                    next = last + 1;
            }

            await tx.CommitAsync();
            return $"{prefix}{next:D5}";
        });
    }
}
