using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Domain;
using SMS.Modules.Reports.Models;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Reports.Repositories;

internal sealed class ReportsRepository : IReportsRepository
{
    private readonly ReportsDbContext   _db;
    private readonly DemandDbContext    _demand;
    private readonly WarehouseDbContext _warehouse;
    private readonly InventoryDbContext _inventory;
    private readonly FinanceDbContext   _finance;
    private readonly LogisticsDbContext _logistics;
    private readonly SuppliersDbContext _suppliers;
    private readonly MaterialDbContext  _material;
    private readonly IUserQueryService  _userQuery;
    private readonly ITimelineService   _timeline;

    public ReportsRepository(
        ReportsDbContext   db,
        DemandDbContext    demand,
        WarehouseDbContext warehouse,
        InventoryDbContext inventory,
        FinanceDbContext   finance,
        LogisticsDbContext logistics,
        SuppliersDbContext suppliers,
        MaterialDbContext  material,
        IUserQueryService  userQuery,
        ITimelineService   timeline)
    {
        _db        = db;
        _demand    = demand;
        _warehouse = warehouse;
        _inventory = inventory;
        _finance   = finance;
        _logistics = logistics;
        _suppliers = suppliers;
        _material  = material;
        _userQuery = userQuery;
        _timeline  = timeline;
    }

    // ── KPI Dashboard ─────────────────────────────────────────────────────────
    //
    // Queries are split into four groups, one per DbContext, and fired in
    // parallel with Task.WhenAll.  Each group runs its own queries sequentially
    // (EF Core DbContext is not thread-safe) but the groups execute concurrently,
    // reducing total latency from Σ(all queries) to max(slowest group).

    public async Task<KpiDashboardModel> GetKpiDashboardAsync()
    {
        var tDemand    = FetchDemandKpiDataAsync();
        var tWarehouse = FetchWarehouseKpiDataAsync();
        var tInventory = FetchInventoryKpiDataAsync();
        var tFinance   = FetchFinanceKpiDataAsync();

        await Task.WhenAll(tDemand, tWarehouse, tInventory, tFinance);

        var d   = tDemand.Result;
        var w   = tWarehouse.Result;
        var inv = tInventory.Result;
        var fin = tFinance.Result;

        // KPI 2 cross-join in memory (both sides now available)
        var poDeliveryMap = d.AllPos.ToDictionary(p => p.Uuid, p => p.DeliveryDate);
        var onTimeCount   = w.ApprovedGrns.Count(g =>
            poDeliveryMap.TryGetValue(g.PoUuid, out var dd) && dd.HasValue && g.ReceivedAt <= dd.Value);

        return new KpiDashboardModel
        {
            PoCycleTimeDays            = d.CycleTimes.Count > 0 ? Math.Round(d.CycleTimes.Average(), 1) : 0,
            SupplierOnTimeDeliveryRate = w.ApprovedGrns.Count > 0
                ? Math.Round((double)onTimeCount / w.ApprovedGrns.Count * 100, 1) : 0,
            PoFillRate                 = d.PoLineTotal > 0
                ? Math.Round((double)d.PoLineFilled / d.PoLineTotal * 100, 1) : 0,
            StockTurnoverRatio         = inv.InventoryValue > 0
                ? Math.Round((double)(d.AnnualPoSpend / inv.InventoryValue), 2) : 0,
            InventoryAccuracy          = inv.InventoryAccuracy,
            InvoiceProcessingTimeDays  = fin.InvoiceProcessingTimeDays,
            ThreeWayMatchRate          = fin.ThreeWayMatchRate,
            BudgetVariancePercent      = d.PrEstimate > 0
                ? Math.Round((double)((d.PoActual - d.PrEstimate) / d.PrEstimate) * 100, 1) : 0,
            GrnRejectionRate           = w.TotalReceived > 0
                ? Math.Round((double)w.TotalRejected / w.TotalReceived * 100, 1) : 0,
            ReorderTriggerCount        = inv.ReorderTriggerCount
        };
    }
    // ── Private data containers ───────────────────────────────────────────────
    private sealed record PoEntry(Guid Uuid, DateTime? DeliveryDate);
    private sealed record GrnEntry(Guid PoUuid, DateTime ReceivedDate)
    {
        public DateTime ReceivedAt { get; internal set; }
    }

    private sealed record DemandKpiData(
        List<double>   CycleTimes,
        List<PoEntry>  AllPos,
        int            PoLineTotal,
        int            PoLineFilled,
        decimal        AnnualPoSpend,
        decimal        PrEstimate,
        decimal        PoActual);

    private sealed record WarehouseKpiData(
        List<GrnEntry> ApprovedGrns,
        double         TotalReceived,
        double         TotalRejected);

    private sealed record InventoryKpiData(
        decimal        InventoryValue,
        double         InventoryAccuracy,
        int            ReorderTriggerCount);

    private sealed record FinanceKpiData(
        double         InvoiceProcessingTimeDays,
        double         ThreeWayMatchRate);

    // ── Demand group (runs sequentially on _demand DbContext) ─────────────────

    private async Task<DemandKpiData> FetchDemandKpiDataAsync()
    {
        var yearAgo = DateTime.UtcNow.AddYears(-1);

        // Single pass over all POs — reused for KPIs 1, 2, 4, 8
        var allPos = await _demand.PurchaseOrders
            .Where(po => !po.IsDelete)
            .Include(po => po.PrLinks)
            .ToListAsync();

        // PRs needed only for cycle-time KPI
        var linkedPrUuids = allPos.SelectMany(po => po.PrLinks.Select(l => l.PrUuid)).Distinct().ToList();
        Dictionary<Guid, DateTime> prDateMap = new();
        if (linkedPrUuids.Count > 0)
        {
            var prs = await _demand.PurchaseRequisitions
                .Where(pr => linkedPrUuids.Contains(pr.UUID))
                .Select(pr => new { pr.UUID, pr.CreatedDate })
                .ToListAsync();
            prDateMap = prs.ToDictionary(p => p.UUID, p => p.CreatedDate);
        }

        var cycleTimes = allPos
            .Where(po => po.PrLinks.Any())
            .Select(po => {
                var prUuid = po.PrLinks.Select(l => l.PrUuid).FirstOrDefault();
                return prDateMap.TryGetValue(prUuid, out var prDate)
                    ? (double?)(po.CreatedDate - prDate).TotalDays
                    : null;
            })
            .Where(d => d.HasValue && d.Value >= 0)
            .Select(d => d!.Value)
            .ToList();

        // PO Lines — KPI 3
        var poLines = await _demand.PurchaseOrderLines
            .Where(l => l.Quantity > 0)
            .Select(l => new { l.Quantity, l.QtyReceived })
            .ToListAsync();

        // PR estimate — KPI 8 (can't derive from PO data)
        var prEstimate = await _demand.PurchaseRequisitions
            .Where(pr => !pr.IsDelete && pr.Status != "DRAFT")
            .SumAsync(pr => (decimal?)pr.EstimatedTotal) ?? 0m;

        // Remaining values computed from already-loaded POs (no extra round-trips)
        var annualPoSpend = allPos
            .Where(po => po.CreatedDate >= yearAgo && (po.Status == "RECEIVED" || po.Status == "CLOSED"))
            .Sum(po => po.TotalAmount);

        var poActual = allPos
            .Where(po => po.Status != "DRAFT" && po.Status != "CANCELLED")
            .Sum(po => po.TotalAmount);

        return new DemandKpiData(
            CycleTimes:    cycleTimes,
            AllPos:        allPos.Select(po => new PoEntry(po.UUID, po.DeliveryDate)).ToList(),
            PoLineTotal:   poLines.Count,
            PoLineFilled:  poLines.Count(l => l.QtyReceived >= l.Quantity),
            AnnualPoSpend: annualPoSpend,
            PrEstimate:    prEstimate,
            PoActual:      poActual);
    }

    // ── Warehouse group ───────────────────────────────────────────────────────

    private async Task<WarehouseKpiData> FetchWarehouseKpiDataAsync()
    {
        var approvedGrns = await _warehouse.Grns
            .Where(g => !g.IsDelete && g.Status == "APPROVED")
            .Select(g => new { g.PoUuid, g.ReceivedAt })
            .ToListAsync();

        var grnLines = await _warehouse.GrnLines
            .Select(l => new { l.QtyReceived, l.QtyRejected })
            .ToListAsync();

        return new WarehouseKpiData(
            ApprovedGrns:  approvedGrns.Select(g => new GrnEntry(g.PoUuid, g.ReceivedAt)).ToList(),
            TotalReceived: grnLines.Sum(l => (double)l.QtyReceived),
            TotalRejected: grnLines.Sum(l => (double)l.QtyRejected));
    }

    // ── Inventory group ───────────────────────────────────────────────────────

    private async Task<InventoryKpiData> FetchInventoryKpiDataAsync()
    {
        var inventoryValue = await _inventory.InventoryItems
            .SumAsync(i => (decimal?)(i.QtyOnHand * i.UnitCost)) ?? 0m;

        var totalItems = await _inventory.InventoryItems.CountAsync();
        var problemItems = await _inventory.StockAdjustments
            .Where(a => a.Status == "REJECTED")
            .Select(a => a.InventoryItemId)
            .Distinct()
            .CountAsync();

        // Server-side COUNT instead of loading all rows — fixes the previous full-table scan
        var reorderCount = await _inventory.InventoryItems
            .Where(i => i.Product != null && i.QtyOnHand <= i.Product.ReorderPoint)
            .CountAsync();

        var accuracy = totalItems > 0
            ? Math.Round((double)(totalItems - problemItems) / totalItems * 100, 1)
            : 100.0;

        return new InventoryKpiData(inventoryValue, accuracy, reorderCount);
    }

    // ── Finance group ─────────────────────────────────────────────────────────

    private async Task<FinanceKpiData> FetchFinanceKpiDataAsync()
    {
        var approvedInvoices = await _finance.Invoices
            .Where(i => !i.IsDelete && i.ApprovedAt.HasValue)
            .Select(i => new { i.ReceivedDate, i.ApprovedAt })
            .ToListAsync();

        var processingDays = approvedInvoices.Count > 0
            ? approvedInvoices
                .Select(i => (i.ApprovedAt!.Value - i.ReceivedDate).TotalDays)
                .Where(d => d >= 0)
                .DefaultIfEmpty(0)
                .Average()
            : 0.0;

        // Two COUNT queries — both on _finance, run sequentially (same DbContext)
        var invoiceTotal = await _finance.Invoices.CountAsync(i => !i.IsDelete && i.GrnUuid.HasValue);
        var matchedTotal = await _finance.Invoices
            .CountAsync(i => !i.IsDelete && i.GrnUuid.HasValue && i.MatchStatus == "Matched");

        var matchRate = invoiceTotal > 0
            ? Math.Round((double)matchedTotal / invoiceTotal * 100, 1)
            : 0.0;

        return new FinanceKpiData(Math.Round(processingDays, 1), matchRate);
    }

