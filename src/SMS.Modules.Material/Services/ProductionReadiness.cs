using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// The material readiness rules (A30 §11.3, §12.2, BR-PR04), as arithmetic over a production
/// order and what the allocation engine says each of its requirements holds. Pure, so the
/// service, the run listener and the tests all apply exactly the same rules.
/// <para>
/// A requirement is <b>covered</b> when what it still needs from stock (required, less what the
/// floor already has) is held for it. Being short of a critical requirement keeps the order at
/// MATERIAL_PENDING; being short only of non-critical ones does not.
/// </para>
/// </summary>
internal static class ProductionReadiness
{
    /// <summary>Required, less issued (net of returns): what still has to come out of stock.</summary>
    public static decimal Outstanding(ProductionMaterialRequirement pmr) => pmr.Outstanding;

    /// <summary>Held for it (reserved) at least covers what it still needs from stock.</summary>
    public static bool IsCovered(ProductionMaterialRequirement pmr) =>
        pmr.Status != PmrStatus.Cancelled && pmr.Outstanding - pmr.ReservedQuantity <= 0m;

    /// <summary>
    /// Writes the engine's view of one requirement onto it: what is held, what is planned against
    /// supply on its way, what nothing covers, and the status those imply.
    /// </summary>
    public static void Apply(ProductionMaterialRequirement pmr, DemandAllocationSummary? demand)
    {
        if (pmr.Status == PmrStatus.Cancelled) return;

        pmr.ReservedQuantity = demand?.ReservedQty ?? 0m;
        pmr.PlannedQuantity  = demand?.PlannedQty  ?? 0m;
        pmr.ConsumedQuantity = Math.Max(0m, pmr.IssuedQuantity - pmr.ReturnedQuantity);
        pmr.ShortageQuantity = Math.Max(0m, pmr.Outstanding - pmr.ReservedQuantity - pmr.PlannedQuantity);
        pmr.Status           = StatusOf(pmr);
        pmr.UpdatedAt        = DateTime.UtcNow;
    }

    public static string StatusOf(ProductionMaterialRequirement pmr)
    {
        if (pmr.Status is PmrStatus.Cancelled or PmrStatus.Consumed) return pmr.Status;
        if (pmr.Outstanding <= 0m)                                     return PmrStatus.Issued;
        if (pmr.ReservedQuantity >= pmr.Outstanding)                   return PmrStatus.FullyReserved;
        if (pmr.ReservedQuantity > 0m || pmr.IssuedQuantity > 0m)      return PmrStatus.PartiallyReserved;
        return PmrStatus.Pending;
    }

    /// <summary>
    /// Re-derives the order's readiness and, while it is waiting for materials, its status:
    /// READY once every critical requirement is covered, MATERIAL_PENDING until then.
    /// </summary>
    public static void Apply(ProductionOrder po)
    {
        var live = po.Materials.Where(m => m.Status != PmrStatus.Cancelled).ToList();
        if (live.Count == 0)
        {
            po.MaterialReadiness = MaterialReadiness.NotChecked;
            return;
        }

        var covered  = live.Count(IsCovered);
        var critical = live.Where(m => m.IsCritical).ToList();
        var allCriticalCovered = critical.All(IsCovered);

        po.MaterialReadiness =
            covered == live.Count ? MaterialReadiness.Ready :
            covered == 0          ? MaterialReadiness.Shortage :
                                    MaterialReadiness.Partial;

        if (ProductionOrderStatus.IsAwaitingMaterials(po.Status))
            po.Status = allCriticalCovered ? ProductionOrderStatus.Ready : ProductionOrderStatus.MaterialPending;

        po.UpdatedAt = DateTime.UtcNow;
    }

    public static bool AllCriticalCovered(ProductionOrder po) =>
        po.Materials.Where(m => m.Status != PmrStatus.Cancelled && m.IsCritical).All(IsCovered);

    /// <summary>Applies a set of engine summaries (keyed by registry uuid) to an order's requirements, then the order.</summary>
    public static void Apply(ProductionOrder po, IReadOnlyDictionary<Guid, DemandAllocationSummary> demandsByRegistryUuid)
    {
        foreach (var pmr in po.Materials)
        {
            if (pmr.Status == PmrStatus.Cancelled) continue;
            DemandAllocationSummary? demand = null;
            if (pmr.AllocationDemandUuid is { } key) demandsByRegistryUuid.TryGetValue(key, out demand);
            Apply(pmr, demand);
        }
        Apply(po);
    }
}

/// <summary>
/// A30-P3-08 / A30-P4-13 — told after every allocation run. When the run touched a production
/// material requirement, the order it belongs to is re-derived (§11.3 auto-transition), and any
/// supply requirement whose ordered supply the run booked a receipt against is brought up to date.
/// Never calls the engine back; it only reads the registries the run has already committed.
/// </summary>
internal sealed class ProductionReadinessListener : IAllocationRunListener
{
    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;
    // Optional the same way every other DI-only-in-production dependency in this codebase is —
    // production DI supplies it, this module's own unit tests (which register this listener directly)
    // have no use for real notifications.
    private readonly IManufacturingNotificationService? _notify;

    public ProductionReadinessListener(MaterialDbContext db, InventoryDbContext inv, IManufacturingNotificationService? notify = null)
    {
        _db     = db;
        _inv    = inv;
        _notify = notify;
    }

