using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Reports.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Services;

/// <summary>See <see cref="IManufacturingReportService"/> and the model file's own note on scope.</summary>
internal sealed class ManufacturingReportService : IManufacturingReportService
{
    private readonly MaterialDbContext  _material;
    private readonly InventoryDbContext _inv;

    public ManufacturingReportService(MaterialDbContext material, InventoryDbContext inv)
    {
        _material = material;
        _inv      = inv;
    }

    // ── R4/R5/R16 — Production Efficiency & WIP Summary ──────────────────────

    public async Task<ProductionEfficiencyReport> GetProductionEfficiencyAsync(ManufacturingReportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = _material.ProductionOrders.AsNoTracking().AsQueryable();
        if (filter.DateFrom is { } from) query = query.Where(p => p.CreatedAt >= from);
        if (filter.DateTo   is { } to)   query = query.Where(p => p.CreatedAt <= to);

        var orders = await query
            .Select(p => new { p.Status, p.PlannedQuantity, p.ProducedQuantity, p.AcceptedQuantity, p.RejectedQuantity, p.CreatedAt, p.ActualEndDate, p.RequiredDate })
            .ToListAsync();

        var report = new ProductionEfficiencyReport
        {
            DateFrom = filter.DateFrom, DateTo = filter.DateTo,
            TotalOrders    = orders.Count,
            CountByStatus  = orders.GroupBy(o => o.Status).ToDictionary(g => g.Key, g => g.Count()),
            TotalPlannedQuantity  = orders.Sum(o => o.PlannedQuantity),
            TotalProducedQuantity = orders.Sum(o => o.ProducedQuantity),
            TotalAcceptedQuantity = orders.Sum(o => o.AcceptedQuantity),
            TotalRejectedQuantity = orders.Sum(o => o.RejectedQuantity)
        };
        if (report.TotalProducedQuantity > 0)
            report.OverallYieldPercent = Math.Round(report.TotalAcceptedQuantity / report.TotalProducedQuantity * 100m, 2);

        var completed = orders.Where(o => o.ActualEndDate is not null).ToList();
        if (completed.Count > 0)
        {
            report.OnTimeCompletionPercent = Math.Round(
                completed.Count(o => o.ActualEndDate!.Value.Date <= o.RequiredDate.Date) / (decimal)completed.Count * 100m, 2);
            report.AverageCycleDays = Math.Round(
                completed.Average(o => (decimal)(o.ActualEndDate!.Value - o.CreatedAt).TotalDays), 2);
        }
        return report;
    }

    // ── R6/R7 — Quality & Scrap Summary ───────────────────────────────────────

    public async Task<QualityScrapReport> GetQualityScrapAsync(ManufacturingReportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = _material.QualityInspections.AsNoTracking().AsQueryable();
        if (filter.DateFrom is { } from) query = query.Where(q => q.InspectedAt >= from);
        if (filter.DateTo   is { } to)   query = query.Where(q => q.InspectedAt <= to);

        var qis = await query
            .Select(q => new { q.InspectedQuantity, q.AcceptedQuantity, q.RejectedQuantity, q.HoldQuantity, q.ReworkQuantity, ProductUuid = q.ProductionOrder.ProductUuid })
            .ToListAsync();

        var report = new QualityScrapReport
        {
            DateFrom = filter.DateFrom, DateTo = filter.DateTo,
            TotalInspections  = qis.Count,
            TotalInspectedQty = qis.Sum(q => q.InspectedQuantity),
            TotalAcceptedQty  = qis.Sum(q => q.AcceptedQuantity),
            TotalRejectedQty  = qis.Sum(q => q.RejectedQuantity),
            TotalHoldQty      = qis.Sum(q => q.HoldQuantity),
            TotalReworkQty    = qis.Sum(q => q.ReworkQuantity)
        };
        if (report.TotalInspectedQty > 0)
        {
            report.AcceptanceRatePercent = Math.Round(report.TotalAcceptedQty / report.TotalInspectedQty * 100m, 2);
            report.RejectionRatePercent  = Math.Round(report.TotalRejectedQty / report.TotalInspectedQty * 100m, 2);
        }

        var productUuids = qis.Select(q => q.ProductUuid).Distinct().ToList();
        var names = productUuids.Count == 0
            ? new Dictionary<Guid, string>()
            : await _inv.Products.AsNoTracking().Where(p => productUuids.Contains(p.Uuid)).ToDictionaryAsync(p => p.Uuid, p => p.Name);

        report.ByProduct = qis.GroupBy(q => q.ProductUuid).Select(g =>
        {
            var inspected = g.Sum(x => x.InspectedQuantity);
            var rejected  = g.Sum(x => x.RejectedQuantity);
            return new QualityScrapByProductItem
            {
                ProductUuid  = g.Key,
                ProductName  = names.GetValueOrDefault(g.Key, g.Key.ToString()),
                InspectedQty = inspected,
                RejectedQty  = rejected,
                RejectionRatePercent = inspected > 0 ? Math.Round(rejected / inspected * 100m, 2) : null
            };
        }).OrderByDescending(x => x.RejectedQty).ToList();

        return report;
    }

