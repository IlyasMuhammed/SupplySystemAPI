using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using DemandDomain = SMS.Modules.Demand.Domain;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A34 PE-04 (D-20, D-21, D-29; docs/route-classification/API-CONTRACT.md §8.4) — the DRAFT delivery a make-to-order
/// production order gets once it completes, or early through "Create delivery now". Called by Material's FGR hook, its
/// sweep and the button, always with no transaction or lock held.
/// <list type="bullet">
/// <item><b>Serialized with the order.</b> Under <see cref="SaleOrderLock"/> (the order's own application lock, the
/// one Demand's confirm / cancel / production creation take) in its own transaction; only then is the order re-read,
/// so a cancel that commits first leaves nothing behind.</item>
/// <item><b>Idempotent (D-20).</b> Quantity = min(accepted − already on non-cancelled deliveries made from this PO,
/// the line's outstanding = quantity − fulfilled − on deliveries not yet issued). A replay creates 0 and still names the
/// latest delivery made from the PO.</item>
/// <item><b>Snapshot (A33 D-10).</b> Mode and steps come from the PO's route, read active or not; a route that no longer
/// exists gives the legacy full path (no route) with the order's header mode.</item>
/// <item><b>Ship-from (D-29).</b> The warehouses of the line's SALES_ORDER holds, split as A33 does; else the PO's
/// output / production warehouse.</item>
/// <item><b>Results, not exceptions (REV-05).</b> Every business outcome is a result with a reason; only an
/// unexpected failure throws (and keeps the PO pending for Material's sweep).</item>
/// <item><b>D-21.</b> The partial-fulfilment setting is not consulted: yield is not a choice to ship early.</item>
/// </list>
/// </summary>
internal sealed class ProductionDeliveryCreator : IProductionDeliveryCreator
{
    private static readonly string[] DeliverableSoStatuses =
    [
        DemandDomain.EnumCode<DemandDomain.SaleOrderStatus>.Of(DemandDomain.SaleOrderStatus.Confirmed),
        DemandDomain.EnumCode<DemandDomain.SaleOrderStatus>.Of(DemandDomain.SaleOrderStatus.PartiallyFulfilled)
    ];

    private static readonly string CancelledLine =
        DemandDomain.EnumCode<DemandDomain.SaleOrderLineStatus>.Of(DemandDomain.SaleOrderLineStatus.Cancelled);

    private static readonly string CancelledDelivery = LogisticsCode.Of(DeliveryStatus.Cancelled);

    private readonly LogisticsDbContext       _db;
    private readonly DemandDbContext          _demand;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly IProductVariantResolver  _variants;
    private readonly IStockReservationService _reservations;
    private readonly IFulfillmentRouteLookup  _routes;
    private readonly ILogger                  _log;

    public ProductionDeliveryCreator(
        LogisticsDbContext db, DemandDbContext demand, IDocumentNumberGenerator numbers,
        IProductVariantResolver variants, IStockReservationService reservations, IFulfillmentRouteLookup routes,
        ILogger<ProductionDeliveryCreator>? log = null)
    {
        _db           = db;
        _demand       = demand;
        _numbers      = numbers;
        _variants     = variants;
        _reservations = reservations;
        _routes       = routes;
        _log          = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task<ProductionDeliveryResult> CreateForProductionOrderAsync(
        Guid organizationId, ProductionDeliveryRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (organizationId == Guid.Empty)
            throw new BadRequestException("A delivery from production can only be created for a known organization.");

        if (request.AcceptedQuantity <= 0)
            return Skip($"{request.ProductionNumber} has no accepted quantity yet, so there is nothing to deliver.");

        return await SaleOrderLock.RunAsync(_db, request.SaleOrderUuid,
            () => CreateLockedAsync(organizationId, request, userId, ct));
    }

    private static ProductionDeliveryResult Skip(string reason, SaleOrderDeliveryRef? latest = null) =>
        new(null, 0m, reason, latest, []);

    private async Task<ProductionDeliveryResult> CreateLockedAsync(
        Guid orgId, ProductionDeliveryRequest req, int userId, CancellationToken ct)
    {
        // What this PO already put on deliveries. Not filtered on the organization on purpose (as the A33 creator): the
        // PO uuid is globally unique, and a delivery mis-stamped under another tenant must still count.
        var fromPo = await _db.DeliveryOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.ProductionOrderUuid == req.ProductionOrderUuid && !d.IsDelete && d.Status != CancelledDelivery)
            .Select(d => new
            {
                d.UUID, d.DeliveryNumber, d.Status, d.CreatedDate, d.Id,
                Qty = d.Lines.Sum(l => (decimal?)l.QtyOrdered) ?? 0m
            })
            .ToListAsync(ct);
        var alreadyFromPo = fromPo.Sum(d => d.Qty);
        var latest = fromPo.OrderByDescending(d => d.CreatedDate).ThenByDescending(d => d.Id)
            .Select(d => new SaleOrderDeliveryRef(d.UUID, d.DeliveryNumber, d.Status)).FirstOrDefault();

        // Re-read under the lock, never tracked: nothing here writes to Demand.
        var so = await _demand.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.UUID == req.SaleOrderUuid && s.OrganizationId == orgId && !s.IsDeleted, ct);
        if (so is null)
            return Skip($"The sale order of {req.ProductionNumber} was not found in this organization, so no delivery was created.", latest);

