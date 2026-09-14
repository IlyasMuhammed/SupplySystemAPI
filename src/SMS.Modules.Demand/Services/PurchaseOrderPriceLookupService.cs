using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

// Implements the SMS.Shared.Common cross-module interface so SMS.Modules.Inventory's Rate Card
// grid can show "Last PO Price" without a project reference to Demand.
internal sealed class PurchaseOrderPriceLookupService : IPurchaseOrderPriceLookupService
{
    private readonly DemandDbContext _db;

    // Only these statuses represent an actually-agreed price — a draft/pending/rejected/cancelled
    // PO never reflects a real commercial commitment.
    private static readonly string[] QualifyingStatuses =
        ["APPROVED", "SENT", "PARTIALLY_RECEIVED", "RECEIVED", "PARTIALLY_INVOICED", "CLOSED"];

    public PurchaseOrderPriceLookupService(DemandDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<(Guid VariantUuid, Guid SupplierId), LastPoInfo>> GetLastPricesAsync(
        IReadOnlyList<(Guid VariantUuid, Guid SupplierId)> pairs)
    {
        if (pairs.Count == 0)
            return new Dictionary<(Guid, Guid), LastPoInfo>();

        var variantUuids = pairs.Select(p => p.VariantUuid).Distinct().ToList();
        var supplierIds  = pairs.Select(p => p.SupplierId).Distinct().ToList();

        // Narrow via the two Contains() (SQL-translatable), then exact-match the pairs in memory —
        // batch size is at most one grid page, so this stays small.
        var candidates = await _db.PurchaseOrderLines
            .Where(l => l.VariantUuid != null
                     && variantUuids.Contains(l.VariantUuid.Value)
                     && supplierIds.Contains(l.PurchaseOrder.SupplierId)
                     && QualifyingStatuses.Contains(l.PurchaseOrder.Status))
            .Select(l => new
            {
                VariantUuid = l.VariantUuid!.Value,
                SupplierId  = l.PurchaseOrder.SupplierId,
                l.UnitPrice,
                PoCreatedDate = l.PurchaseOrder.CreatedDate
            })
            .ToListAsync();

        var pairSet = pairs.ToHashSet();

        return candidates
            .Where(c => pairSet.Contains((c.VariantUuid, c.SupplierId)))
            .GroupBy(c => (c.VariantUuid, c.SupplierId))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.OrderByDescending(c => c.PoCreatedDate).First();
                    return new LastPoInfo(latest.UnitPrice, latest.PoCreatedDate);
                });
    }

    public async Task<PoReferenceSummary?> GetPoReferenceSummaryAsync(Guid variantUuid, Guid supplierId)
    {
        var qualifyingLines = await _db.PurchaseOrderLines
            .Where(l => l.VariantUuid == variantUuid
                     && l.PurchaseOrder.SupplierId == supplierId
                     && QualifyingStatuses.Contains(l.PurchaseOrder.Status))
            .Select(l => new
            {
                PoUuid        = l.PurchaseOrder.UUID,
                PoNumber      = l.PurchaseOrder.PoNumber,
                l.UnitPrice,
                PoCreatedDate = l.PurchaseOrder.CreatedDate
            })
            .ToListAsync();

        if (qualifyingLines.Count == 0) return null;

        var latest = qualifyingLines.OrderByDescending(l => l.PoCreatedDate).First();
        var cutoff = DateTime.UtcNow.AddMonths(-12);
        var countLast12Months = qualifyingLines.Count(l => l.PoCreatedDate >= cutoff);

        return new PoReferenceSummary(
            latest.PoUuid, latest.PoNumber, latest.PoCreatedDate, latest.UnitPrice, countLast12Months);
    }
}