    // ── Supplier Performance ──────────────────────────────────────────────────

    public async Task<List<SupplierPerformanceItem>> GetSupplierPerformanceAsync(ReportDateFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var pos = await _demand.PurchaseOrders
            .Where(po => !po.IsDelete && (!from.HasValue || po.CreatedDate >= from) && (!to.HasValue || po.CreatedDate <= to))
            .Select(po => new { po.UUID, po.SupplierId, po.SupplierName, po.TotalAmount, po.DeliveryDate })
            .ToListAsync();

        var grnQuery = _warehouse.Grns.Where(g => !g.IsDelete);
        if (from.HasValue) grnQuery = grnQuery.Where(g => g.ReceivedAt >= from);
        if (to.HasValue)   grnQuery = grnQuery.Where(g => g.ReceivedAt <= to);
        var grnData = await grnQuery
            .Include(g => g.Lines)
            .Select(g => new {
                g.UUID, g.SupplierId, g.SupplierName, g.PoUuid, g.ReceivedAt, g.Status,
                Lines = g.Lines.Select(l => new { l.QtyReceived, l.QtyAccepted, l.QtyRejected })
            })
            .ToListAsync();

        var allSupplierIds = pos.Select(p => p.SupplierId)
            .Union(grnData.Select(g => g.SupplierId))
            .Distinct();

        return allSupplierIds.Select(supplierId => {
            var supplierPos  = pos.Where(p => p.SupplierId == supplierId).ToList();
            var supplierGrns = grnData.Where(g => g.SupplierId == supplierId).ToList();
            var name = supplierPos.FirstOrDefault()?.SupplierName
                    ?? supplierGrns.FirstOrDefault()?.SupplierName
                    ?? "Unknown";

            var allLines = supplierGrns.SelectMany(g => g.Lines).ToList();
            var totalRecv = allLines.Sum(l => l.QtyReceived);
            var totalAcc  = allLines.Sum(l => l.QtyAccepted);
            var qualityScore = totalRecv > 0 ? Math.Round((double)(totalAcc / totalRecv) * 100, 1) : 100.0;

            int onTime = supplierGrns.Count(g => {
                var po = pos.FirstOrDefault(p => p.UUID == g.PoUuid);
                return po?.DeliveryDate.HasValue == true && g.ReceivedAt <= po.DeliveryDate.Value;
            });
            var onTimeRate = supplierGrns.Count > 0 ? Math.Round((double)onTime / supplierGrns.Count * 100, 1) : 0.0;

            return new SupplierPerformanceItem {
                SupplierId         = supplierId.ToString(),
                SupplierName       = name,
                PoCount            = supplierPos.Count,
                TotalSpend         = supplierPos.Sum(p => p.TotalAmount),
                OnTimeDeliveryRate = onTimeRate,
                QualityScore       = qualityScore,
                GrnCount           = supplierGrns.Count,
                RejectedGrnCount   = supplierGrns.Count(g => g.Status == "REJECTED")
            };
        })
        .OrderByDescending(x => x.TotalSpend)
        .ToList();
    }

    // ── PO Summary ────────────────────────────────────────────────────────────

    public async Task<List<PoSummaryItem>> GetPoSummaryAsync(ReportDateFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);
        var pos = await _demand.PurchaseOrders
            .Where(po => !po.IsDelete &&
                         (!from.HasValue || po.CreatedDate >= from) &&
                         (!to.HasValue   || po.CreatedDate <= to))
            .Select(po => new { po.Status, po.TotalAmount })
            .ToListAsync();

