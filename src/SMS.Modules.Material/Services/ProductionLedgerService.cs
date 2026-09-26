using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Services;

internal sealed class ProductionLedgerService : IProductionLedgerService
{
    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;

    public ProductionLedgerService(MaterialDbContext db, InventoryDbContext inv)
    {
        _db  = db;
        _inv = inv;
    }

    public async Task<ProductionLedgerModel?> GetForOrderAsync(Guid productionOrderUuid)
    {
        var po = await _db.ProductionOrders.AsNoTracking().FirstOrDefaultAsync(p => p.UUID == productionOrderUuid);
        if (po is null) return null;

        var entries = await BuildEntriesAsync(new ProductionLedgerListFilter { ProductionOrderUuid = productionOrderUuid, PageSize = int.MaxValue });
        var ordered = entries.OrderBy(e => e.TransactionDate).ToList();

        return new ProductionLedgerModel
        {
            ProductionOrderUuid = po.UUID,
            ProductionNumber    = po.ProductionNumber,
            Summary             = Summarize(ordered),
            Entries             = ordered
        };
    }

    public async Task<PaginatedResponse<ProductionLedgerEntryModel>> GetListAsync(ProductionLedgerListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var all     = (await BuildEntriesAsync(filter)).OrderByDescending(e => e.TransactionDate).ToList();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 50 : filter.PageSize, 1, 500);
        var items    = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new PaginatedResponse<ProductionLedgerEntryModel>
        {
            Data = items, TotalRecords = all.Count, Page = page, PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)all.Count / pageSize)
        };
    }

    public async Task<ProductionLedgerSummaryModel> GetSummaryAsync(ProductionLedgerListFilter filter) =>
        Summarize(await BuildEntriesAsync(filter));

    private static ProductionLedgerSummaryModel Summarize(IReadOnlyList<ProductionLedgerEntryModel> entries)
    {
        var materialsConsumed = entries
            .Where(e => e.EntryType == "DEBIT" && e.MovementType == InventoryTransactionType.ProductionIssue)
            .Select(e => e.VariantUuid).Distinct().Count();
        var finishedGoods = entries
            .Where(e => e.EntryType == "CREDIT" && e.MovementType == InventoryTransactionType.FinishedGoodsReceipt)
            .Sum(e => e.Quantity);
        var scrap = entries.Where(e => e.MovementType == InventoryTransactionType.ProductionScrap).Sum(e => e.Quantity);
        var denominator = finishedGoods + scrap;

        return new ProductionLedgerSummaryModel
        {
            MaterialsConsumedCount = materialsConsumed,
            FinishedGoodsQuantity  = finishedGoods,
            ScrapQuantity          = scrap,
            YieldPercent           = denominator > 0 ? Math.Round(finishedGoods / denominator * 100m, 2) : null
        };
    }

    /// <summary>
    /// Every debit/credit row in scope, stitched from the ledger entries Material Issue and FGR
    /// already wrote (joined back to their production order through the issue/receipt uuid the
    /// ledger's ReferenceId carries) plus a synthetic scrap row per inspection that rejected
    /// something — the one movement that never touched <c>InventoryLedgerEntries</c> at all.
    /// </summary>
    private async Task<List<ProductionLedgerEntryModel>> BuildEntriesAsync(ProductionLedgerListFilter filter)
    {
        var poQuery = _db.ProductionOrders.AsNoTracking().AsQueryable();
        if (filter.ProductionOrderUuid is { } one) poQuery = poQuery.Where(p => p.UUID == one);
        var orders = await poQuery
            .Select(p => new { p.Id, p.UUID, p.ProductionNumber, p.ProductVariantUuid, p.WarehouseUuid })
            .ToListAsync();
        if (orders.Count == 0) return [];
        var orderIds  = orders.Select(o => o.Id).ToList();
        var orderById = orders.ToDictionary(o => o.Id);

        var issues = await _db.ProductionMaterialIssues.AsNoTracking()
            .Where(i => orderIds.Contains(i.ProductionOrderId) && i.Status == ProductionIssueStatus.Confirmed)
            .Select(i => new { i.UUID, i.ProductionOrderId, i.IssueNumber })
            .ToListAsync();
        var issueByUuid = issues.ToDictionary(i => i.UUID);

        var fgrs = await _db.FinishedGoodsReceipts.AsNoTracking()
            .Where(f => orderIds.Contains(f.ProductionOrderId) && f.Status == FgrStatus.Confirmed)
            .Select(f => new { f.UUID, f.ProductionOrderId, f.FgrNumber })
            .ToListAsync();
        var fgrByUuid = fgrs.ToDictionary(f => f.UUID);

        var refUuids = issueByUuid.Keys.Concat(fgrByUuid.Keys).ToList();
        var entries  = new List<ProductionLedgerEntryModel>();

        if (refUuids.Count > 0)
        {
            var ledgerQuery = _inv.InventoryLedgerEntries.AsNoTracking()
                .Where(e => (e.ReferenceType == "PRODUCTION_ISSUE" || e.ReferenceType == "FGR") && refUuids.Contains(e.ReferenceId));
            if (filter.DateFrom is { } from) ledgerQuery = ledgerQuery.Where(e => e.TransactionDate >= from);
            if (filter.DateTo is { } to)     ledgerQuery = ledgerQuery.Where(e => e.TransactionDate <= to);
            if (!string.IsNullOrWhiteSpace(filter.MovementType)) ledgerQuery = ledgerQuery.Where(e => e.TransactionType == filter.MovementType);
            var ledgerRows = await ledgerQuery.ToListAsync();

            var variantIds   = ledgerRows.Select(e => e.VariantId).Distinct().ToList();
            var warehouseIds = ledgerRows.Select(e => e.WarehouseId).Distinct().ToList();
            var variants = variantIds.Count == 0 ? []
                : await _inv.ProductVariants.AsNoTracking().Where(v => variantIds.Contains(v.Id))
                    .Select(v => new { v.Id, v.Uuid, v.VariantName, ProductName = v.Product.Name, v.Product.UomCode })
                    .ToListAsync();
            var variantById = variants.ToDictionary(v => v.Id);
            var warehouses = warehouseIds.Count == 0 ? []
                : await _inv.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id))
                    .Select(w => new { w.Id, w.Uuid, w.Name }).ToListAsync();
            var warehouseById = warehouses.ToDictionary(w => w.Id);

            foreach (var row in ledgerRows)
            {
                var isFgr = row.ReferenceType == "FGR";
                var orderId = isFgr
                    ? (fgrByUuid.TryGetValue(row.ReferenceId, out var fgr) ? fgr.ProductionOrderId : (int?)null)
                    : (issueByUuid.TryGetValue(row.ReferenceId, out var issue) ? issue.ProductionOrderId : (int?)null);
                if (orderId is null || !orderById.TryGetValue(orderId.Value, out var order)) continue;

                var variant = variantById.GetValueOrDefault(row.VariantId);
                if (filter.VariantUuid is { } wantedVariant && variant?.Uuid != wantedVariant) continue;

                var entryType = row.QuantityIn.HasValue ? "CREDIT" : "DEBIT";
                if (!string.IsNullOrWhiteSpace(filter.EntryType) && !string.Equals(filter.EntryType, entryType, StringComparison.OrdinalIgnoreCase)) continue;

                var warehouse = warehouseById.GetValueOrDefault(row.WarehouseId);
                entries.Add(new ProductionLedgerEntryModel
                {
                    ProductionOrderUuid = order.UUID,
                    ProductionNumber    = order.ProductionNumber,
                    EntryType           = entryType,
                    VariantUuid         = variant?.Uuid ?? Guid.Empty,
                    ProductName         = variant?.ProductName ?? string.Empty,
                    VariantName         = variant?.VariantName ?? string.Empty,
                    Quantity            = row.QuantityIn ?? row.QuantityOut ?? 0m,
                    Uom                 = variant?.UomCode ?? string.Empty,
                    WarehouseUuid       = warehouse?.Uuid ?? Guid.Empty,
                    WarehouseName       = warehouse?.Name ?? string.Empty,
                    SourceDocumentType  = isFgr ? "FINISHED_GOODS_RECEIPT" : "MATERIAL_ISSUE",
                    SourceDocumentNumber = isFgr ? fgrByUuid[row.ReferenceId].FgrNumber : issueByUuid[row.ReferenceId].IssueNumber,
                    MovementType        = row.TransactionType,
                    TransactionDate     = row.TransactionDate,
                    Notes               = row.Notes
                });
            }
        }

        // Scrap never touched inventory (QualityInspectionService's own note), so it comes from the
        // inspection row itself, not a stock movement.
        var qis = await _db.QualityInspections.AsNoTracking()
            .Where(q => orderIds.Contains(q.ProductionOrderId) && q.RejectedQuantity > 0)
            .Select(q => new { q.ProductionOrderId, q.RejectedQuantity, q.InspectionNumber, q.InspectedAt, q.Notes })
            .ToListAsync();

        var missingVariantUuids = new List<Guid>();
        foreach (var qi in qis)
        {
            if (!orderById.TryGetValue(qi.ProductionOrderId, out var order)) continue;
            if (filter.VariantUuid is { } wantedVariant && order.ProductVariantUuid != wantedVariant) continue;
            if (!string.IsNullOrWhiteSpace(filter.EntryType) && !string.Equals(filter.EntryType, "DEBIT", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(filter.MovementType) && filter.MovementType != InventoryTransactionType.ProductionScrap) continue;
            if (filter.DateFrom is { } from2 && qi.InspectedAt < from2) continue;
            if (filter.DateTo is { } to2 && qi.InspectedAt > to2) continue;

            missingVariantUuids.Add(order.ProductVariantUuid);
            entries.Add(new ProductionLedgerEntryModel
            {
                ProductionOrderUuid  = order.UUID,
                ProductionNumber     = order.ProductionNumber,
                EntryType            = "DEBIT",
                VariantUuid          = order.ProductVariantUuid,
                Quantity             = qi.RejectedQuantity,
                WarehouseUuid        = order.WarehouseUuid,
                SourceDocumentType   = "QUALITY_INSPECTION",
                SourceDocumentNumber = qi.InspectionNumber,
                MovementType         = InventoryTransactionType.ProductionScrap,
                TransactionDate      = qi.InspectedAt,
                Notes                = qi.Notes
            });
        }

        if (missingVariantUuids.Count > 0)
        {
            var names = await _inv.ProductVariants.AsNoTracking().Where(v => missingVariantUuids.Contains(v.Uuid))
                .Select(v => new { v.Uuid, v.VariantName, ProductName = v.Product.Name, v.Product.UomCode })
                .ToDictionaryAsync(v => v.Uuid);
            var warehouseUuids = entries.Where(e => e.SourceDocumentType == "QUALITY_INSPECTION").Select(e => e.WarehouseUuid).Distinct().ToList();
            var warehouseNames = await _inv.Warehouses.AsNoTracking().Where(w => warehouseUuids.Contains(w.Uuid))
                .ToDictionaryAsync(w => w.Uuid, w => w.Name);

            foreach (var entry in entries.Where(e => e.SourceDocumentType == "QUALITY_INSPECTION"))
            {
                if (names.TryGetValue(entry.VariantUuid, out var n))
                {
                    entry.ProductName = n.ProductName;
                    entry.VariantName = n.VariantName;
                    entry.Uom         = n.UomCode ?? entry.Uom;
                }
                entry.WarehouseName = warehouseNames.GetValueOrDefault(entry.WarehouseUuid, string.Empty);
            }
        }

        return entries;
    }
}