    // ── R14/R15 — Manufacturing document registers ────────────────────────────

    public async Task<PaginatedResponse<ProductionMaterialIssueRegisterItem>> GetMaterialIssueRegisterAsync(ManufacturingRegisterFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = _material.ProductionMaterialIssues.AsNoTracking().AsQueryable();
        if (filter.DateFrom is { } from) query = query.Where(i => i.CreatedAt >= from);
        if (filter.DateTo   is { } to)   query = query.Where(i => i.CreatedAt <= to);
        if (filter.WarehouseUuid is { } wh) query = query.Where(i => i.WarehouseUuid == wh);
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(i => i.Status == filter.Status.ToUpperInvariant());

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 50 : filter.PageSize, 1, 500);

        var rows = await query.OrderByDescending(i => i.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new
            {
                i.UUID, i.IssueNumber, ProductionOrderUuid = i.ProductionOrder.UUID, ProductionNumber = i.ProductionOrder.ProductionNumber,
                ProductUuid = i.ProductionOrder.ProductUuid, i.WarehouseUuid, i.IssueType, i.Status, i.CreatedAt, i.ConfirmedAt,
                TotalQuantity = i.Lines.Sum(l => l.Quantity), LineCount = i.Lines.Count
            })
            .ToListAsync();

        var (productNames, warehouseNames) = await NamesAsync(rows.Select(r => r.ProductUuid), rows.Select(r => r.WarehouseUuid));

        var items = rows.Select(r => new ProductionMaterialIssueRegisterItem
        {
            IssueUuid = r.UUID, IssueNumber = r.IssueNumber, ProductionOrderUuid = r.ProductionOrderUuid, ProductionNumber = r.ProductionNumber,
            OutputProductName = productNames.GetValueOrDefault(r.ProductUuid, string.Empty),
            WarehouseUuid = r.WarehouseUuid, WarehouseName = warehouseNames.GetValueOrDefault(r.WarehouseUuid, string.Empty),
            IssueType = r.IssueType, Status = r.Status, TotalQuantity = r.TotalQuantity, LineCount = r.LineCount,
            CreatedAt = r.CreatedAt, ConfirmedAt = r.ConfirmedAt
        }).ToList();

