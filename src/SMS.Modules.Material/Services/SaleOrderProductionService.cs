using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A34 C5 (D-17, D-18, D-22, D-25) — production orders made to order for sale order lines. Demand calls it: create and
/// cancel from inside its <see cref="SaleOrderLocks.HoldsResource"/> lock (this class takes no lock of its own), plan
/// after the lock is released. A30's make-to-shortage path (<see cref="IProductionDemandService"/>) is untouched.
/// <para>
/// <b>Organization explicit everywhere</b> (R-11): every read filters on the organization passed in, with the EF tenant
/// filter ignored (it is off for super admins and in Hangfire anyway), and every PO is stamped with it.
/// </para>
/// <para>
/// <b>Dependencies are resolved when used</b>, not in the constructor: Demand's <c>SaleOrderService</c> takes this service,
/// and the production graph behind it (planning, supply engine, purchase drafts) must neither be built on every sale
/// order request nor ever be able to close a constructor cycle back into Demand.
/// </para>
/// </summary>
internal sealed class SaleOrderProductionService : ISaleOrderProductionService
{
    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;
    private readonly IServiceProvider   _services;
    private readonly ILogger<SaleOrderProductionService>? _log;

    public SaleOrderProductionService(
        MaterialDbContext db, InventoryDbContext inv, IServiceProvider services, ILogger<SaleOrderProductionService>? log = null)
    {
        _db       = db;
        _inv      = inv;
        _services = services;
        _log      = log;
    }

    private IProductionOrderRepository Repository => _services.GetRequiredService<IProductionOrderRepository>();
    private ProductionOrderService     Orders     => _services.GetRequiredService<ProductionOrderService>();
    private IAllocationEngine          Engine     => _services.GetRequiredService<IAllocationEngine>();
    private ISupplyRequirementEngine   Supply     => _services.GetRequiredService<ISupplyRequirementEngine>();
    private IBackgroundJobClient?      Jobs       => _services.GetService<IBackgroundJobClient>();
    private IManufacturingNotificationService? Notify => _services.GetService<IManufacturingNotificationService>();
    private IFulfillmentRouteLookup?   Routes     => _services.GetService<IFulfillmentRouteLookup>();

    private IQueryable<ProductionOrder> SaleOrderPos(Guid organizationId, Guid saleOrderUuid) =>
        _db.ProductionOrders.IgnoreQueryFilters()
           .Where(p => p.OrganizationId == organizationId && p.SourceType == ProductionSourceType.SalesOrder && p.SourceUuid == saleOrderUuid);

    // ── Create (D-17 step 2, PD-01) ───────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SaleOrderProductionRef>> CreateDraftsAsync(
        Guid organizationId, SaleOrderProductionRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (organizationId == Guid.Empty) throw new BadRequestException("An organization is required.");
        var lines = (request.Lines ?? []).GroupBy(l => l.SoLineUuid).Select(g => g.First()).ToList();
        if (lines.Count == 0) return [];

        // Idempotent per line: a non-cancelled PO of this order and line that carries a route is the line's PO.
        var existing = await SaleOrderPos(organizationId, request.SaleOrderUuid).AsNoTracking()
            .Where(p => p.Status != ProductionOrderStatus.Cancelled && p.FulfillmentRouteUuid != null)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var missing = lines.Where(l => !existing.Any(p => p.SourceLineUuid == l.SoLineUuid)).ToList();
        await EnsureRoutesAsync(organizationId, missing, ct);