        return pos.GroupBy(p => p.Status)
            .Select(g => new PoSummaryItem { Status = g.Key, Count = g.Count(), TotalValue = g.Sum(x => x.TotalAmount) })
            .OrderBy(x => x.Status)
            .ToList();
    }

    // ── Spend by Supplier ─────────────────────────────────────────────────────

    public async Task<List<SpendBySupplierItem>> GetSpendBySupplierAsync(ReportDateFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);
        var pos = await _demand.PurchaseOrders
            .Where(po => !po.IsDelete &&
                         po.Status != "DRAFT" && po.Status != "CANCELLED" &&
                         (!from.HasValue || po.CreatedDate >= from) &&
                         (!to.HasValue   || po.CreatedDate <= to))
            .Select(po => new { po.SupplierId, po.SupplierName, po.TotalAmount, po.CreatedDate })
            .ToListAsync();

        return pos.GroupBy(p => p.SupplierId)
            .Select(g => new SpendBySupplierItem {
                SupplierId   = g.Key.ToString(),
                SupplierName = g.First().SupplierName,
                PoCount      = g.Count(),
                TotalSpend   = g.Sum(p => p.TotalAmount),
                LatestPoDate = g.Max(p => p.CreatedDate)
            })
            .OrderByDescending(x => x.TotalSpend)
            .ToList();
    }

    // ── Pending Approvals ─────────────────────────────────────────────────────

    public async Task<List<PendingApprovalItem>> GetPendingApprovalsAsync()
    {
        var result = new List<PendingApprovalItem>();

        var prs = await _demand.PurchaseRequisitions
            .Where(pr => !pr.IsDelete && pr.Status == "SUBMITTED")
            .Select(pr => new { pr.UUID, pr.PrNumber, pr.PrTitle, pr.Status, pr.CreatedDate })
            .ToListAsync();
        result.AddRange(prs.Select(pr => new PendingApprovalItem {
            Module       = "Demand",
            EntityType   = "Purchase Requisition",
            EntityNumber = pr.PrNumber,
            EntityUuid   = pr.UUID,
            Status       = pr.Status,
            CreatedDate  = pr.CreatedDate,
            Description  = pr.PrTitle
        }));

        var pos = await _demand.PurchaseOrders
            .Where(po => !po.IsDelete && po.Status == "DRAFT")
            .Select(po => new { po.UUID, po.PoNumber, po.SupplierName, po.Status, po.CreatedDate })
            .ToListAsync();
        result.AddRange(pos.Select(po => new PendingApprovalItem {
            Module       = "Demand",
            EntityType   = "Purchase Order",
            EntityNumber = po.PoNumber,
            EntityUuid   = po.UUID,
            Status       = po.Status,
            CreatedDate  = po.CreatedDate,
            Description  = po.SupplierName
        }));

        var pendingGrnStatuses = new[] { "PENDING_QC", "PENDING_FINANCE", "PENDING_APPROVAL" };
        var grnsPending = await _warehouse.Grns
            .Where(g => !g.IsDelete && pendingGrnStatuses.Contains(g.Status))
            .Select(g => new { g.UUID, g.GrnNumber, g.SupplierName, g.Status, g.CreatedDate })
            .ToListAsync();
        result.AddRange(grnsPending.Select(g => new PendingApprovalItem {
            Module       = "Warehouse",
            EntityType   = "Goods Receipt",
            EntityNumber = g.GrnNumber,
            EntityUuid   = g.UUID,
            Status       = g.Status,
            CreatedDate  = g.CreatedDate,
            Description  = g.SupplierName
        }));

        var pendingInvoices = await _finance.Invoices
            .Where(i => !i.IsDelete && i.ApprovedBy == null &&
                        (i.MatchStatus == "Matched" || i.MatchStatus == "Variance"))
            .Select(i => new { i.UUID, i.InvoiceNumber, i.SupplierName, i.MatchStatus, i.CreatedDate })
            .ToListAsync();
        result.AddRange(pendingInvoices.Select(i => new PendingApprovalItem {
            Module       = "Finance",
            EntityType   = "Invoice",
            EntityNumber = i.InvoiceNumber,
            EntityUuid   = i.UUID,
            Status       = $"Awaiting Approval ({i.MatchStatus})",
            CreatedDate  = i.CreatedDate,
            Description  = i.SupplierName
        }));

        return result.OrderBy(x => x.CreatedDate).ToList();
    }

    // ── Stock Levels ──────────────────────────────────────────────────────────

    public async Task<List<StockLevelItem>> GetStockLevelsAsync(ReportDateFilter filter)
    {
        var items = await _inventory.InventoryItems
            .Include(i => i.Product)
            .Include(i => i.Warehouse)
            .Where(i => i.QtyOnHand > 0)
            .ToListAsync();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            items = items.Where(i =>
                (i.Product?.Name.ToLower().Contains(s) == true) ||
                (i.Product?.Sku?.ToLower().Contains(s) == true)).ToList();
        }

        return items.Select(i => {
            var avail = i.QtyOnHand - i.QtyReserved;
            return new StockLevelItem {
                ProductName   = i.Product?.Name ?? "–",
                Sku           = i.Product?.Sku,
                WarehouseName = i.Warehouse?.Name,
                QtyOnHand     = i.QtyOnHand,
                QtyReserved   = i.QtyReserved,
                QtyAvailable  = avail < 0 ? 0 : avail,
                UnitCost      = i.UnitCost ?? 0m,
                StockValue    = i.QtyOnHand * (i.UnitCost ?? 0m)
            };
        })
        .OrderBy(x => x.WarehouseName).ThenBy(x => x.ProductName)
        .ToList();
    }

    // ── Stock Level Summary (SFM-008) ────────────────────────────────────────

    public async Task<StockLevelSummaryReport> GetStockLevelSummaryAsync(StockLevelSummaryFilter filter)
    {
        var query = _inventory.InventoryItems
            .Include(i => i.Product).ThenInclude(p => p.Category)
            .Include(i => i.Product).ThenInclude(p => p.SubCategory)
            .Include(i => i.Warehouse)
            .AsQueryable();

        if (filter.WarehouseId.HasValue)   query = query.Where(i => i.WarehouseId == filter.WarehouseId.Value);
        if (filter.CategoryId.HasValue)    query = query.Where(i => i.Product.CategoryId == filter.CategoryId.Value);
        if (filter.SubCategoryId.HasValue) query = query.Where(i => i.Product.SubCategoryId == filter.SubCategoryId.Value);

        var items = await query.ToListAsync();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            items = items.Where(i =>
                (i.Product?.Name.ToLower().Contains(s) == true) ||
                (i.Product?.Sku?.ToLower().Contains(s) == true)).ToList();
        }

        var rows = items.Select(i =>
        {
            var available    = i.QtyOnHand - i.QtyReserved;
            if (available < 0) available = 0;
            var unitCost     = i.UnitCost ?? 0m;
            var reorderPoint = i.Product?.ReorderPoint;

            return new StockLevelSummaryItem
            {
                ProductCode       = i.Product?.Sku ?? string.Empty,
                ProductName       = i.Product?.Name ?? "–",
                Category          = i.Product?.Category?.Name,
                SubCategory       = i.Product?.SubCategory?.Name,
                Warehouse         = i.Warehouse?.Name,
                QtyOnHand         = i.QtyOnHand,
                QtyReserved       = i.QtyReserved,
                QtyAvailable      = available,
                ReorderPoint      = reorderPoint,
                UnitCost          = unitCost,
                TotalValue        = i.QtyOnHand * unitCost,
                BelowReorderLevel = reorderPoint.HasValue && available <= reorderPoint.Value
            };
        })
        .OrderBy(x => x.ProductName)
        .ToList();

        return new StockLevelSummaryReport
        {
            Items           = rows,
            GrandTotalValue = rows.Sum(x => x.TotalValue)
        };
    }

    // ── PR Fulfillment Status (SFM-009) ──────────────────────────────────────

    public async Task<List<PrFulfillmentItem>> GetPrFulfillmentStatusAsync(PrFulfillmentFilter filter)
    {
        var query = _demand.PurchaseRequisitions
            .AsNoTracking()
            .Include(r => r.Lines)
            .Where(r => !r.IsDelete && (r.ApprovedAt != null || r.Status == "CANCELLED"));

        if (filter.DateFrom.HasValue)                    query = query.Where(r => r.ApprovedAt >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)                       query = query.Where(r => r.ApprovedAt <= filter.DateTo.Value);
        if (!string.IsNullOrWhiteSpace(filter.Department)) query = query.Where(r => r.Department == filter.Department);

        var prs = await query.ToListAsync();

        var fulfillment    = await ComputeFulfillmentForPrsAsync(prs);
        var requesterNames = await ResolveUserNamesAsync(prs.Select(p => p.RequesterId));

        var items = prs.Select(pr =>
        {
            var (status, total, fulfilled) = fulfillment[pr.UUID];
            return new PrFulfillmentItem
            {
                PrUuid                = pr.UUID,
                PrNumber              = pr.PrNumber,
                Department            = pr.Department,
                Requester             = requesterNames.GetValueOrDefault(pr.RequesterId, $"User #{pr.RequesterId}"),
                ApprovalDate          = pr.ApprovedAt,
                TotalLines            = total,
                FulfilledLines        = fulfilled,
                PendingLines          = total - fulfilled,
                FulfillmentPercentage = total > 0 ? Math.Round(fulfilled * 100.0 / total, 2) : 0,
                FulfillmentStatus     = status
            };
        }).ToList();

        if (!string.IsNullOrWhiteSpace(filter.FulfillmentStatus))
            items = items.Where(i => i.FulfillmentStatus == filter.FulfillmentStatus).ToList();

        return items.OrderByDescending(i => i.ApprovalDate).ToList();
    }

    // Bulk-computes fulfillment for a batch of PRs in a handful of queries (no N+1): PR lines are
    // joined to PO lines via PurchaseOrderLine.SourcePrLineUuid, then those PO lines to GrnLines
    // via GrnLine.PoLineUuid, aggregating QtyAccepted against the PO line's ordered Quantity. A PR
    // line counts as fulfilled only once its accepted quantity meets its ordered quantity.
    private async Task<Dictionary<Guid, (string Status, int Total, int Fulfilled)>> ComputeFulfillmentForPrsAsync(List<PurchaseRequisition> prs)
    {
        var result = new Dictionary<Guid, (string, int, int)>();

        var prLineUuids = prs.SelectMany(p => p.Lines.Select(l => l.UUID)).ToList();

        var poLines = prLineUuids.Count > 0
            ? await _demand.PurchaseOrderLines
                .Where(l => l.SourcePrLineUuid.HasValue && prLineUuids.Contains(l.SourcePrLineUuid.Value))
                .Select(l => new { l.UUID, SourcePrLineUuid = l.SourcePrLineUuid!.Value, l.Quantity })
                .ToListAsync()
            : [];

        var poLineUuids = poLines.Select(l => l.UUID).ToList();

        var acceptedByPoLine = poLineUuids.Count > 0
            ? (await _warehouse.GrnLines
                .Where(g => poLineUuids.Contains(g.PoLineUuid))
                .Select(g => new { g.PoLineUuid, g.QtyAccepted })
                .ToListAsync())
                .GroupBy(g => g.PoLineUuid)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.QtyAccepted))
            : new Dictionary<Guid, decimal>();

        var poLinesByPrLine = poLines
            .GroupBy(l => l.SourcePrLineUuid)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var pr in prs)
        {
            if (pr.Status == "CANCELLED")
            {
                result[pr.UUID] = ("CANCELLED", pr.Lines.Count, 0);
                continue;
            }

            var total       = pr.Lines.Count;
            var fulfilled   = 0;
            var anyLineHasPo = false;

            foreach (var line in pr.Lines)
            {
                if (!poLinesByPrLine.TryGetValue(line.UUID, out var matched) || matched.Count == 0)
                    continue;

                anyLineHasPo = true;
                var ordered  = matched.Sum(l => l.Quantity);
                var accepted = matched.Sum(l => acceptedByPoLine.GetValueOrDefault(l.UUID));
                if (ordered > 0 && accepted >= ordered)
                    fulfilled++;
            }

            string status =
                total == 0 || !anyLineHasPo ? "UNFULFILLED" :
                fulfilled == total          ? "FULLY_FULFILLED" :
                                               "PARTIALLY_FULFILLED";

            result[pr.UUID] = (status, total, fulfilled);
        }

        return result;
    }

    private async Task<Dictionary<int, string>> ResolveUserNamesAsync(IEnumerable<int> userIds)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, string>();
        return (await _userQuery.GetUsersAsync(ids)).ToDictionary(u => u.UserId, u => u.DisplayName);
    }

    // ── PR-to-PO Pipeline (SFM-009) ───────────────────────────────────────────

    // Furthest-stage ladder, index = rank (higher is further downstream).
    private static readonly string[] PipelineStages =
    [
        "Awaiting Quotation", "Quotation Sent", "Quotation Awarded",
        "PO Created", "PO Sent", "GRN Received", "Invoice Paid", "Material Disbursed"
    ];

    private static readonly Dictionary<string, int> PipelineEventRank = new()
    {
        ["QUOTATION_SENT"]    = 1,
        ["QUOTATION_AWARDED"] = 2,
        ["PO_CREATED"]        = 3,
        ["PO_SENT"]           = 4,
        ["GRN_CREATED"]       = 5,
        ["GRN_APPROVED"]      = 5,
        ["INVOICE_PAID"]      = 6,
        ["MIR_ISSUED"]        = 7
    };

    public async Task<PrPipelineReport> GetPrToPoPipelineAsync(PrPipelineFilter filter)
    {
        var query = _demand.PurchaseRequisitions
            .AsNoTracking()
            .Include(r => r.Lines)
            .Where(r => !r.IsDelete && (r.ApprovedAt != null || r.Status == "CANCELLED"));

        if (filter.DateFrom.HasValue)                    query = query.Where(r => r.ApprovedAt >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)                       query = query.Where(r => r.ApprovedAt <= filter.DateTo.Value);
        if (!string.IsNullOrWhiteSpace(filter.Department)) query = query.Where(r => r.Department == filter.Department);

        var prs         = await query.ToListAsync();
        var fulfillment = await ComputeFulfillmentForPrsAsync(prs);

        var items = new List<PrPipelineItem>();
        foreach (var pr in prs)
        {
            var (furthestStage, resolvedViaTraceId) = await ResolveFurthestStageAsync(pr);
            var (status, _, _) = fulfillment[pr.UUID];

            items.Add(new PrPipelineItem
            {
                PrUuid             = pr.UUID,
                PrNumber           = pr.PrNumber,
                Department         = pr.Department,
                FurthestStage      = furthestStage,
                ResolvedViaTraceId = resolvedViaTraceId,
                FulfillmentStatus  = status
            });
        }

        return new PrPipelineReport
        {
            TotalPrs               = items.Count,
            FullyFulfilledCount     = items.Count(i => i.FulfillmentStatus == "FULLY_FULFILLED"),
            PartiallyFulfilledCount = items.Count(i => i.FulfillmentStatus == "PARTIALLY_FULFILLED"),
            UnfulfilledCount        = items.Count(i => i.FulfillmentStatus == "UNFULFILLED"),
            CancelledCount          = items.Count(i => i.FulfillmentStatus == "CANCELLED"),
            Items                   = items.OrderByDescending(i => i.PrNumber).ToList()
        };
    }

    private async Task<(string Stage, bool ViaTraceId)> ResolveFurthestStageAsync(PurchaseRequisition pr)
    {
        var timeline = await _timeline.GetTimelineDetailAsync(pr.TraceId);
        if (timeline is not null && timeline.Events.Count > 0)
        {
            var rank = 0;
            foreach (var evt in timeline.Events)
                if (PipelineEventRank.TryGetValue(evt.EventType, out var r) && r > rank)
                    rank = r;

            return (PipelineStages[rank], true);
        }

        // Fallback: no timeline exists for this PR's trace_id (e.g. it predates event wiring) —
        // derive the furthest stage from live entity state via FK traversal instead.
        var stage = await ResolveFurthestStageViaFkFallbackAsync(pr);
        return (stage, false);
    }

    private async Task<string> ResolveFurthestStageViaFkFallbackAsync(PurchaseRequisition pr)
    {
        var prLineUuids = pr.Lines.Select(l => l.UUID).ToList();
        var rank = 0;

        var quotation = await _demand.Quotations
            .Where(q => q.SourceType == "PR" && q.SourceId == pr.UUID && !q.IsDelete)
            .OrderByDescending(q => q.Id)
            .Select(q => q.Status)
            .FirstOrDefaultAsync();

        if (quotation == "AWARDED")      rank = Math.Max(rank, 2);
        else if (quotation == "SENT")    rank = Math.Max(rank, 1);

        var poLines = prLineUuids.Count > 0
            ? await _demand.PurchaseOrderLines
                .Where(l => l.SourcePrLineUuid.HasValue && prLineUuids.Contains(l.SourcePrLineUuid.Value))
                .Select(l => new { LineUuid = l.UUID, PoUuid = l.PurchaseOrder.UUID, PoStatus = l.PurchaseOrder.Status })
                .ToListAsync()
            : [];

        if (poLines.Count > 0)
        {
            rank = Math.Max(rank, 3); // PO Created

            if (poLines.Any(l => l.PoStatus is "SENT" or "PARTIALLY_RECEIVED" or "RECEIVED" or "PARTIALLY_INVOICED" or "CLOSED"))
                rank = Math.Max(rank, 4); // PO Sent

            var poLineUuids = poLines.Select(l => l.LineUuid).ToList();
            var hasGrn = await _warehouse.GrnLines.AnyAsync(g => poLineUuids.Contains(g.PoLineUuid) && g.QtyAccepted > 0);
            if (hasGrn) rank = Math.Max(rank, 5); // GRN Received

            var poUuids = poLines.Select(l => l.PoUuid).Distinct().ToList();
            var hasPaidInvoice = await _finance.Invoices
                .AnyAsync(i => poUuids.Contains(i.PoUuid) && !i.IsDelete && (i.PaymentStatus == "Paid" || i.PaymentStatus == "FULLY_PAID"));
            if (hasPaidInvoice) rank = Math.Max(rank, 6); // Invoice Paid
        }

        var prLineIds = pr.Lines.Select(l => l.Id).ToList();
        var mirLineIds = prLineIds.Count > 0
            ? await _material.MaterialIssueRequestDetails
                .Where(l => l.PrLineId.HasValue && prLineIds.Contains(l.PrLineId.Value))
                .Select(l => l.Id)
                .ToListAsync()
            : [];

        if (mirLineIds.Count > 0)
        {
            var hasDisbursed = await (
                from mivLine in _material.MaterialIssueVoucherLines
                where mirLineIds.Contains(mivLine.MirLineId)
                join miv in _material.MaterialIssueVouchers on mivLine.MivId equals miv.Id
                where miv.Status == "POSTED"
                select miv.Id
            ).AnyAsync();

            if (hasDisbursed) rank = Math.Max(rank, 7); // Material Disbursed
        }

        return PipelineStages[rank];
    }

    // ── Reorder Alerts ────────────────────────────────────────────────────────

    public async Task<List<ReorderAlertItem>> GetReorderAlertsAsync()
    {
        var items = await _inventory.InventoryItems
            .Include(i => i.Product)
            .Include(i => i.Warehouse)
            .ToListAsync();

        return items
            .Where(i => i.Product != null && i.QtyOnHand <= i.Product.ReorderPoint)
            .Select(i => new ReorderAlertItem {
                ProductName   = i.Product!.Name,
                Sku           = i.Product.Sku,
                WarehouseName = i.Warehouse?.Name,
                QtyOnHand     = i.QtyOnHand,
                ReorderPoint  = i.Product.ReorderPoint ?? 0m,
                ReorderQty    = i.Product.ReorderQty   ?? 0m,
                Shortfall     = (i.Product.ReorderPoint ?? 0m) - i.QtyOnHand
            })
            .OrderByDescending(x => x.Shortfall)
            .ToList();
    }

    // ── Inventory Valuation ───────────────────────────────────────────────────

    public async Task<List<InventoryValuationItem>> GetInventoryValuationAsync()
    {
        var items = await _inventory.InventoryItems
            .Include(i => i.Warehouse)
            .Where(i => i.QtyOnHand > 0)
            .ToListAsync();

        return items
            .GroupBy(i => i.Warehouse?.Name ?? "Unassigned")
            .Select(g => new InventoryValuationItem {
                WarehouseName = g.Key,
                ProductCount  = g.Select(i => i.ProductId).Distinct().Count(),
                TotalQty      = g.Sum(i => i.QtyOnHand),
                TotalValue    = g.Sum(i => i.QtyOnHand * (i.UnitCost ?? 0m))
            })
            .OrderByDescending(x => x.TotalValue)
            .ToList();
    }

    // ── GRN Variance ─────────────────────────────────────────────────────────

    public async Task<List<GrnVarianceItem>> GetGrnVarianceAsync(ReportDateFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);
        var grns = await _warehouse.Grns
            .Where(g => !g.IsDelete &&
                        (!from.HasValue || g.ReceivedAt >= from) &&
                        (!to.HasValue   || g.ReceivedAt <= to))
            .Include(g => g.Lines)
            .ToListAsync();

        return grns.Select(g => {
            var ordered  = g.Lines.Sum(l => l.QtyOrdered);
            var received = g.Lines.Sum(l => l.QtyReceived);
            var accepted = g.Lines.Sum(l => l.QtyAccepted);
            var rejected = g.Lines.Sum(l => l.QtyRejected);
            return new GrnVarianceItem {
                GrnNumber     = g.GrnNumber,
                PoNumber      = g.PoNumber,
                SupplierName  = g.SupplierName,
                ReceivedAt    = g.ReceivedAt,
                TotalOrdered  = ordered,
                TotalReceived = received,
                TotalAccepted = accepted,
                TotalRejected = rejected,
                VarianceQty   = ordered - accepted,
                Status        = g.Status
            };
        })
        .Where(x => x.VarianceQty != 0 || x.TotalRejected > 0)
        .OrderByDescending(x => x.ReceivedAt)
        .ToList();
    }

    // ── Shipment Tracker ──────────────────────────────────────────────────────

    public async Task<List<ShipmentTrackerItem>> GetShipmentTrackerAsync(string? status)
    {
        var query = _logistics.Shipments.Where(s => !s.IsDelete);
        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);
        else
            query = query.Where(s => s.Status != "Delivered" && s.Status != "Returned");

        var shipments = await query
            .OrderByDescending(s => s.DispatchDate)
            .ToListAsync();

        var now = DateTime.UtcNow;
        return shipments.Select(s => new ShipmentTrackerItem {
            ShipmentNumber   = s.ShipmentNumber,
            PoNumber         = s.PoNumber,
            CarrierName      = s.CarrierName,
            ShipmentType     = s.ShipmentType,
            Status           = s.Status,
            DispatchDate     = s.DispatchDate,
            EstimatedArrival = s.EstimatedArrival,
            ActualArrival    = s.ActualArrival,
            IsOverdue        = s.ActualArrival == null && s.EstimatedArrival < now,
            TrackingNumber   = s.TrackingNumber,
            TrackingUrl      = s.TrackingUrl
        }).ToList();
    }

    // ── Invoice Aging ─────────────────────────────────────────────────────────

    public async Task<(List<InvoiceAgingItem> Items, List<InvoiceAgingBucketSummary> Buckets)> GetInvoiceAgingAsync()
    {
        // "Paid" is the legacy single-invoice Payment flow's fully-paid value; "FULLY_PAID" is
        // the newer SupplierPayment posting flow's (SFM-004) — both must be excluded here.
        var invoices = await _finance.Invoices
            .Where(i => !i.IsDelete && i.PaymentStatus != "Paid" && i.PaymentStatus != "FULLY_PAID")
            .ToListAsync();

        var today = DateTime.UtcNow.Date;
        var items = invoices.Select(i => {
            var days   = (int)(today - i.DueDate.Date).TotalDays;
            var bucket = days <= 0 ? "Current" : days <= 30 ? "1–30 days" : days <= 60 ? "31–60 days" : days <= 90 ? "61–90 days" : "90+ days";
            return new InvoiceAgingItem {
                InvoiceNumber     = i.InvoiceNumber,
                SupplierInvoiceNo = i.SupplierInvoiceNo,
                SupplierName      = i.SupplierName,
                DueDate           = i.DueDate,
                TotalAmount       = i.TotalAmount,
                PaymentStatus     = i.PaymentStatus,
                DaysOverdue       = days > 0 ? days : 0,
                AgingBucket       = bucket
            };
        }).OrderByDescending(x => x.DaysOverdue).ToList();

        var buckets = items
            .GroupBy(x => x.AgingBucket)
            .Select(g => new InvoiceAgingBucketSummary {
                Bucket      = g.Key,
                Count       = g.Count(),
                TotalAmount = g.Sum(x => x.TotalAmount)
            })
            .ToList();

        return (items, buckets);
    }

    // ── Payment Summary ───────────────────────────────────────────────────────

    public async Task<PaymentSummaryModel> GetPaymentSummaryAsync(ReportDateFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);
        var payments = await _finance.Payments
            .Where(p => !p.IsDelete &&
                        (!from.HasValue || p.PaymentDate >= from) &&
                        (!to.HasValue   || p.PaymentDate <= to))
            .ToListAsync();

        var byMethod = payments
            .GroupBy(p => p.PaymentMethod)
            .Select(g => new PaymentByMethodItem {
                Method      = g.Key,
                Count       = g.Count(),
                TotalAmount = g.Sum(p => p.AmountPaid)
            }).ToList();

        return new PaymentSummaryModel {
            TotalProcessed = payments.Where(p => p.Status == "Processed" || p.Status == "Cleared").Sum(p => p.AmountPaid),
            ProcessedCount = payments.Count(p => p.Status == "Processed" || p.Status == "Cleared"),
            TotalPending   = payments.Where(p => p.Status == "Pending").Sum(p => p.AmountPaid),
            PendingCount   = payments.Count(p => p.Status == "Pending"),
            TotalReversed  = payments.Where(p => p.Status == "Reversed").Sum(p => p.AmountPaid),
            ReversedCount  = payments.Count(p => p.Status == "Reversed"),
            ByMethod       = byMethod
        };
    }

    // ── Supplier Ledger Summary (SC-001) ────────────────────────────────────────

    public async Task<SupplierLedgerSummaryReport> GetSupplierLedgerSummaryAsync(SupplierLedgerSummaryFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var suppliersQuery = _suppliers.Suppliers.AsNoTracking().Where(s => !s.IsDelete).AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.SupplierStatus))
            suppliersQuery = suppliersQuery.Where(s => s.Status == filter.SupplierStatus);
        if (filter.SupplierCategory.HasValue)
            suppliersQuery = suppliersQuery.Where(s => s.TypeMappings.Any(m => m.LookupValueId == filter.SupplierCategory.Value));

        var eligibleSuppliers = await suppliersQuery
            .Select(s => new { s.UUID, s.SupplierName, s.SupplierCode })
            .ToDictionaryAsync(s => s.UUID);

        var entriesQuery = _finance.SupplierLedgerEntries.AsNoTracking().AsQueryable();
        if (from.HasValue) entriesQuery = entriesQuery.Where(e => e.EntryDate >= from.Value);
        if (to.HasValue)   entriesQuery = entriesQuery.Where(e => e.EntryDate <  to.Value);

        var entries = await entriesQuery
            .Select(e => new { e.SupplierId, e.DebitAmount, e.CreditAmount, e.EntryDate })
            .ToListAsync();

        var items = entries
            .Where(e => eligibleSuppliers.ContainsKey(e.SupplierId))
            .GroupBy(e => e.SupplierId)
            .Select(g =>
            {
                var supplier    = eligibleSuppliers[g.Key];
                var totalDebits = g.Sum(e => e.DebitAmount);
                var totalCredits = g.Sum(e => e.CreditAmount);
                var netBalance   = totalDebits - totalCredits;

                return new SupplierLedgerSummaryItem
                {
                    SupplierId          = g.Key,
                    SupplierName        = supplier.SupplierName,
                    SupplierCode        = supplier.SupplierCode,
                    TotalInvoiced       = totalDebits,
                    TotalPaid           = totalCredits,
                    OutstandingBalance  = netBalance > 0 ? netBalance : 0m,
                    AdvanceBalance      = netBalance < 0 ? -netBalance : 0m,
                    LastTransactionDate = g.Max(e => e.EntryDate),
                    DrillDownUrl        = BuildLedgerDrillDownUrl(g.Key, filter.DateFrom, filter.DateTo)
                };
            })
            .Where(i => !filter.MinOutstanding.HasValue || i.OutstandingBalance >= filter.MinOutstanding.Value)
            .ToList();

        items = (filter.SortBy switch
        {
            "supplier_name"         => items.OrderBy(i => i.SupplierName),
            "last_transaction_date" => items.OrderByDescending(i => i.LastTransactionDate),
            _                       => items.OrderByDescending(i => i.OutstandingBalance)
        }).ToList();

        return new SupplierLedgerSummaryReport
        {
            Items                 = items,
            GrandTotalInvoiced    = items.Sum(i => i.TotalInvoiced),
            GrandTotalPaid        = items.Sum(i => i.TotalPaid),
            GrandTotalOutstanding = items.Sum(i => i.OutstandingBalance)
        };
    }

    private static string BuildLedgerDrillDownUrl(Guid supplierId, string? dateFrom, string? dateTo)
    {
        var url = $"/api/suppliers/{supplierId}/ledger";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(dateFrom)) query.Add($"dateFrom={Uri.EscapeDataString(dateFrom)}");
        if (!string.IsNullOrWhiteSpace(dateTo))   query.Add($"dateTo={Uri.EscapeDataString(dateTo)}");
        return query.Count > 0 ? $"{url}?{string.Join("&", query)}" : url;
    }

    // ── Supplier Orders Report (SC-002, FSD Addendum 23) ────────────────────────

    public async Task<SupplierOrdersReport> GetSupplierOrdersAsync(SupplierOrdersFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var poQuery = _demand.PurchaseOrders.AsNoTracking().Where(po => !po.IsDelete).AsQueryable();
        if (from.HasValue) poQuery = poQuery.Where(po => po.CreatedDate >= from.Value);
        if (to.HasValue)   poQuery = poQuery.Where(po => po.CreatedDate <  to.Value);
        if (filter.SupplierId.HasValue) poQuery = poQuery.Where(po => po.SupplierId == filter.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(filter.PoStatus)) poQuery = poQuery.Where(po => po.Status == filter.PoStatus);

        var pos = await poQuery.Include(po => po.Lines).ToListAsync();
        if (pos.Count == 0) return new SupplierOrdersReport();

        var poUuids = pos.Select(p => p.UUID).ToList();
        var grnByPo = (await _warehouse.Grns.AsNoTracking()
                .Where(g => !g.IsDelete && poUuids.Contains(g.PoUuid))
                .Select(g => new { g.PoUuid, g.ReceivedAt })
                .ToListAsync())
            .GroupBy(g => g.PoUuid)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), FirstReceivedAt: g.Min(x => x.ReceivedAt)));

        var rows = pos.Select(po =>
        {
            var hasGrn = grnByPo.TryGetValue(po.UUID, out var grn);

            int?    variance = null;
            string? color    = null;
            if (po.DeliveryDate.HasValue && hasGrn)
            {
                variance = (int)(grn.FirstReceivedAt.Date - po.DeliveryDate.Value.Date).TotalDays;
                color = variance <= 0 ? "green" : variance <= 7 ? "amber" : "red";
            }

            var detail = new SupplierOrderDetailItem
            {
                PoUuid               = po.UUID,
                PoNumber             = po.PoNumber,
                PoDate               = po.CreatedDate,
                TotalAmount          = po.TotalAmount,
                Status               = po.Status,
                QtyOrdered           = po.Lines.Sum(l => l.Quantity),
                QtyReceived          = po.Lines.Sum(l => l.QtyReceived),
                GrnCount             = hasGrn ? grn.Count : 0,
                ExpectedDeliveryDate = po.DeliveryDate,
                FirstGrnReceivedAt   = hasGrn ? grn.FirstReceivedAt : null,
                DeliveryVarianceDays = variance,
                DeliveryColor        = color
            };

            return (po.SupplierId, po.SupplierName, Detail: detail);
        }).ToList();

        rows = filter.DeliveryPerformance?.ToLowerInvariant() switch
        {
            "late"    => rows.Where(r => r.Detail.DeliveryVarianceDays > 0).ToList(),
            "on_time" => rows.Where(r => r.Detail.DeliveryVarianceDays is <= 0).ToList(),
            _         => rows
        };

        var suppliers = rows
            .GroupBy(r => r.SupplierId)
            .Select(g =>
            {
                // Lifecycle: DRAFT/APPROVED/SENT (pending) -> PARTIALLY_RECEIVED -> RECEIVED -> PARTIALLY_INVOICED -> CLOSED,
                // with CANCELLED reachable from the pending states. Anything past RECEIVED implies receipt already completed.
                var posForSupplier    = g.Select(x => x.Detail).OrderByDescending(d => d.PoDate).ToList();
                var fullyReceived     = posForSupplier.Count(d => d.Status is "RECEIVED" or "PARTIALLY_INVOICED" or "CLOSED");
                var partiallyReceived = posForSupplier.Count(d => d.Status == "PARTIALLY_RECEIVED");
                var cancelled         = posForSupplier.Count(d => d.Status == "CANCELLED");
                var pending           = posForSupplier.Count - fullyReceived - partiallyReceived - cancelled;
                var totalValue        = posForSupplier.Sum(d => d.TotalAmount);

                return new SupplierOrdersSummaryItem
                {
                    SupplierId              = g.Key,
                    SupplierName            = g.First().SupplierName,
                    TotalPoCount            = posForSupplier.Count,
                    TotalPoValue            = totalValue,
                    AvgPoValue              = posForSupplier.Count > 0 ? Math.Round(totalValue / posForSupplier.Count, 2) : 0m,
                    FullyReceivedCount      = fullyReceived,
                    PartiallyReceivedCount  = partiallyReceived,
                    PendingCount            = pending,
                    CancelledCount          = cancelled,
                    PurchaseOrders          = posForSupplier
                };
            })
            .OrderByDescending(s => s.TotalPoValue)
            .ToList();

        return new SupplierOrdersReport
        {
            Suppliers         = suppliers,
            GrandTotalPoCount = suppliers.Sum(s => s.TotalPoCount),
            GrandTotalPoValue = suppliers.Sum(s => s.TotalPoValue)
        };
    }

    // ── Supplier Comparison (SC-008) ────────────────────────────────────────────

    public async Task<SupplierComparisonResponse> GetSupplierComparisonAsync(List<Guid> supplierIds)
    {
        if (supplierIds.Count < 2 || supplierIds.Count > 5)
            throw new BadRequestException("Supplier comparison requires between 2 and 5 supplier IDs.");

        var suppliers = await _suppliers.Suppliers.AsNoTracking()
            .Where(s => !s.IsDelete && supplierIds.Contains(s.UUID))
            .Select(s => new { s.UUID, s.SupplierName })
            .ToListAsync();

        var columns = new List<SupplierComparisonColumn>();

        foreach (var supplierId in supplierIds)
        {
            var supplier = suppliers.FirstOrDefault(s => s.UUID == supplierId);
            if (supplier is null) continue; // unknown/deleted id — silently excluded from the comparison

            var latestSnapshot = await _suppliers.SupplierScoreSnapshots.AsNoTracking()
                .Where(s => s.SupplierId == supplierId)
                .OrderByDescending(s => s.PeriodEnd)
                .Select(s => new { s.Grade, s.TotalScore })
                .FirstOrDefaultAsync();

            var pos = await _demand.PurchaseOrders.AsNoTracking()
                .Where(po => !po.IsDelete && po.SupplierId == supplierId)
                .Select(po => new { po.UUID, po.TotalAmount, po.DeliveryDate })
                .ToListAsync();

            var poUuids = pos.Select(p => p.UUID).ToList();
            var grnFirstReceipt = await _warehouse.Grns.AsNoTracking()
                .Where(g => !g.IsDelete && poUuids.Contains(g.PoUuid))
                .GroupBy(g => g.PoUuid)
                .Select(g => new { PoUuid = g.Key, FirstReceivedAt = g.Min(x => x.ReceivedAt) })
                .ToListAsync();

            var variances = new List<int>();
            foreach (var po in pos)
            {
                if (!po.DeliveryDate.HasValue) continue;
                var grn = grnFirstReceipt.FirstOrDefault(g => g.PoUuid == po.UUID);
                if (grn is null) continue;
                variances.Add((grn.FirstReceivedAt.Date - po.DeliveryDate.Value.Date).Days);
            }

            var grnLineResults = await _warehouse.GrnLines.AsNoTracking()
                .Where(l => !l.Grn.IsDelete && l.Grn.SupplierId == supplierId && l.InspectionResult != null)
                .Select(l => l.InspectionResult)
                .ToListAsync();

            var ledgerEntries = await _finance.SupplierLedgerEntries.AsNoTracking()
                .Where(e => e.SupplierId == supplierId)
                .ToListAsync();
            var totalInvoiced = ledgerEntries.Sum(e => e.DebitAmount);
            var totalPaid     = ledgerEntries.Sum(e => e.CreditAmount);

            columns.Add(new SupplierComparisonColumn
            {
                SupplierId              = supplierId,
                SupplierName            = supplier.SupplierName,
                Grade                   = latestSnapshot?.Grade,
                CompositeScore          = latestSnapshot?.TotalScore,
                PoCount                 = pos.Count,
                TotalPoValue            = pos.Sum(p => p.TotalAmount),
                AvgDeliveryVarianceDays = variances.Count > 0 ? Math.Round((decimal)variances.Average(), 1) : null,
                RejectionRatePercent    = grnLineResults.Count > 0
                    ? Math.Round((decimal)grnLineResults.Count(r => r != "Pass") / grnLineResults.Count * 100, 1)
                    : 0m,
                TotalInvoiced      = totalInvoiced,
                TotalPaid          = totalPaid,
                OutstandingBalance = Math.Max(0m, totalInvoiced - totalPaid)
            });
        }

        return new SupplierComparisonResponse { Suppliers = columns };
    }

    // ── GRN Quality Analysis (SC-008) ───────────────────────────────────────────

    public async Task<GrnQualityAnalysisResponse> GetGrnQualityAnalysisAsync(GrnQualityAnalysisFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _warehouse.GrnLines.AsNoTracking().Where(l => !l.Grn.IsDelete && l.InspectionResult != null);
        if (from.HasValue) query = query.Where(l => l.Grn.ReceivedAt >= from.Value);
        if (to.HasValue)   query = query.Where(l => l.Grn.ReceivedAt < to.Value);
        if (filter.SupplierId.HasValue) query = query.Where(l => l.Grn.SupplierId == filter.SupplierId.Value);

        var lines = await query
            .Select(l => new { l.Grn.SupplierId, l.Grn.SupplierName, l.Grn.ReceivedAt, l.InspectionResult })
            .ToListAsync();

        var suppliers = lines
            .GroupBy(l => l.SupplierId)
            .Select(g =>
            {
                var lineList = g.ToList();
                var monthly = lineList
                    .GroupBy(l => new DateTime(l.ReceivedAt.Year, l.ReceivedAt.Month, 1))
                    .OrderBy(mg => mg.Key)
                    .Select(mg =>
                    {
                        var total = mg.Count();
                        return new GrnQualityMonthlyPoint
                        {
                            Month       = mg.Key.ToString("yyyy-MM"),
                            PassRate    = Math.Round((decimal)mg.Count(l => l.InspectionResult == "Pass") / total * 100, 1),
                            FailRate    = Math.Round((decimal)mg.Count(l => l.InspectionResult == "Fail") / total * 100, 1),
                            PartialRate = Math.Round((decimal)mg.Count(l => l.InspectionResult == "PartialPass") / total * 100, 1),
                            TotalLines  = total
                        };
                    })
                    .ToList();

                return new SupplierGrnQualityItem
                {
                    SupplierId      = g.Key,
                    SupplierName    = lineList[0].SupplierName,
                    OverallPassRate = Math.Round((decimal)lineList.Count(l => l.InspectionResult == "Pass") / lineList.Count * 100, 1),
                    TotalLines      = lineList.Count,
                    MonthlyTrend    = monthly
                };
            })
            .OrderByDescending(s => s.TotalLines)
            .ToList();

        var failedQuery = _warehouse.GrnLines.AsNoTracking()
            .Where(l => !l.Grn.IsDelete && (l.InspectionResult == "Fail" || l.InspectionResult == "PartialPass"));
        if (from.HasValue) failedQuery = failedQuery.Where(l => l.Grn.ReceivedAt >= from.Value);
        if (to.HasValue)   failedQuery = failedQuery.Where(l => l.Grn.ReceivedAt < to.Value);
        if (filter.SupplierId.HasValue) failedQuery = failedQuery.Where(l => l.Grn.SupplierId == filter.SupplierId.Value);

        var failedLines = await failedQuery
            .OrderByDescending(l => l.InspectedAt)
            .Take(200)
            .Select(l => new FailedGrnLineItem
            {
                GrnId            = l.Grn.UUID,
                GrnNumber        = l.Grn.GrnNumber,
                SupplierId       = l.Grn.SupplierId,
                SupplierName     = l.Grn.SupplierName,
                ItemDescription  = l.ItemDescription,
                InspectionResult = l.InspectionResult,
                RejectionReason  = l.RejectionReason,
                InspectedAt      = l.InspectedAt
            })
            .ToListAsync();

        return new GrnQualityAnalysisResponse { Suppliers = suppliers, FailedLines = failedLines };
    }

    // ── Supplier Spend Analysis (SC-008) ────────────────────────────────────────

    public async Task<SupplierSpendAnalysisResponse> GetSupplierSpendAnalysisAsync(SupplierSpendFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _finance.InvoiceLines.AsNoTracking().Where(il => !il.Invoice.IsDelete);
        if (from.HasValue) query = query.Where(il => il.Invoice.InvoiceDate >= from.Value);
        if (to.HasValue)   query = query.Where(il => il.Invoice.InvoiceDate < to.Value);

        var lines = await query
            .Select(il => new { il.Invoice.SupplierId, il.Invoice.SupplierName, il.LineTotal, il.PoLineUuid })
            .ToListAsync();

        var supplierTotals = lines
            .GroupBy(l => l.SupplierId)
            .Select(g => new SupplierSpendItem
            {
                SupplierId    = g.Key,
                SupplierName  = g.First().SupplierName,
                TotalInvoiced = g.Sum(l => l.LineTotal)
            })
            .OrderByDescending(s => s.TotalInvoiced)
            .ToList();

        var grandTotal = supplierTotals.Sum(s => s.TotalInvoiced);
        var top5Sum     = supplierTotals.Take(5).Sum(s => s.TotalInvoiced);

        // Category breakdown: InvoiceLine -> PurchaseOrderLine -> Product -> Category/SubCategory
        var poLineUuids = lines.Select(l => l.PoLineUuid).Distinct().ToList();
        var poLineProducts = await _demand.PurchaseOrderLines.AsNoTracking()
            .Where(pl => poLineUuids.Contains(pl.UUID))
            .Select(pl => new { pl.UUID, pl.ProductUuid })
            .ToDictionaryAsync(pl => pl.UUID, pl => pl.ProductUuid);

        var productUuids = poLineProducts.Values.Where(p => p.HasValue).Select(p => p!.Value).Distinct().ToList();
        var products = await _inventory.Products.AsNoTracking()
            .Where(p => productUuids.Contains(p.Uuid))
            .Select(p => new { p.Uuid, p.CategoryId, p.SubCategoryId })
            .ToDictionaryAsync(p => p.Uuid);

        var categoryIds    = products.Values.Where(p => p.CategoryId.HasValue).Select(p => p.CategoryId!.Value).Distinct().ToList();
        var subCategoryIds = products.Values.Where(p => p.SubCategoryId.HasValue).Select(p => p.SubCategoryId!.Value).Distinct().ToList();

        var categoryNames = await _inventory.ProductCategories.AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
        var subCategoryNames = await _inventory.ProductSubCategories.AsNoTracking()
            .Where(c => subCategoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);

        var spendByCategory = lines
            .Select(l =>
            {
                string  category    = "Uncategorised";
                string? subCategory = null;
                if (poLineProducts.TryGetValue(l.PoLineUuid, out var productUuid) && productUuid.HasValue
                    && products.TryGetValue(productUuid.Value, out var product))
                {
                    if (product.CategoryId.HasValue && categoryNames.TryGetValue(product.CategoryId.Value, out var catName))
                        category = catName;
                    if (product.SubCategoryId.HasValue && subCategoryNames.TryGetValue(product.SubCategoryId.Value, out var subName))
                        subCategory = subName;
                }
                return new { category, subCategory, l.LineTotal };
            })
            .GroupBy(x => new { x.category, x.subCategory })
            .Select(g => new CategorySpendItem
            {
                Category      = g.Key.category,
                SubCategory   = g.Key.subCategory,
                TotalInvoiced = g.Sum(x => x.LineTotal)
            })
            .OrderByDescending(c => c.TotalInvoiced)
            .ToList();

        return new SupplierSpendAnalysisResponse
        {
            GrandTotalSpend          = grandTotal,
            TopSuppliers             = supplierTotals.Take(10).ToList(),
            Top5ConcentrationPercent = grandTotal > 0 ? Math.Round(top5Sum / grandTotal * 100, 1) : 0m,
            SpendByCategory          = spendByCategory
        };
    }

    // ── Delivery Performance Heatmap (SC-008) ───────────────────────────────────

    public async Task<DeliveryHeatmapResponse?> GetDeliveryPerformanceHeatmapAsync(Guid supplierId, int year)
    {
        var supplier = await _suppliers.Suppliers.AsNoTracking()
            .Where(s => s.UUID == supplierId && !s.IsDelete)
            .Select(s => new { s.SupplierName })
            .FirstOrDefaultAsync();
        if (supplier is null) return null;

        var yearStart = new DateTime(year, 1, 1);
        var yearEnd   = yearStart.AddYears(1);

        var grns = await _warehouse.Grns.AsNoTracking()
            .Where(g => !g.IsDelete && g.SupplierId == supplierId && g.ReceivedAt >= yearStart && g.ReceivedAt < yearEnd)
            .Select(g => new { g.ReceivedAt, g.PoUuid })
            .ToListAsync();

        var poUuids = grns.Select(g => g.PoUuid).Distinct().ToList();
        var poDeliveryDates = await _demand.PurchaseOrders.AsNoTracking()
            .Where(po => poUuids.Contains(po.UUID))
            .Select(po => new { po.UUID, po.DeliveryDate })
            .ToDictionaryAsync(po => po.UUID, po => po.DeliveryDate);

        // Worst (highest) variance wins when multiple GRNs land on the same day.
        var varianceByDay = new Dictionary<DateTime, int?>();
        foreach (var grn in grns)
        {
            var day = grn.ReceivedAt.Date;
            int? variance = null;
            if (poDeliveryDates.TryGetValue(grn.PoUuid, out var deliveryDate) && deliveryDate.HasValue)
                variance = (grn.ReceivedAt.Date - deliveryDate.Value.Date).Days;

            if (!varianceByDay.TryGetValue(day, out var existing) || (variance ?? int.MinValue) > (existing ?? int.MinValue))
                varianceByDay[day] = variance;
        }

        var days = new List<DeliveryHeatmapDayItem>();
        for (var date = yearStart; date < yearEnd; date = date.AddDays(1))
        {
            var hasGrn = varianceByDay.TryGetValue(date, out var variance);
            days.Add(new DeliveryHeatmapDayItem
            {
                Date         = date,
                HasGrn       = hasGrn,
                VarianceDays = hasGrn ? variance : null,
                Color        = HeatmapColorFor(hasGrn, variance)
            });
        }

        return new DeliveryHeatmapResponse { SupplierId = supplierId, SupplierName = supplier.SupplierName, Year = year, Days = days };
    }

    private static string HeatmapColorFor(bool hasGrn, int? variance)
    {
        if (!hasGrn || !variance.HasValue) return "grey";
        if (variance <= 0) return "green";
        if (variance <= 7) return "amber";
        return "red";
    }

    // ── Budget Utilization ────────────────────────────────────────────────────

    public async Task<List<BudgetUtilizationItem>> GetBudgetUtilizationAsync(ReportDateFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var prs = await _demand.PurchaseRequisitions
            .Where(pr => !pr.IsDelete && pr.Status != "DRAFT" &&
                         (!from.HasValue || pr.CreatedDate >= from) &&
                         (!to.HasValue   || pr.CreatedDate <= to))
            .Select(pr => new { pr.BudgetCode, pr.Department, pr.EstimatedTotal })
            .ToListAsync();

        var pos = await _demand.PurchaseOrders
            .Where(po => !po.IsDelete && po.Status != "DRAFT" && po.Status != "CANCELLED" &&
                         (!from.HasValue || po.CreatedDate >= from) &&
                         (!to.HasValue   || po.CreatedDate <= to))
            .Select(po => new { po.BudgetCode, po.TotalAmount })
            .ToListAsync();

        var allBudgetCodes = prs.Select(p => p.BudgetCode ?? "–")
            .Union(pos.Select(p => p.BudgetCode ?? "–"))
            .Distinct();

        return allBudgetCodes.Select(code => {
            var prGroup   = prs.Where(p => (p.BudgetCode ?? "–") == code).ToList();
            var poGroup   = pos.Where(p => (p.BudgetCode ?? "–") == code).ToList();
            var estimated = prGroup.Sum(p => p.EstimatedTotal);
            var actual    = poGroup.Sum(p => p.TotalAmount);
            var variance  = actual - estimated;
            return new BudgetUtilizationItem {
                BudgetCode      = code,
                Department      = prGroup.FirstOrDefault()?.Department,
                PoCount         = poGroup.Count,
                EstimatedAmount = estimated,
                ActualSpend     = actual,
                VarianceAmount  = variance,
                VariancePercent = estimated > 0 ? Math.Round((double)(variance / estimated) * 100, 1) : 0
            };
        })
        .OrderByDescending(x => x.ActualSpend)
        .ToList();
    }

    // ── Audit Trail ───────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<AuditLogItemModel>> GetAuditTrailAsync(AuditLogFilter filter)
    {
        var query = _db.AuditLogs.AsQueryable();
        if (!string.IsNullOrEmpty(filter.Module))     query = query.Where(l => l.Module     == filter.Module);
        if (!string.IsNullOrEmpty(filter.Action))     query = query.Where(l => l.Action     == filter.Action);
        if (!string.IsNullOrEmpty(filter.EntityType)) query = query.Where(l => l.EntityType == filter.EntityType);
        if (filter.EntityId.HasValue)                 query = query.Where(l => l.EntityId   == filter.EntityId);
        if (filter.UserId.HasValue)                   query = query.Where(l => l.UserId     == filter.UserId);
        if (DateTime.TryParse(filter.DateFrom, out var df)) query = query.Where(l => l.Timestamp >= df);
        if (DateTime.TryParse(filter.DateTo,   out var dt)) query = query.Where(l => l.Timestamp <= dt.AddDays(1));

        var total = await query.CountAsync();
        var data  = await query.OrderByDescending(l => l.Timestamp)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        // Resolve display names for rows that have a UserId but no stored UserName
        var missingIds = data
            .Where(l => l.UserId.HasValue && string.IsNullOrEmpty(l.UserName))
            .Select(l => l.UserId!.Value)
            .Distinct()
            .ToList();

        var nameMap = missingIds.Count > 0
            ? (await _userQuery.GetUsersAsync(missingIds))
                .ToDictionary(u => u.UserId, u => u.DisplayName)
            : new Dictionary<int, string>();

        return new PaginatedResponse<AuditLogItemModel> {
            Data         = data.Select(l => new AuditLogItemModel {
                UUID         = l.UUID,
                Timestamp    = l.Timestamp,
                UserId       = l.UserId,
                UserName     = !string.IsNullOrEmpty(l.UserName)
                                   ? l.UserName
                                   : (l.UserId.HasValue && nameMap.TryGetValue(l.UserId.Value, out var n) ? n : null),
                Module       = l.Module,
                Action       = l.Action,
                EntityType   = l.EntityType,
                EntityId     = l.EntityId,
                FieldChanged = l.FieldChanged,
                OldValue     = l.OldValue,
                NewValue     = l.NewValue,
                IpAddress    = l.IpAddress,
                Notes        = l.Notes
            }).ToList(),
            TotalRecords = total,
            Page         = filter.Page,
            PageSize     = filter.PageSize,
            TotalPages   = (int)Math.Ceiling((double)total / filter.PageSize)
        };
    }

    // ── User Activity ─────────────────────────────────────────────────────────

    public async Task<List<UserActivityItem>> GetUserActivityAsync(ReportDateFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);
        var query = _db.AuditLogs.AsQueryable();
        if (from.HasValue) query = query.Where(l => l.Timestamp >= from);
        if (to.HasValue)   query = query.Where(l => l.Timestamp <= to);

        var logs = await query.Where(l => l.UserId.HasValue)
            .Select(l => new { l.UserId, l.UserName, l.Action, l.Timestamp })
            .ToListAsync();

        var grouped = logs.GroupBy(l => l.UserId!.Value).ToList();

        // Resolve display names for any user whose name is missing from the audit log
        var userIds = grouped.Select(g => g.Key).Distinct().ToList();
        var nameMap = userIds.Count > 0
            ? (await _userQuery.GetUsersAsync(userIds)).ToDictionary(u => u.UserId, u => u.DisplayName)
            : new Dictionary<int, string>();

        return grouped
            .Select(g => new UserActivityItem {
                UserId       = g.Key,
                UserName     = !string.IsNullOrEmpty(g.First().UserName)
                                   ? g.First().UserName
                                   : (nameMap.TryGetValue(g.Key, out var n) ? n : null),
                TotalActions = g.Count(),
                CreateCount  = g.Count(l => l.Action == "CREATE"),
                UpdateCount  = g.Count(l => l.Action == "UPDATE"),
                DeleteCount  = g.Count(l => l.Action == "DELETE"),
                ApproveCount = g.Count(l => l.Action == "APPROVE"),
                LastActionAt = g.Max(l => l.Timestamp)
            })
            .OrderByDescending(x => x.TotalActions)
            .ToList();
    }

    // ── Audit Log Write ───────────────────────────────────────────────────────

    public async Task AddAuditLogAsync(AuditLog log)
    {
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync();
    }

    // ── Material Issue Register ───────────────────────────────────────────────

    public async Task<List<MaterialIssueRegisterItem>> GetMaterialIssueRegisterAsync(MaterialIssueRegisterFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _material.MaterialIssueVouchers
            .Include(v => v.MaterialIssueRequest)
            .Include(v => v.Lines)
            .AsQueryable();

        if (from.HasValue) query = query.Where(v => v.IssueDate >= from.Value);
        if (to.HasValue)   query = query.Where(v => v.IssueDate <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(v => v.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.RequestType))
            query = query.Where(v => v.MaterialIssueRequest.RequestType == filter.RequestType);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            query = query.Where(v => v.IssueNo.ToLower().Contains(s) ||
                                     v.MaterialIssueRequest.RequestNo.ToLower().Contains(s));
        }

        var data = await query
            .OrderByDescending(v => v.IssueDate)
            .ToListAsync();

        return data.Select(v => new MaterialIssueRegisterItem
        {
            MivUuid     = v.UUID,
            IssueNo     = v.IssueNo,
            MirNo       = v.MaterialIssueRequest.RequestNo,
            RequestType = v.MaterialIssueRequest.RequestType,
            IssuedTo    = v.IssuedTo,
            IssueDate   = v.IssueDate,
            Status      = v.Status,
            TotalValue  = v.TotalValue,
            LineCount   = v.Lines.Count,
            Notes       = v.Notes,
            CreatedDate = v.CreatedDate
        }).ToList();
    }

    // ── Material Consumption Report ───────────────────────────────────────────

    public async Task<List<MaterialConsumptionReportItem>> GetMaterialConsumptionReportAsync(MaterialConsumptionReportFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        // Base: all POSTED MIV lines — these represent what was issued
        var mivQuery = _material.MaterialIssueVoucherLines
            .Include(l => l.MaterialIssueVoucher)
                .ThenInclude(v => v.MaterialIssueRequest)
            .Where(l => l.MaterialIssueVoucher.Status == "POSTED")
            .AsQueryable();

        if (from.HasValue) mivQuery = mivQuery.Where(l => l.MaterialIssueVoucher.IssueDate >= from.Value);
        if (to.HasValue)   mivQuery = mivQuery.Where(l => l.MaterialIssueVoucher.IssueDate <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.MirUuid) && Guid.TryParse(filter.MirUuid, out var mirGuid))
            mivQuery = mivQuery.Where(l => l.MaterialIssueVoucher.MaterialIssueRequest.UUID == mirGuid);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            mivQuery = mivQuery.Where(l => l.ItemDescription.ToLower().Contains(s));
        }

        var mivLines = await mivQuery.ToListAsync();

        // Load all consumptions for these MIV line IDs
        var mivLineIds = mivLines.Select(l => l.Id).ToList();
        var consumptionQuery = _material.MaterialConsumptions
            .Where(c => mivLineIds.Contains(c.MivLineId));
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
            consumptionQuery = consumptionQuery.Where(c => c.SourceType == filter.SourceType);
        var consumptions = await consumptionQuery.ToListAsync();
        var consumedByLineId = consumptions.GroupBy(c => c.MivLineId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.ConsumedQty));

        return mivLines.Select(l =>
        {
            var consumed = consumedByLineId.GetValueOrDefault(l.Id, 0m);
            var balance  = l.IssuedQty - consumed;
            return new MaterialConsumptionReportItem
            {
                ProductName   = l.ItemDescription,
                ProductUuid   = l.ProductUuid,
                UnitOfMeasure = l.UnitOfMeasure,
                MirNo         = l.MaterialIssueVoucher.MaterialIssueRequest.RequestNo,
                MirUuid       = l.MaterialIssueVoucher.MaterialIssueRequest.UUID,
                IssuedQty     = l.IssuedQty,
                ConsumedQty   = consumed,
                BalanceQty    = balance,
                UnitCost      = l.UnitCost,
                BalanceValue  = balance * l.UnitCost,
                SourceType    = filter.SourceType
            };
        })
        .OrderBy(x => x.MirNo).ThenBy(x => x.ProductName)
        .ToList();
    }

    // ── Project Consumption Report ────────────────────────────────────────────

    public async Task<List<ProjectConsumptionItem>> GetProjectConsumptionAsync(ProjectConsumptionFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _material.ProjectCostLedger
            .Include(l => l.Project)
            .AsQueryable();

        if (from.HasValue) query = query.Where(l => l.PostedDate >= from.Value);
        if (to.HasValue)   query = query.Where(l => l.PostedDate <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.TransactionType))
            query = query.Where(l => l.TransactionType == filter.TransactionType);
        if (!string.IsNullOrWhiteSpace(filter.ProjectUuid) && Guid.TryParse(filter.ProjectUuid, out var projGuid))
            query = query.Where(l => l.Project.UUID == projGuid);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            query = query.Where(l => l.Project.ProjectCode.ToLower().Contains(s) ||
                                     l.Project.ProjectName.ToLower().Contains(s) ||
                                     l.ItemDescription.ToLower().Contains(s));
        }

        var data = await query.OrderByDescending(l => l.PostedDate).ToListAsync();
        return data.Select(l => new ProjectConsumptionItem
        {
            ProjectUuid     = l.Project.UUID,
            ProjectCode     = l.Project.ProjectCode,
            ProjectName     = l.Project.ProjectName,
            ItemDescription = l.ItemDescription,
            ProductUuid     = l.ProductUuid,
            TransactionType = l.TransactionType,
            ReferenceNumber = l.ReferenceNumber,
            Quantity        = l.Quantity,
            UnitCost        = l.UnitCost,
            Amount          = l.Amount,
            PostedDate      = l.PostedDate
        }).ToList();
    }

    // ── Department Consumption Report ─────────────────────────────────────────

    public async Task<List<DepartmentConsumptionItem>> GetDepartmentConsumptionAsync(DepartmentConsumptionFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _material.DepartmentCostLedger.AsQueryable();
        if (from.HasValue) query = query.Where(l => l.PostedDate >= from.Value);
        if (to.HasValue)   query = query.Where(l => l.PostedDate <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.Department))
            query = query.Where(l => l.Department == filter.Department);
        if (!string.IsNullOrWhiteSpace(filter.TransactionType))
            query = query.Where(l => l.TransactionType == filter.TransactionType);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            query = query.Where(l => l.Department.ToLower().Contains(s) ||
                                     l.ItemDescription.ToLower().Contains(s));
        }

        var data = await query.OrderByDescending(l => l.PostedDate).ToListAsync();
        return data.Select(l => new DepartmentConsumptionItem
        {
            Department      = l.Department,
            CostCenter      = l.CostCenter,
            ItemDescription = l.ItemDescription,
            ProductUuid     = l.ProductUuid,
            TransactionType = l.TransactionType,
            ReferenceNumber = l.ReferenceNumber,
            Quantity        = l.Quantity,
            UnitCost        = l.UnitCost,
            Amount          = l.Amount,
            PostedDate      = l.PostedDate
        }).ToList();
    }

    // ── Stock Movement Report ─────────────────────────────────────────────────

    public async Task<List<StockMovementItem>> GetStockMovementAsync(StockMovementFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _inventory.InventoryLedgerEntries
            .Include(e => e.Product)
            .Include(e => e.Warehouse)
            .AsQueryable();

        if (from.HasValue) query = query.Where(e => e.TransactionDate >= from.Value);
        if (to.HasValue)   query = query.Where(e => e.TransactionDate <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.TransactionType))
            query = query.Where(e => e.TransactionType == filter.TransactionType);
        if (!string.IsNullOrWhiteSpace(filter.ProductUuid) && Guid.TryParse(filter.ProductUuid, out var prodGuid))
            query = query.Where(e => e.Product.Uuid == prodGuid);
        if (!string.IsNullOrWhiteSpace(filter.WarehouseId) && Guid.TryParse(filter.WarehouseId, out var whGuid))
            query = query.Where(e => e.Warehouse.Uuid == whGuid);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            query = query.Where(e => e.Product.Name.ToLower().Contains(s) ||
                                     (e.Product.Sku != null && e.Product.Sku.ToLower().Contains(s)));
        }

        var data = await query.OrderByDescending(e => e.TransactionDate).ToListAsync();
        return data.Select(e => new StockMovementItem
        {
            LedgerUuid      = e.LedgerId,
            TransactionType = e.TransactionType,
            ProductName     = e.Product?.Name ?? "–",
            ProductUuid     = e.Product?.Uuid ?? Guid.Empty,
            Sku             = e.Product?.Sku,
            WarehouseName   = e.Warehouse?.Name ?? "–",
            QuantityIn      = e.QuantityIn ?? 0m,
            QuantityOut     = e.QuantityOut ?? 0m,
            UnitCost        = e.UnitCost,
            TotalValue      = e.TransactionValue,
            ReferenceNumber = e.ReferenceNumber,
            BatchNumber     = null,
            TransactionDate = e.TransactionDate
        }).ToList();
    }

    // ── Stock Ledger Report ───────────────────────────────────────────────────

    public async Task<List<StockLedgerItem>> GetStockLedgerAsync(StockLedgerFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _inventory.InventoryLedgerEntries
            .Include(e => e.Product)
            .Include(e => e.Warehouse)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.ProductUuid) && Guid.TryParse(filter.ProductUuid, out var prodGuid))
            query = query.Where(e => e.Product.Uuid == prodGuid);
        if (!string.IsNullOrWhiteSpace(filter.WarehouseId) && Guid.TryParse(filter.WarehouseId, out var whGuid))
            query = query.Where(e => e.Warehouse.Uuid == whGuid);

        // Load all entries ordered chronologically so BalanceAfter is meaningful
        var allEntries = await query.OrderBy(e => e.TransactionDate).ToListAsync();

        var result = new List<StockLedgerItem>();
        foreach (var e in allEntries)
        {
            if (from.HasValue && e.TransactionDate < from.Value) continue;
            if (to.HasValue   && e.TransactionDate >= to.Value)  continue;
            result.Add(new StockLedgerItem
            {
                LedgerUuid      = e.LedgerId,
                TransactionDate = e.TransactionDate,
                TransactionType = e.TransactionType,
                ProductName     = e.Product?.Name ?? "–",
                ProductUuid     = e.Product?.Uuid ?? Guid.Empty,
                WarehouseName   = e.Warehouse?.Name ?? "–",
                QuantityIn      = e.QuantityIn ?? 0m,
                QuantityOut     = e.QuantityOut ?? 0m,
                RunningBalance  = e.BalanceAfter,
                UnitCost        = e.UnitCost,
                ReferenceNumber = e.ReferenceNumber
            });
        }
        return result;
    }

    // ── Material Return Report ────────────────────────────────────────────────

    public async Task<List<MaterialReturnReportItem>> GetMaterialReturnReportAsync(MaterialReturnReportFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _material.MaterialReturns
            .Include(r => r.Lines)
            .Include(r => r.MIV)
            .Include(r => r.MIR)
            .AsQueryable();

        if (from.HasValue) query = query.Where(r => r.ReturnDate >= from.Value);
        if (to.HasValue)   query = query.Where(r => r.ReturnDate <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(r => r.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            query = query.Where(r => r.ReturnNo.ToLower().Contains(s) ||
                                     r.MIV.IssueNo.ToLower().Contains(s));
        }

        var data = await query.OrderByDescending(r => r.ReturnDate).ToListAsync();
        var result = new List<MaterialReturnReportItem>();
        foreach (var ret in data)
        {
            foreach (var line in ret.Lines)
            {
                if (!string.IsNullOrWhiteSpace(filter.Condition) && line.Condition != filter.Condition)
                    continue;
                result.Add(new MaterialReturnReportItem
                {
                    ReturnUuid      = ret.UUID,
                    ReturnNo        = ret.ReturnNo,
                    MivNo           = ret.MIV.IssueNo,
                    MirNo           = ret.MIR.RequestNo,
                    Status          = ret.Status,
                    ReturnDate      = ret.ReturnDate,
                    ItemDescription = line.ItemDescription,
                    ProductUuid     = line.ProductUuid,
                    UnitOfMeasure   = line.UnitOfMeasure,
                    ReturnedQty     = line.ReturnedQty,
                    Condition       = line.Condition,
                    Reason          = line.Reason,
                    UnitCost        = line.UnitCost,
                    LineValue       = line.LineValue
                });
            }
        }
        return result;
    }

    // ── Wastage Report ────────────────────────────────────────────────────────

    public async Task<List<WastageReportItem>> GetWastageReportAsync(WastageReportFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _material.Wastages.AsQueryable();
        if (from.HasValue) query = query.Where(w => w.CreatedDate >= from.Value);
        if (to.HasValue)   query = query.Where(w => w.CreatedDate <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(w => w.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
            query = query.Where(w => w.SourceType == filter.SourceType);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.ToLower();
            query = query.Where(w => w.WastageNo.ToLower().Contains(s) ||
                                     w.ItemDescription.ToLower().Contains(s) ||
                                     w.Reason.ToLower().Contains(s));
        }

        var data = await query.OrderByDescending(w => w.CreatedDate).ToListAsync();
        return data.Select(w => new WastageReportItem
        {
            WastageUuid     = w.UUID,
            WastageNo       = w.WastageNo,
            SourceType      = w.SourceType,
            ItemDescription = w.ItemDescription,
            ProductUuid     = w.ProductUuid,
            UnitOfMeasure   = w.UnitOfMeasure,
            WastedQty       = w.WastedQty,
            UnitCost        = w.UnitCost,
            Amount          = w.Amount,
            Reason          = w.Reason,
            Status          = w.Status,
            ApprovedBy      = w.ApprovedBy,
            ApprovedAt      = w.ApprovedAt,
            CreatedDate     = w.CreatedDate
        }).ToList();
    }

    // ── Reserved Stock Report ─────────────────────────────────────────────────

    public async Task<List<ReservedStockItem>> GetReservedStockAsync(ReservedStockFilter filter)
    {
        var (from, to) = ParseDates(filter.DateFrom, filter.DateTo);

        var query = _material.StockReservations
            .Include(r => r.MaterialIssueRequest)
            .AsQueryable();

        if (from.HasValue) query = query.Where(r => r.ReservedAt >= from.Value);
        if (to.HasValue)   query = query.Where(r => r.ReservedAt <  to.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(r => r.Status == filter.Status);
        else
            query = query.Where(r => r.Status == "ACTIVE" || r.Status == "FLAGGED");

        var reservations = await query.OrderByDescending(r => r.ReservedAt).ToListAsync();

        // Resolve product names and warehouse names from inventory in one batch
        var inventoryItemIds = reservations.Select(r => r.InventoryItemId).Distinct().ToList();
        var invItems = await _inventory.InventoryItems
            .Include(i => i.Product)
            .Include(i => i.Warehouse)
            .Where(i => inventoryItemIds.Contains(i.Id))
            .ToListAsync();
        var invMap = invItems.ToDictionary(i => i.Id);

        var today = DateTime.UtcNow.Date;
        return reservations.Select(r =>
        {
            invMap.TryGetValue(r.InventoryItemId, out var inv);
            return new ReservedStockItem
            {
                ReservationUuid = r.UUID,
                MirNo           = r.MaterialIssueRequest.RequestNo,
                MirUuid         = r.MaterialIssueRequest.UUID,
                RequestType     = r.MaterialIssueRequest.RequestType,
                ProductUuid     = r.ProductUuid,
                ProductName     = inv?.Product?.Name ?? "–",
                Sku             = inv?.Product?.Sku,
                WarehouseName   = inv?.Warehouse?.Name ?? "–",
                ReservedQty     = r.ReservedQty,
                Status          = r.Status,
                ReservedAt      = r.ReservedAt,
                AgeDays         = (int)(today - r.ReservedAt.Date).TotalDays,
                IsFlagged       = r.IsFlagged
            };
        }).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (DateTime? from, DateTime? to) ParseDates(string? from, string? to)
    {
        DateTime? f = DateTime.TryParse(from, out var fd) ? fd : null;
        DateTime? t = DateTime.TryParse(to,   out var td) ? td.AddDays(1) : null;
        return (f, t);
    }
}