        return new PaginatedResponse<ProductionMaterialIssueRegisterItem>
        {
            Data = items, TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<PaginatedResponse<FinishedGoodsReceiptRegisterItem>> GetFinishedGoodsReceiptRegisterAsync(ManufacturingRegisterFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = _material.FinishedGoodsReceipts.AsNoTracking().AsQueryable();
        if (filter.DateFrom is { } from) query = query.Where(f => f.ReceivedAt >= from);
        if (filter.DateTo   is { } to)   query = query.Where(f => f.ReceivedAt <= to);
        if (filter.WarehouseUuid is { } wh) query = query.Where(f => f.WarehouseUuid == wh);
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(f => f.Status == filter.Status.ToUpperInvariant());

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 50 : filter.PageSize, 1, 500);

        var rows = await query.OrderByDescending(f => f.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(f => new
            {
                f.UUID, f.FgrNumber, ProductionOrderUuid = f.ProductionOrder.UUID, ProductionNumber = f.ProductionOrder.ProductionNumber,
                ProductUuid = f.ProductionOrder.ProductUuid, f.WarehouseUuid, f.TotalQuantity, f.Status, f.ReceivedAt
            })
            .ToListAsync();

        var (productNames, warehouseNames) = await NamesAsync(rows.Select(r => r.ProductUuid), rows.Select(r => r.WarehouseUuid));

        var items = rows.Select(r => new FinishedGoodsReceiptRegisterItem
        {
            FgrUuid = r.UUID, FgrNumber = r.FgrNumber, ProductionOrderUuid = r.ProductionOrderUuid, ProductionNumber = r.ProductionNumber,
            ProductName = productNames.GetValueOrDefault(r.ProductUuid, string.Empty),
            WarehouseUuid = r.WarehouseUuid, WarehouseName = warehouseNames.GetValueOrDefault(r.WarehouseUuid, string.Empty),
            TotalQuantity = r.TotalQuantity, Status = r.Status, ReceivedAt = r.ReceivedAt
        }).ToList();

        return new PaginatedResponse<FinishedGoodsReceiptRegisterItem>
        {
            Data = items, TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    private async Task<(Dictionary<Guid, string> Products, Dictionary<Guid, string> Warehouses)> NamesAsync(IEnumerable<Guid> productUuids, IEnumerable<Guid> warehouseUuids)
    {
        var products   = productUuids.Distinct().ToList();
        var warehouses = warehouseUuids.Distinct().ToList();
        var productNames = products.Count == 0
            ? new Dictionary<Guid, string>()
            : await _inv.Products.AsNoTracking().Where(p => products.Contains(p.Uuid)).ToDictionaryAsync(p => p.Uuid, p => p.Name);
        var warehouseNames = warehouses.Count == 0
            ? new Dictionary<Guid, string>()
            : await _inv.Warehouses.AsNoTracking().Where(w => warehouses.Contains(w.Uuid)).ToDictionaryAsync(w => w.Uuid, w => w.Name);
        return (productNames, warehouseNames);
    }

    // ── R24 — Production Ledger Reconciliation ────────────────────────────────

    /// <summary>
    /// The application already enforces these invariants transactionally at write time (§19A.6, QI's
    /// own accept/reject bookkeeping), so a genuine mismatch here would mean a bug or manual data
    /// tampering rather than something the UI can ever produce — which is exactly why it is worth
    /// checking on a schedule rather than only trusting the write path forever.
    /// </summary>
    public async Task<LedgerReconciliationReport> GetLedgerReconciliationAsync(ManufacturingReportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = _material.ProductionOrders.AsNoTracking().Where(p => p.ProducedQuantity > 0).AsQueryable();
        if (filter.DateFrom is { } from) query = query.Where(p => p.CreatedAt >= from);
        if (filter.DateTo   is { } to)   query = query.Where(p => p.CreatedAt <= to);

        var orders = await query
            .Select(p => new { p.Id, p.UUID, p.ProductionNumber, p.Status, p.AcceptedQuantity, p.RejectedQuantity })
            .ToListAsync();
        var orderIds = orders.Select(o => o.Id).ToList();

        var fgrSums = orderIds.Count == 0 ? [] : await _material.FinishedGoodsReceipts.AsNoTracking()
            .Where(f => orderIds.Contains(f.ProductionOrderId) && f.Status == FgrStatus.Confirmed)
            .GroupBy(f => f.ProductionOrderId)
            .Select(g => new { ProductionOrderId = g.Key, Total = g.Sum(f => f.TotalQuantity) })
            .ToDictionaryAsync(g => g.ProductionOrderId, g => g.Total);

        var issuedOrderIds = orderIds.Count == 0 ? [] : (await _material.ProductionMaterialIssues.AsNoTracking()
            .Where(i => orderIds.Contains(i.ProductionOrderId) && i.Status == ProductionIssueStatus.Confirmed)
            .Select(i => i.ProductionOrderId).Distinct().ToListAsync()).ToHashSet();

        var qiOrderIds = orderIds.Count == 0 ? [] : (await _material.QualityInspections.AsNoTracking()
            .Where(q => orderIds.Contains(q.ProductionOrderId))
            .Select(q => q.ProductionOrderId).Distinct().ToListAsync()).ToHashSet();

        var flagged = new List<LedgerReconciliationItem>();
        foreach (var o in orders)
        {
            var sumFgr    = fgrSums.GetValueOrDefault(o.Id, 0m);
            var hasIssues = issuedOrderIds.Contains(o.Id);
            var hasQi     = qiOrderIds.Contains(o.Id);
            var reasons   = new List<string>();

            if (Math.Abs(o.AcceptedQuantity - sumFgr) > 0.0001m)
                reasons.Add($"AcceptedQuantity ({o.AcceptedQuantity:0.####}) does not match the sum of confirmed FGRs ({sumFgr:0.####}).");
            if (!hasIssues)
                reasons.Add("Reported output but has no confirmed material issue recorded.");
            if (o.RejectedQuantity > 0 && !hasQi)
                reasons.Add("Carries a rejected quantity but has no quality inspection recorded.");

            if (reasons.Count == 0) continue;
            flagged.Add(new LedgerReconciliationItem
            {
                ProductionOrderUuid = o.UUID, ProductionNumber = o.ProductionNumber, Status = o.Status,
                AcceptedQuantity = o.AcceptedQuantity, FinishedGoodsCredited = sumFgr, RejectedQuantity = o.RejectedQuantity,
                HasMaterialIssues = hasIssues, HasQualityInspection = hasQi, Reason = string.Join(" ", reasons)
            });
        }

        return new LedgerReconciliationReport
        {
            DateFrom = filter.DateFrom, DateTo = filter.DateTo,
            TotalOrdersChecked = orders.Count, FlaggedCount = flagged.Count, Flagged = flagged
        };
    }

    // ── R26/R27/R28 — Chained manufacturing dependency tree ───────────────────

    private sealed record NodeFacts(int Id, Guid Uuid, string ProductionNumber, Guid ProductUuid, decimal PlannedQuantity,
        decimal AcceptedQuantity, string Status, DateTime CreatedAt, DateTime? ActualEndDate, int? ParentProductionOrderId);

    /// <summary>
    /// Walks up to the chain's true root, then breadth-first back down by <c>ParentProductionOrderId</c>
    /// — the same bounded (depth ≤ 10) level-by-level walk <c>BomRepository.EnsureNoCycleAsync</c> uses
    /// for a recipe chain, so this never loads more of the table than the one chain actually in view.
    /// </summary>
    public async Task<ChainedManufacturingReport?> GetChainedManufacturingAsync(Guid productionOrderUuid)
    {
        var start = await _material.ProductionOrders.AsNoTracking()
            .Where(p => p.UUID == productionOrderUuid)
            .Select(p => new { p.Id, p.ParentProductionOrderId })
            .FirstOrDefaultAsync();
        if (start is null) return null;

        var topId = start.Id;
        var parentId = start.ParentProductionOrderId;
        for (var guard = 0; parentId is { } pid && guard < 20; guard++)
        {
            var parent = await _material.ProductionOrders.AsNoTracking()
                .Where(p => p.Id == pid).Select(p => new { p.Id, p.ParentProductionOrderId }).FirstOrDefaultAsync();
            if (parent is null) break;
            topId = parent.Id;
            parentId = parent.ParentProductionOrderId;
        }

        var nodes      = new Dictionary<int, NodeFacts>();
        var childrenOf = new Dictionary<int, List<int>>();
        var depthOf    = new Dictionary<int, int> { [topId] = 0 };
        var frontier   = new List<int> { topId };

        for (var guard = 0; frontier.Count > 0 && guard < 20; guard++)
        {
            var here = await _material.ProductionOrders.AsNoTracking()
                .Where(p => frontier.Contains(p.Id))
                .Select(p => new NodeFacts(p.Id, p.UUID, p.ProductionNumber, p.ProductUuid, p.PlannedQuantity, p.AcceptedQuantity, p.Status, p.CreatedAt, p.ActualEndDate, p.ParentProductionOrderId))
                .ToListAsync();
            foreach (var n in here) nodes[n.Id] = n;

            var children = await _material.ProductionOrders.AsNoTracking()
                .Where(p => p.ParentProductionOrderId != null && frontier.Contains(p.ParentProductionOrderId.Value))
                .Select(p => new NodeFacts(p.Id, p.UUID, p.ProductionNumber, p.ProductUuid, p.PlannedQuantity, p.AcceptedQuantity, p.Status, p.CreatedAt, p.ActualEndDate, p.ParentProductionOrderId))
                .ToListAsync();

            var next = new List<int>();
            foreach (var c in children)
            {
                nodes[c.Id] = c;
                var parent = c.ParentProductionOrderId!.Value;
                if (!childrenOf.TryGetValue(parent, out var list)) childrenOf[parent] = list = [];
                list.Add(c.Id);
                if (depthOf.ContainsKey(c.Id)) continue;
                depthOf[c.Id] = depthOf[parent] + 1;
                next.Add(c.Id);
            }
            frontier = next;
        }

        var productUuids = nodes.Values.Select(n => n.ProductUuid).Distinct().ToList();
        var names = productUuids.Count == 0
            ? new Dictionary<Guid, string>()
            : await _inv.Products.AsNoTracking().Where(p => productUuids.Contains(p.Uuid)).ToDictionaryAsync(p => p.Uuid, p => p.Name);

        ChainedManufacturingNode Build(int id)
        {
            var n = nodes[id];
            return new ChainedManufacturingNode
            {
                ProductionOrderUuid = n.Uuid, ProductionNumber = n.ProductionNumber, ProductName = names.GetValueOrDefault(n.ProductUuid, string.Empty),
                PlannedQuantity = n.PlannedQuantity, AcceptedQuantity = n.AcceptedQuantity, Status = n.Status,
                CreatedAt = n.CreatedAt, ActualEndDate = n.ActualEndDate, Depth = depthOf[id],
                CycleDays = n.ActualEndDate is { } end ? Math.Round((decimal)(end - n.CreatedAt).TotalDays, 2) : null,
                Children = childrenOf.TryGetValue(id, out var kids) ? kids.OrderBy(k => nodes[k].CreatedAt).Select(Build).ToList() : []
            };
        }

        var root = Build(topId);
        var flat = Flatten(root);
        decimal? totalCycle = flat.All(n => n.ActualEndDate is not null)
            ? Math.Round((decimal)(flat.Max(n => n.ActualEndDate!.Value) - root.CreatedAt).TotalDays, 2)
            : null;

        return new ChainedManufacturingReport
        {
            RootProductionOrderUuid = root.ProductionOrderUuid, RootProductionNumber = root.ProductionNumber,
            TotalOrdersInChain = flat.Count, MaxDepth = flat.Max(n => n.Depth), TotalCycleDays = totalCycle, Root = root
        };
    }

    private static List<ChainedManufacturingNode> Flatten(ChainedManufacturingNode node) =>
        new List<ChainedManufacturingNode> { node }.Concat(node.Children.SelectMany(Flatten)).ToList();
}