    public async Task OnAllocationRunAsync(AllocationRunResult result, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var touched = result.Demands
            .Where(d => d.DemandType == AllocationDemandType.ProductionMaterial)
            .GroupBy(d => d.Uuid)
            .ToDictionary(g => g.Key, g => g.First());
        if (touched.Count == 0) return;

        var keys = touched.Keys.ToList();
        var orderIds = await _db.ProductionMaterialRequirements
            .Where(m => m.AllocationDemandUuid != null && keys.Contains(m.AllocationDemandUuid.Value))
            .Select(m => m.ProductionOrderId)
            .Distinct()
            .ToListAsync(ct);
        if (orderIds.Count == 0) return;

        var orders = await _db.ProductionOrders.Include(p => p.Materials)
            .Where(p => orderIds.Contains(p.Id))
            .ToListAsync(ct);

        // A30-P5-01 — captured before Apply() overwrites them, so a transition (not just a state) is
        // what triggers a notification: a material newly covered, or an order newly ready.
        var newlyCovered = new List<(ProductionOrder Order, ProductionMaterialRequirement Pmr)>();
        var newlyReady   = new List<ProductionOrder>();

        foreach (var po in orders)
        {
            var wasReady = po.Status == ProductionOrderStatus.Ready;

            // Only the demands this run evaluated are fresh; the rest keep what they had.
            foreach (var pmr in po.Materials)
            {
                if (pmr.Status == PmrStatus.Cancelled || pmr.AllocationDemandUuid is not { } key) continue;
                if (!touched.TryGetValue(key, out var demand)) continue;
                var wasCovered = ProductionReadiness.IsCovered(pmr);
                ProductionReadiness.Apply(pmr, demand);
                if (!wasCovered && ProductionReadiness.IsCovered(pmr)) newlyCovered.Add((po, pmr));
            }
            ProductionReadiness.Apply(po);
            if (!wasReady && po.Status == ProductionOrderStatus.Ready) newlyReady.Add(po);
        }

        await SyncSupplyRequirementsAsync(result.VariantUuid, ct);
        await _db.SaveChangesAsync(ct);

        if (_notify is not null) await NotifyAsync(newlyCovered, newlyReady);
    }

    private async Task NotifyAsync(List<(ProductionOrder Order, ProductionMaterialRequirement Pmr)> newlyCovered, List<ProductionOrder> newlyReady)
    {
        if (newlyCovered.Count > 0)
        {
            var names = await _inv.Products.AsNoTracking()
                .Where(p => newlyCovered.Select(m => m.Pmr.MaterialProductUuid).Contains(p.Uuid))
                .ToDictionaryAsync(p => p.Uuid, p => p.Name);
            foreach (var (order, pmr) in newlyCovered)
                await _notify!.AllocationCompletedAsync(order, names.GetValueOrDefault(pmr.MaterialProductUuid, pmr.MaterialProductUuid.ToString()), pmr.RequiredQuantity, pmr.Uom);
        }
        foreach (var po in newlyReady)
            await _notify!.ProductionOrderReadyAsync(po);
    }

    /// <summary>
    /// A supply requirement that raised a purchase or child production order registered that
    /// document as expected supply. The receipt booked against it (by the GRN, the FGR) is what
    /// fulfils the requirement, so the registry's received quantity is copied here.
    /// </summary>
    private async Task SyncSupplyRequirementsAsync(Guid variantUuid, CancellationToken ct)
    {
        var open = await _db.SupplyRequirements
            .Where(s => s.VariantUuid == variantUuid && s.SupplySourceUuid != null &&
                        (s.Status == SupplyRequirementStatus.Ordered || s.Status == SupplyRequirementStatus.Planned ||
                         s.Status == SupplyRequirementStatus.PartiallyReceived))
            .ToListAsync(ct);
        if (open.Count == 0) return;

        var sourceUuids = open.Select(s => s.SupplySourceUuid!.Value)
            .Concat(open.Where(s => s.SupplySourceLineUuid.HasValue).Select(s => s.SupplySourceLineUuid!.Value))
            .Distinct().ToList();

        var supplies = await _inv.AllocationSupplies.AsNoTracking()
            .Where(s => s.VariantUuid == variantUuid &&
                        (sourceUuids.Contains(s.SupplyUuid) || (s.SupplyLineUuid != null && sourceUuids.Contains(s.SupplyLineUuid.Value))))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var sr in open)
        {
            var supply = supplies.FirstOrDefault(s =>
                (sr.SupplySourceLineUuid is { } line && (s.SupplyLineUuid == line || s.SupplyUuid == line)) ||
                s.SupplyUuid == sr.SupplySourceUuid);
            if (supply is null) continue;

            sr.QuantityReceived = supply.ReceivedQty;
            sr.Status = supply.Status == AllocationSupplyStatus.Cancelled ? sr.Status
                      : supply.ReceivedQty >= sr.QuantityRequired || supply.Status == AllocationSupplyStatus.Received
                          ? SupplyRequirementStatus.Fulfilled
                      : supply.ReceivedQty > 0m ? SupplyRequirementStatus.PartiallyReceived
                      : sr.Status;
            sr.UpdatedAt = now;
        }
    }
}