        if (!DeliverableSoStatuses.Contains(so.Status))
            return Skip($"Sale order {so.SoNumber} is {so.Status}: no delivery was created from {req.ProductionNumber}.", latest);

        var lines = so.Lines.OrderBy(l => l.Id).ToList();
        var index = lines.FindIndex(l => l.UUID == req.SoLineUuid);
        if (index < 0)
            return Skip($"The line {req.ProductionNumber} was made for is not a line of sale order {so.SoNumber}.", latest);

        var line  = lines[index];
        var label = $"Line {index + 1}";
        if (line.Status == CancelledLine)
            return Skip($"{label} of sale order {so.SoNumber} is cancelled: no delivery was created from {req.ProductionNumber}.", latest);

        var inFlight = await _db.DeliveryOrderLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.SoLineUuid == line.UUID
                     && !l.DeliveryOrder.IsDelete
                     && l.DeliveryOrder.SaleOrderUuid == so.UUID
                     && SaleOrderDeliveryQuantities.BeforeIssue.Contains(l.DeliveryOrder.Status))
            .SumAsync(l => (decimal?)l.QtyOrdered, ct) ?? 0m;

        var available   = req.AcceptedQuantity - alreadyFromPo;
        var outstanding = line.Quantity - line.FulfilledQty - inFlight;
        var qty         = Math.Min(available, outstanding);
        if (qty <= 0)
            return Skip(
                $"Nothing left to deliver from {req.ProductionNumber}: it accepted {req.AcceptedQuantity:0.####}, " +
                $"{alreadyFromPo:0.####} is already on deliveries made from it, and {label.ToLowerInvariant()} of " +
                $"{so.SoNumber} has {Math.Max(outstanding, 0m):0.####} outstanding.", latest);

        // The PO's route, active or not (D-10 snapshot); gone altogether → the legacy full path with the header's mode.
        var route = (await _routes.GetAsync(orgId, [req.RouteUuid], ct)).GetValueOrDefault(req.RouteUuid);
        var mode  = route is not null
            ? (route.RequiresShipping ? DeliveryMode.Ship : DeliveryMode.SelfPickup)
            : (string.Equals(so.DeliveryMode, LogisticsCode.Of(DeliveryMode.SelfPickup), StringComparison.OrdinalIgnoreCase)
                ? DeliveryMode.SelfPickup : DeliveryMode.Ship);

        Address? shipTo = null;
        if (mode == DeliveryMode.Ship)
        {
            shipTo = so.ShippingAddressId is { } addressUuid
                ? await _db.Addresses.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.UUID == addressUuid && a.OrganizationId == orgId && !a.IsDelete, ct)
                : null;
            if (shipTo is null)
                return Skip(
                    $"{label} of {so.SoNumber} ships, but the order has no shipping address. Add one to the order, then " +
                    $"use \"Create delivery now\" on {req.ProductionNumber}.", latest);
        }

        // Where the line's goods are held (D-29): FGR's allocation moves the finished goods onto the line as a
        // SALES_ORDER hold. Nothing held → the PO's own warehouse.
        var holds = ((await _reservations.GetBySourceAsync(ReservationSourceType.SalesOrder, so.UUID, ct)) ?? [])
            .Where(h => h.Status == "ACTIVE" && h.SourceLineUuid == line.UUID && h.ReservedQty > 0)
            .ToList();
        Guid? fallback = req.FallbackWarehouseUuid == Guid.Empty ? null : req.FallbackWarehouseUuid;
        var shares = SaleOrderDeliveryCreator.SplitByWarehouse(line.UUID, qty, holds)
            .Select(s => (Warehouse: s.Warehouse ?? fallback, s.Qty))
            .ToList();

        var descriptions = await _variants.DescribeVariantsAsync([line.VariantUuid]);
        descriptions.TryGetValue(line.VariantUuid, out var variant);

        var now     = DateTime.UtcNow;
        var created = new List<DeliveryOrder>();
        foreach (var (warehouse, shareQty) in shares)
        {
            var delivery = new DeliveryOrder
            {
                UUID                  = Guid.NewGuid(),
                OrganizationId        = orgId,
                TraceId               = so.TraceId,
                DeliveryNumber        = await _numbers.NextAsync(DocumentNumberPrefix.Delivery, now, orgId, ct),
                Direction             = LogisticsCode.Of(DeliveryDirection.Outbound),
                SourceType            = LogisticsCode.Of(DeliverySourceType.SaleOrder),
                SourceUuid            = so.UUID,
                SourceNumber          = so.SoNumber,
                SaleOrderUuid         = so.UUID,
                DeliveryMode          = LogisticsCode.Of(mode),
                ShipFromWarehouseUuid = warehouse,
                ShipToAddressId       = shipTo?.Id,
                RequestedDate         = line.ManualDeliveryDate ?? line.CalculatedDeliveryDate ?? so.ExpectedDeliveryDate,
                Priority              = LogisticsCode.Of(DeliveryPriority.Normal),
                Status                = LogisticsCode.Of(DeliveryStatus.Draft),
                FulfillmentRouteUuid  = route?.Uuid,
                FulfillmentRouteCode  = route?.Code,
                RouteSteps            = route is null ? null : FulfillmentStepCode.Format(route.Steps),
                ProductionOrderUuid   = req.ProductionOrderUuid,
                Notes                 = $"From production order {req.ProductionNumber}.",
                IsActive              = true,
                CreatedBy             = userId,
                CreatedDate           = now
            };
            delivery.Lines.Add(new DeliveryOrderLine
            {
                UUID            = Guid.NewGuid(),
                OrganizationId  = orgId,
                LineNo          = 1,
                VariantUuid     = line.VariantUuid,
                ProductUuid     = variant?.ProductUuid,
                ItemDescription = DeliveryFromSourceRepository.DescribeLine(line.VariantUuid, variant),
                UnitOfMeasure   = variant?.UomCode,
                QtyOrdered      = shareQty,
                SourceLineUuid  = line.UUID,
                SoLineUuid      = line.UUID,
                UnitValue       = line.UnitPrice,
                CreatedBy       = userId,
                CreatedDate     = now
            });

            _db.DeliveryOrders.Add(delivery);
            created.Add(delivery);
        }

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "A34: created {Count} delivery(ies) for {Qty} from production order {ProductionNumber} ({PoUuid}), sale order {SoNumber}, organization {OrgId}.",
            created.Count, qty, req.ProductionNumber, req.ProductionOrderUuid, so.SoNumber, orgId);

        var refs = created.Select(d => new CreatedSaleOrderDelivery(
            d.UUID, d.DeliveryNumber, route?.Uuid ?? Guid.Empty, route?.Code ?? string.Empty, d.DeliveryMode!,
            d.ShipFromWarehouseUuid, d.Lines.Count)).ToList();
        var last = created[^1];

        return new ProductionDeliveryResult(
            refs[0],
            qty,
            qty < available
                ? $"Only {qty:0.####} of the {available:0.####} accepted went on a delivery: {label.ToLowerInvariant()} of " +
                  $"{so.SoNumber} has {qty:0.####} outstanding."
                : null,
            new SaleOrderDeliveryRef(last.UUID, last.DeliveryNumber, last.Status),
            refs);
    }
}
