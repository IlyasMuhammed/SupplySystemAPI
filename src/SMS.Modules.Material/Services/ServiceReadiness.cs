using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A36 D-6 / FSD §5.5 — service order readiness, as pure arithmetic (the service, the listener and the tests apply the
/// same rules). A requirement is <b>covered</b> when:
/// <list type="bullet">
/// <item>STOCK — what it still needs from stock is held for it (production's rule; supply merely planned does not count);</item>
/// <item>SUBCONTRACT — its supply requirement has reached ORDERED or later (labour never arrives into stock, R-2);</item>
/// <item>INTERNAL_LABOR — always (tracking only, never critical).</item>
/// </list>
/// No live requirement at all ⇒ NOT_APPLICABLE and the order is ready (SVC-05). Before the job: every critical requirement
/// covered ⇒ READY, else MATERIAL_PENDING (ST-02..04). During it: ⇒ IN_PROGRESS, else WAITING (ST-06/07).
/// </summary>
internal static class ServiceReadiness
{
    private static readonly string[] SubcontractCoveredStatuses =
        [SupplyRequirementStatus.Ordered, SupplyRequirementStatus.PartiallyReceived, SupplyRequirementStatus.Fulfilled];

    public static bool IsLive(ServiceMaterialRequirement smr) => smr.Status != SmrStatus.Cancelled;

    public static bool IsCovered(ServiceMaterialRequirement smr, string? supplyStatus) => smr.SourceType switch
    {
        BomLineSourceType.InternalLabor => true,
        BomLineSourceType.Subcontract   => smr.Status == SmrStatus.Consumed || SubcontractCoveredStatuses.Contains(supplyStatus),
        _                               => smr.Outstanding - smr.ReservedQuantity <= 0m
    };

    /// <summary>A STOCK requirement: what the engine holds for it, what nothing covers, and the status that implies.</summary>
    public static void ApplyStock(ServiceMaterialRequirement smr, DemandAllocationSummary? demand)
    {
        if (!smr.IsStock || SmrStatus.IsFinal(smr.Status)) return;

        smr.ReservedQuantity = demand?.ReservedQty ?? 0m;
        smr.ShortageQuantity = Math.Max(0m, smr.Outstanding - smr.ReservedQuantity);
        smr.Status =
            smr.Outstanding <= 0m                                  ? SmrStatus.Issued :
            smr.ReservedQuantity >= smr.Outstanding                ? SmrStatus.FullyReserved :
            smr.ReservedQuantity > 0m || smr.IssuedQuantity > 0m   ? SmrStatus.PartiallyReserved :
                                                                     SmrStatus.Pending;
        smr.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>A SUBCONTRACT or INTERNAL_LABOR requirement, from its supply requirement's status.</summary>
    public static void ApplyNonStock(ServiceMaterialRequirement smr, string? supplyStatus)
    {
        if (smr.IsStock || SmrStatus.IsFinal(smr.Status)) return;

        var covered = IsCovered(smr, supplyStatus);
        smr.ShortageQuantity = covered ? 0m : smr.RequiredQuantity;
        smr.Status = smr.SourceType == BomLineSourceType.Subcontract && covered ? SmrStatus.FullyReserved : SmrStatus.Pending;
        smr.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Re-derives the order's readiness and, while it is waiting for or running on materials, its status.</summary>
    public static void ApplyOrder(ServiceOrder order, IReadOnlyDictionary<Guid, string> supplyStatusBySmr)
    {
        if (ServiceOrderStatus.IsTerminal(order.Status) || order.Status == ServiceOrderStatus.Draft) return;

        var live = order.Materials.Where(IsLive).ToList();
        bool Covered(ServiceMaterialRequirement m) => IsCovered(m, supplyStatusBySmr.GetValueOrDefault(m.UUID));

        var allCriticalCovered = live.Where(m => m.IsCritical).All(Covered);
        var covered = live.Count(Covered);

        order.MaterialReadiness =
            live.Count == 0       ? ServiceReadinessCode.NotApplicable :
            covered == live.Count ? ServiceReadinessCode.Ready :
            covered == 0          ? ServiceReadinessCode.Shortage :
                                    ServiceReadinessCode.Partial;

        if (ServiceOrderStatus.IsAwaitingMaterials(order.Status))
            order.Status = allCriticalCovered ? ServiceOrderStatus.Ready : ServiceOrderStatus.MaterialPending;
        else if (ServiceOrderStatus.IsRunning(order.Status))
            order.Status = allCriticalCovered ? ServiceOrderStatus.InProgress : ServiceOrderStatus.Waiting;

        order.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>The live (else latest) SERVICE_ORDER supply requirement status of each requirement that has one.</summary>
    public static async Task<Dictionary<Guid, string>> SupplyStatusesAsync(
        MaterialDbContext db, Guid organizationId, IReadOnlyCollection<Guid> smrUuids, CancellationToken ct)
    {
        if (smrUuids.Count == 0) return [];
        var rows = await db.SupplyRequirements.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.DemandSourceType == SupplyDemandSourceType.ServiceOrder &&
                        smrUuids.Contains(s.DemandSourceUuid))
            .Select(s => new { s.Id, s.DemandSourceUuid, s.Status })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.DemandSourceUuid).ToDictionary(g => g.Key,
            g => (g.Where(r => SupplyRequirementStatus.IsLive(r.Status)).OrderByDescending(r => r.Id).FirstOrDefault()
                  ?? g.Where(r => r.Status != SupplyRequirementStatus.Cancelled).OrderByDescending(r => r.Id).FirstOrDefault()
                  ?? g.OrderByDescending(r => r.Id).First()).Status);
    }
}

/// <summary>
/// A36-P2-08 / X-03 — told after every allocation run (and every receipt that closes expected supply). When a run touched
/// a SERVICE_ORDER demand, its requirement and order are re-derived (ST-03/04/07, incl. WAITING → IN_PROGRESS); a receipt
/// re-derives orders whose subcontracted requirement is for the received variant. Never calls the engine back; issuing
/// what became held is left to the next action on the order (start, allocate, add material).
/// </summary>
internal sealed class ServiceReadinessListener : IAllocationRunListener, IAllocationReceiptListener
{
    private readonly MaterialDbContext _db;

    public ServiceReadinessListener(MaterialDbContext db) => _db = db;

    public async Task OnAllocationRunAsync(AllocationRunResult result, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var touched = result.Demands
            .Where(d => d.DemandType == AllocationDemandType.ServiceOrder)
            .GroupBy(d => d.Uuid)
            .ToDictionary(g => g.Key, g => g.First());
        if (touched.Count == 0) return;

        var keys = touched.Keys.ToList();
        var orderIds = await _db.ServiceMaterialRequirements.IgnoreQueryFilters()
            .Where(m => m.AllocationDemandUuid != null && keys.Contains(m.AllocationDemandUuid.Value))
            .Select(m => m.ServiceOrderId).Distinct().ToListAsync(ct);
        if (orderIds.Count == 0) return;

        var orders = await _db.ServiceOrders.IgnoreQueryFilters().Include(o => o.Materials)
            .Where(o => orderIds.Contains(o.Id)).ToListAsync(ct);

        foreach (var order in orders)
        {
            foreach (var smr in order.Materials)
                if (smr.AllocationDemandUuid is { } key && touched.TryGetValue(key, out var demand))
                    ServiceReadiness.ApplyStock(smr, demand);
            await ApplyOrderAsync(order, ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task OnSupplyClosedAsync(Guid variantUuid, CancellationToken ct = default)
    {
        var orderIds = await _db.ServiceMaterialRequirements.IgnoreQueryFilters()
            .Where(m => m.MaterialVariantUuid == variantUuid && m.SourceType == BomLineSourceType.Subcontract && m.Status != SmrStatus.Cancelled)
            .Select(m => m.ServiceOrderId).Distinct().ToListAsync(ct);
        if (orderIds.Count == 0) return;

        var orders = await _db.ServiceOrders.IgnoreQueryFilters().Include(o => o.Materials)
            .Where(o => orderIds.Contains(o.Id)).ToListAsync(ct);
        foreach (var order in orders.Where(o => !ServiceOrderStatus.IsTerminal(o.Status) && o.Status != ServiceOrderStatus.Draft))
            await ApplyOrderAsync(order, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyOrderAsync(ServiceOrder order, CancellationToken ct)
    {
        var supply = await ServiceReadiness.SupplyStatusesAsync(_db, order.OrganizationId,
            order.Materials.Where(m => !m.IsStock).Select(m => m.UUID).ToList(), ct);
        foreach (var smr in order.Materials.Where(m => !m.IsStock))
            ServiceReadiness.ApplyNonStock(smr, supply.GetValueOrDefault(smr.UUID));
        ServiceReadiness.ApplyOrder(order, supply);
    }
}
