using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

internal sealed class PurchaseRequiredService : IPurchaseRequiredService
{
    private static readonly string[] ActiveStatuses =
        [ProductionOrderStatus.Planned, ProductionOrderStatus.MaterialPending, ProductionOrderStatus.Ready, ProductionOrderStatus.InProgress];

    private readonly MaterialDbContext          _db;
    private readonly InventoryDbContext         _inv;
    private readonly ISupplierNameLookupService _supplierNames;
    private readonly ISupplyRequirementEngine   _supplyEngine;

    public PurchaseRequiredService(
        MaterialDbContext db, InventoryDbContext inv, ISupplierNameLookupService supplierNames, ISupplyRequirementEngine supplyEngine)
    {
        _db            = db;
        _inv           = inv;
        _supplierNames = supplierNames;
        _supplyEngine  = supplyEngine;
    }

    /// <summary>Every open, uncancelled material requirement with a genuine shortage (nothing — held or
    /// planned — covers it), across every still-active production order, and not already covered by a
    /// fully-received purchase. The one query both the dashboard's own aggregation and the
    /// affected-orders drawer are built from.
    /// <para>
    /// A31 C10 hotfix — <c>ProductionMaterialRequirement.ShortageQuantity</c> is only recomputed when
    /// allocation runs for the variant, and C10 made that a separate, manual step instead of something
    /// a GRN triggers automatically. A shortage whose own supply requirement has already reached
    /// <see cref="SupplyRequirementStatus.Fulfilled"/> (found live: the goods were received in full,
    /// but the row still showed a stale shortage and "Create Purchase Order" lit back up because
    /// nobody had clicked "Run Allocation" yet) has nothing left to buy — that PMR's own stale
    /// shortage is a Material Availability / "Run Allocation" problem now, not a Purchase Required one,
    /// so it is excluded here rather than left to confuse procurement into raising a second PO.
    /// </para>
    /// </summary>
    private async Task<List<ProductionMaterialRequirement>> OpenShortagesAsync()
    {
        var pmrs = await _db.ProductionMaterialRequirements.AsNoTracking().Include(m => m.ProductionOrder)
            .Where(m => m.Status != PmrStatus.Cancelled && m.ShortageQuantity > 0 && ActiveStatuses.Contains(m.ProductionOrder.Status))
            .ToListAsync();
        if (pmrs.Count == 0) return pmrs;

        var pmrUuids = pmrs.Select(m => m.UUID).ToList();
        var fulfilledPmrUuids = await _db.SupplyRequirements.AsNoTracking()
            .Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement &&
                        pmrUuids.Contains(s.DemandSourceUuid) && s.SupplyMethod == SupplyMethod.Purchase &&
                        s.Status == SupplyRequirementStatus.Fulfilled)
            .Select(s => s.DemandSourceUuid)
            .ToListAsync();
        if (fulfilledPmrUuids.Count == 0) return pmrs;