        var variants = missing.Count == 0
            ? new Dictionary<Guid, (Guid ProductUuid, string ProductName)>()
            : await _inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.OrganizationId == organizationId && missing.Select(l => l.VariantUuid).Contains(v.Uuid))
                .Select(v => new { v.Uuid, ProductUuid = v.Product.Uuid, ProductName = v.Product.Name })
                .ToDictionaryAsync(v => v.Uuid, v => (v.ProductUuid, v.ProductName), ct);

        var today    = DateTime.UtcNow.Date;
        var priority = AllocationPriority.IsKnown(request.Priority) ? request.Priority : AllocationPriority.Normal;
        var result   = new List<SaleOrderProductionRef>(lines.Count);

        foreach (var line in lines)
        {
            if (existing.FirstOrDefault(p => p.SourceLineUuid == line.SoLineUuid) is { } found)
            {
                result.Add(ToRef(found, created: false));
                continue;
            }

            if (line.Quantity <= 0)
                throw new BadRequestException($"Sale order {request.SoNumber}: a make-to-order line needs a quantity above zero.");
            if (!variants.TryGetValue(line.VariantUuid, out var variant))
                throw new BadRequestException($"Sale order {request.SoNumber}: variant {line.VariantUuid} does not exist.");

            // D-19 dates arrive date-only; never in the past (the repository refuses that) and start ≤ required.
            var required = line.RequiredDate.Date < today ? today : line.RequiredDate.Date;
            DateTime? start = line.PlannedStartDate?.Date;
            if (start is { } s && s > required) start = required;

            var po = await Repository.CreateAsync(new CreateProductionOrderRequest
            {
                ProductUuid        = variant.ProductUuid,
                ProductVariantUuid = line.VariantUuid,
                PlannedQuantity    = line.Quantity,
                RequiredDate       = required,
                PlannedStartDate   = start,
                Priority           = priority,
                SourceType         = ProductionSourceType.SalesOrder,
                SourceUuid         = request.SaleOrderUuid,
                SourceLineUuid     = line.SoLineUuid,
                SourceReference    = request.SoNumber,
                Notes              = $"Made to order for {request.SoNumber}."
            }, userId, traceId: request.TraceId, organizationId: organizationId, fulfillmentRouteUuid: line.FulfillmentRouteUuid);

            EnqueueTimeline(po, ManufacturingTimelineEventTypes.ProdCreated, userId, $"Made to order for {request.SoNumber}.");
            if (Notify is { } notify) await notify.ProductionOrderCreatedAsync(po, variant.ProductName, userId);
            result.Add(ToRef(po, created: true));
        }

        return result;
    }

    /// <summary>R-12 — the route a PO will carry must be a MANUFACTURE route of the same organization (when routes are on).</summary>
    private async Task EnsureRoutesAsync(Guid organizationId, IReadOnlyList<SaleOrderProductionLine> lines, CancellationToken ct)
    {
        if (lines.Count == 0 || Routes is not { } routes) return;
        var found = await routes.GetAsync(organizationId, lines.Select(l => l.FulfillmentRouteUuid).Distinct().ToList(), ct);
        foreach (var line in lines)
        {
            if (!found.TryGetValue(line.FulfillmentRouteUuid, out var route))
                throw new BadRequestException($"Fulfillment route {line.FulfillmentRouteUuid} does not exist.");
            if (!route.IsManufacture)
                throw new BadRequestException($"Route '{route.Code}' is not a make-to-order route, so no production order is made for it.");
        }
    }

    // ── Plan (D-17 step 3, PD-05) ─────────────────────────────────────────────────────────────────────────

    public async Task PlanDraftsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> productionOrderUuids, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(productionOrderUuids);
        var uuids = productionOrderUuids.Distinct().ToList();
        if (uuids.Count == 0) return;

        var drafts = await _db.ProductionOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && uuids.Contains(p.UUID) && p.Status == ProductionOrderStatus.Draft)
            .OrderBy(p => p.Id)
            .Select(p => new { p.UUID, p.ProductionNumber })
            .ToListAsync(ct);

        List<Exception>? failures = null;
        foreach (var draft in drafts)
        {
            try
            {
                // BR-C5-04 — the A30 planning path: BOM explosion, PMRs, allocation, supply requirements, notifications.
                await Orders.PlanAsync(draft.UUID, userId, ct);
            }
            catch (Exception ex)
            {
                if (ex is BadRequestException && await StatusAsync(organizationId, draft.UUID, ct) != ProductionOrderStatus.Draft)
                {
                    // Someone else planned or cancelled it meanwhile: nothing left to do here.
                    _log?.LogInformation(ex, "Production order {Number} was no longer a draft when its plan ran.", draft.ProductionNumber);
                }
                else
                {
                    _log?.LogWarning(ex, "Production order {Number} could not be planned.", draft.ProductionNumber);
                    (failures ??= []).Add(ex);
                }
            }
            finally
            {
                // The plan / cancel race (REV design note): a cancel can commit while this plan is between its saves, after
                // which the plan still registers demands and raises supply for a cancelled order. Re-check once the plan
                // is over and release whatever it left behind; a cancel that commits later sees all of it itself.
                await ReleaseLeftoversIfCancelledAsync(organizationId, draft.UUID, userId, ct);
            }
        }

        if (failures is { Count: > 0 })
            throw failures.Count == 1 ? failures[0] : new AggregateException("Some production orders could not be planned.", failures);
    }

    private Task<string?> StatusAsync(Guid organizationId, Guid uuid, CancellationToken ct) =>
        _db.ProductionOrders.IgnoreQueryFilters().AsNoTracking()
           .Where(p => p.OrganizationId == organizationId && p.UUID == uuid)
           .Select(p => p.Status).FirstOrDefaultAsync(ct);

    /// <summary>
    /// For a CANCELLED order only: cancels the PRODUCTION_MATERIAL demands still open on it (including any registered
    /// after the cancel read the requirements) and the live supply requirements of its requirements. Idempotent.
    /// </summary>
    private async Task ReleaseLeftoversIfCancelledAsync(Guid organizationId, Guid uuid, int userId, CancellationToken ct)
    {
        try
        {
            var po = await _db.ProductionOrders.IgnoreQueryFilters().AsNoTracking().Include(p => p.Materials)
                .FirstOrDefaultAsync(p => p.OrganizationId == organizationId && p.UUID == uuid, ct);
            if (po is null || po.Status != ProductionOrderStatus.Cancelled) return;

            var reason = $"Production order {po.ProductionNumber} cancelled.";
            foreach (var demand in await Engine.GetDemandsAsync(demandType: AllocationDemandType.ProductionMaterial, demandUuid: po.UUID, openOnly: true, ct: ct))
                await Engine.CancelDemandAsync(demand.Uuid, reason, userId, ct);

            var pmrUuids = po.Materials.Select(m => m.UUID).ToList();
            if (pmrUuids.Count == 0) return;
            var liveSupply = await _db.SupplyRequirements.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == organizationId && s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement &&
                            pmrUuids.Contains(s.DemandSourceUuid) && SupplyRequirementStatus.LiveStatuses.Contains(s.Status))
                .Select(s => s.UUID).ToListAsync(ct);
            foreach (var sr in liveSupply)
                await Supply.CancelAsync(sr, reason, userId, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Leftovers of cancelled production order {Uuid} could not all be released.", uuid);
        }
    }

    // ── Cancel cascade (D-22, PD-06) ──────────────────────────────────────────────────────────────────────

    public async Task<SaleOrderProductionCancellation> CancelForSaleOrderAsync(
        Guid organizationId, Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default)
    {
        var orders = await SaleOrderPos(organizationId, saleOrderUuid).AsNoTracking()
            .Where(p => p.Status != ProductionOrderStatus.Cancelled)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        if (orders.Count == 0) return new SaleOrderProductionCancellation([], []);

        var ids = orders.Select(p => p.Id).ToList();
        var issuedOrderIds = (await _db.ProductionMaterialRequirements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => ids.Contains(m.ProductionOrderId) && m.IssuedQuantity > 0)
                .Select(m => m.ProductionOrderId).ToListAsync(ct))
            .Concat(await _db.ProductionMaterialIssues.IgnoreQueryFilters().AsNoTracking()
                .Where(i => ids.Contains(i.ProductionOrderId) && i.Status == ProductionIssueStatus.Confirmed)
                .Select(i => i.ProductionOrderId).ToListAsync(ct))
            .ToHashSet();

        var why = string.IsNullOrWhiteSpace(reason) ? "Sale order cancelled." : reason.Trim();
        var cancelled = new List<SaleOrderProductionRef>();
        var running   = new List<SaleOrderProductionRef>();

        foreach (var po in orders)
        {
            var notStarted = po.Status is ProductionOrderStatus.Draft or ProductionOrderStatus.Planned
                                       or ProductionOrderStatus.MaterialPending or ProductionOrderStatus.Ready;
            if (!notStarted || issuedOrderIds.Contains(po.Id))
            {
                running.Add(ToRef(po, created: false));
                continue;
            }

            await Orders.CancelAsync(po.UUID, new CancelProductionOrderRequest
            {
                Reason = $"Sale order {po.SourceReference ?? saleOrderUuid.ToString()} cancelled: {why}"
            }, userId, ct);
            // Demands a concurrent plan registered but had not yet written onto the requirements (REV design note).
            await ReleaseLeftoversIfCancelledAsync(organizationId, po.UUID, userId, ct);

            var after = await _db.ProductionOrders.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == po.Id, ct);
            cancelled.Add(ToRef(after, created: false));
        }

        return new SaleOrderProductionCancellation(cancelled, running);
    }

    // ── Read (D-25) ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SaleOrderProductionRef>> GetForSaleOrderAsync(
        Guid organizationId, Guid saleOrderUuid, CancellationToken ct = default) =>
        (await SaleOrderPos(organizationId, saleOrderUuid).AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct))
            .Select(p => ToRef(p, created: false)).ToList();

    // ── Pieces ────────────────────────────────────────────────────────────────────────────────────────────

    internal static SaleOrderProductionRef ToRef(ProductionOrder p, bool created) => new(
        p.UUID, p.ProductionNumber, p.SourceLineUuid, p.Status, p.PlannedQuantity, p.AcceptedQuantity,
        p.FulfillmentRouteUuid, p.DeliveryOrderUuid, p.DeliveryNumber, created);

    private void EnqueueTimeline(ProductionOrder po, string eventType, int userId, string? notes) =>
        Jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            po.TraceId,
            new TimelineEvent(eventType, ManufacturingInterfaceCodes.ProductionOrder, po.UUID, po.ProductionNumber, DateTime.UtcNow, userId, notes),
            ManufacturingInterfaceCodes.ProductionOrder, po.ProductionNumber));
}