        var fulfilled = fulfilledPmrUuids.ToHashSet();
        return pmrs.Where(m => !fulfilled.Contains(m.UUID)).ToList();
    }

    public async Task<IReadOnlyList<PurchaseRequiredLineModel>> GetListAsync(PurchaseRequiredListFilter filter)
    {
        var shortages = await OpenShortagesAsync();
        if (shortages.Count == 0) return [];

        var byVariant = shortages.GroupBy(m => m.MaterialVariantUuid).ToList();
        var variantUuids = byVariant.Select(g => g.Key).ToList();

        var variants = await _inv.ProductVariants.AsNoTracking().Include(v => v.Product)
            .Where(v => variantUuids.Contains(v.Uuid))
            .ToDictionaryAsync(v => v.Uuid);

        var variantIds = variants.Values.Select(v => v.Id).ToList();
        var onHandByVariantId = await _inv.InventoryItems.AsNoTracking()
            .Where(i => variantIds.Contains(i.VariantId))
            .GroupBy(i => i.VariantId)
            .Select(g => new { VariantId = g.Key, Qty = g.Sum(i => i.QtyOnHand) })
            .ToDictionaryAsync(x => x.VariantId, x => x.Qty);

        var supplierIds = variants.Values.Where(v => v.DefaultSupplierId.HasValue)
            .Select(v => v.DefaultSupplierId!.Value).Distinct().ToList();
        var supplierNames = supplierIds.Count > 0 ? await _supplierNames.GetNamesAsync(supplierIds)
            : new Dictionary<Guid, string>();

        var pmrUuids = shortages.Select(m => m.UUID).ToList();
        var srByPmr = await PendingPurchaseSrsByPmrAsync(pmrUuids);

        var acks = await _db.PurchaseRequiredAcknowledgements.AsNoTracking()
            .Where(a => variantUuids.Contains(a.VariantUuid))
            .ToDictionaryAsync(a => a.VariantUuid);

        var result = new List<PurchaseRequiredLineModel>();
        foreach (var g in byVariant)
        {
            if (!variants.TryGetValue(g.Key, out var variant)) continue;
            var product = variant.Product;

            if (filter.SupplierId is { } wantedSupplier && variant.DefaultSupplierId != wantedSupplier) continue;
            var totalShortage = g.Sum(m => m.ShortageQuantity);
            if (filter.MinShortageQty is { } minQty && totalShortage < minQty) continue;

            var pendingSr = g.Select(m => srByPmr.GetValueOrDefault(m.UUID)).FirstOrDefault(s => s is not null);
            var ack = acks.GetValueOrDefault(g.Key);
            var first = g.First();

            result.Add(new PurchaseRequiredLineModel
            {
                VariantUuid          = variant.Uuid,
                ProductUuid          = product.Uuid,
                ProductName          = product.Name,
                VariantName          = variant.VariantName,
                Sku                  = variant.Sku,
                Uom                  = first.Uom,
                TotalShortageQty     = totalShortage,
                AffectedPoCount      = g.Select(m => m.ProductionOrderId).Distinct().Count(),
                EarliestRequiredDate = g.Min(m => m.RequiredDate),
                DefaultSupplierId    = variant.DefaultSupplierId,
                DefaultSupplierName  = variant.DefaultSupplierId is { } sid ? supplierNames.GetValueOrDefault(sid) : null,
                CurrentStockOnHand   = onHandByVariantId.GetValueOrDefault(variant.Id),
                PendingPoUuid        = pendingSr?.SupplySourceUuid,
                PendingPoNumber      = pendingSr?.SupplySourceReference,
                PendingPoQuantity    = pendingSr?.QuantityOrdered,
                PendingPoStatus      = pendingSr?.Status,
                IsAcknowledgedManually = ack is not null,
                AcknowledgedNotes    = ack?.Notes,
                AcknowledgedAt       = ack?.AcknowledgedAt
            });
        }

        return (filter.SortBy?.ToLowerInvariant() switch
        {
            "urgency" => result.OrderBy(r => r.EarliestRequiredDate),
            "product" => result.OrderBy(r => r.ProductName),
            _         => result.OrderByDescending(r => r.TotalShortageQty)
        }).ToList();
    }

    public async Task<IReadOnlyList<PurchaseRequiredAffectedOrderModel>> GetAffectedOrdersAsync(Guid variantUuid)
    {
        var shortages = await OpenShortagesAsync();
        return shortages.Where(m => m.MaterialVariantUuid == variantUuid)
            .OrderBy(m => m.ProductionOrder.PlannedStartDate ?? m.RequiredDate)
            .Select(m => new PurchaseRequiredAffectedOrderModel
            {
                ProductionOrderUuid = m.ProductionOrder.UUID,
                ProductionNumber    = m.ProductionOrder.ProductionNumber,
                Status              = m.ProductionOrder.Status,
                PlannedQuantity     = m.ProductionOrder.PlannedQuantity,
                ShortageQuantity    = m.ShortageQuantity,
                PlannedStartDate    = m.ProductionOrder.PlannedStartDate,
                RequiredDate        = m.RequiredDate
            })
            .ToList();
    }

    /// <summary>The most recent live purchase-method supply requirement each shortage-raising PMR has,
    /// keyed by the PMR's own uuid — this already carries the PO PC-06 consolidated onto, if any.</summary>
    private async Task<Dictionary<Guid, SupplyRequirement>> PendingPurchaseSrsByPmrAsync(List<Guid> pmrUuids)
    {
        var srs = await _db.SupplyRequirements.AsNoTracking()
            .Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement &&
                        pmrUuids.Contains(s.DemandSourceUuid) &&
                        s.SupplyMethod == SupplyMethod.Purchase &&
                        SupplyRequirementStatus.LiveStatuses.Contains(s.Status) &&
                        s.SupplySourceReference != null)
            .ToListAsync();
        return srs.GroupBy(s => s.DemandSourceUuid).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Id).First());
    }

    public async Task AcknowledgeAsync(Guid variantUuid, AcknowledgePurchaseRequiredRequest req, int userId)
    {
        var existing = await _db.PurchaseRequiredAcknowledgements.FirstOrDefaultAsync(a => a.VariantUuid == variantUuid);
        if (existing is null)
        {
            _db.PurchaseRequiredAcknowledgements.Add(new PurchaseRequiredAcknowledgement
            {
                VariantUuid = variantUuid, Notes = req.Notes, AcknowledgedBy = userId, AcknowledgedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Notes = req.Notes;
            existing.AcknowledgedBy = userId;
            existing.AcknowledgedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    public async Task ClearAcknowledgementAsync(Guid variantUuid)
    {
        var existing = await _db.PurchaseRequiredAcknowledgements.FirstOrDefaultAsync(a => a.VariantUuid == variantUuid);
        if (existing is null) return;
        _db.PurchaseRequiredAcknowledgements.Remove(existing);
        await _db.SaveChangesAsync();
    }

    public async Task<Guid> CreatePurchaseOrderAsync(Guid variantUuid, CreatePurchaseRequiredPoRequest req, int userId)
    {
        var result = await _supplyEngine.CreatePurchaseOrderForShortagesAsync(
            variantUuid, req.SupplierId, req.SupplierName, req.Quantity, req.UnitPrice, req.RequiredDate, req.Notes, userId);
        return result.PoUuid;
    }
}
