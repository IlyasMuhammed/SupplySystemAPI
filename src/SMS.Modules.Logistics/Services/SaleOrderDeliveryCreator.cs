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
/// A33 PD-03 (BR-C4-01..05) — the DRAFT deliveries a confirmed sale order gets: one per route × ship-from warehouse
/// (C-4), each carrying its route's snapshot (D-10), its delivery mode taken from the route (D-4), and only the quantity
/// still outstanding on each line. Called by Demand once its confirm has committed, by Demand's D-12 sweep, and by the
/// recovery endpoint.
/// <list type="bullet">
/// <item><b>Idempotent.</b> Outstanding = quantity − fulfilled − on a delivery not yet issued (DRAFT included), so a
/// second call creates nothing more and reports every line as skipped.</item>
/// <item><b>Serialized with the order (REV-02).</b> Under <see cref="SaleOrderLock"/> in its own transaction, and only
/// then the order is re-read: an order a concurrent cancel has just committed gets nothing.</item>
/// <item><b>Explicit organization (R-12/R-13).</b> Runs from Hangfire with no user: every read filters on the
/// organization it is given, and the deliveries and their numbers are stamped with it.</item>
/// <item><b>D-8.</b> Route splitting is not partial fulfilment — the "allow partial fulfilment" setting is not
/// consulted: the deliveries of one call jointly cover every line.</item>
/// <item><b>All or nothing.</b> A failure throws and, inside the transaction, nothing is kept; <c>Skipped</c> only
/// ever means "this line needs no delivery, or cannot have one" — never an error a retry would fix.</item>
/// </list>
/// </summary>
internal sealed class SaleOrderDeliveryCreator : ISaleOrderDeliveryCreator
{
    private static readonly string[] DeliverableSoStatuses =
    [
        DemandDomain.EnumCode<DemandDomain.SaleOrderStatus>.Of(DemandDomain.SaleOrderStatus.Confirmed),
        DemandDomain.EnumCode<DemandDomain.SaleOrderStatus>.Of(DemandDomain.SaleOrderStatus.PartiallyFulfilled)
    ];

    private static readonly string CancelledLine =
        DemandDomain.EnumCode<DemandDomain.SaleOrderLineStatus>.Of(DemandDomain.SaleOrderLineStatus.Cancelled);

    private static readonly string DropShipLine =
        DemandDomain.EnumCode<DemandDomain.SaleOrderLineFulfillmentMode>.Of(DemandDomain.SaleOrderLineFulfillmentMode.DropShip);

    private readonly LogisticsDbContext       _db;
    private readonly DemandDbContext          _demand;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly IProductVariantResolver  _variants;
    private readonly IStockReservationService _reservations;
    private readonly IFulfillmentRouteLookup  _routes;
    private readonly ILogger                  _log;

    public SaleOrderDeliveryCreator(
        LogisticsDbContext db, DemandDbContext demand, IDocumentNumberGenerator numbers,
        IProductVariantResolver variants, IStockReservationService reservations, IFulfillmentRouteLookup routes,
        ILogger<SaleOrderDeliveryCreator>? log = null)
    {
        _db           = db;
        _demand       = demand;
        _numbers      = numbers;
        _variants     = variants;
        _reservations = reservations;
        _routes       = routes;
        _log          = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task<SaleOrderDeliveryCreationResult> CreateForConfirmedOrderAsync(
        Guid organizationId, Guid saleOrderUuid, IReadOnlyList<SaleOrderLineRoute> lineRoutes, int userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lineRoutes);
        if (organizationId == Guid.Empty)
            throw new BadRequestException("Deliveries can only be created for a sale order of a known organization.");

        return await SaleOrderLock.RunAsync(_db, saleOrderUuid,
            () => CreateLockedAsync(organizationId, saleOrderUuid, lineRoutes, userId, ct));
    }

    private sealed record Portion(DemandDomain.SaleOrderLine Line, int LineNumber, decimal Qty);

    private async Task<SaleOrderDeliveryCreationResult> CreateLockedAsync(
        Guid orgId, Guid soUuid, IReadOnlyList<SaleOrderLineRoute> lineRoutes, int userId, CancellationToken ct)
    {
        // Re-read under the lock, never tracked: Demand hands its own scoped context to this call, and nothing here
        // may write to it.
        var so = await _demand.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.UUID == soUuid && s.OrganizationId == orgId && !s.IsDeleted, ct)
            ?? throw new NotFoundException("SaleOrder", soUuid);

        if (!DeliverableSoStatuses.Contains(so.Status))
            throw new BadRequestException(
                $"Sale order {so.SoNumber} is {so.Status}, so no delivery can be created for it. Deliveries are " +
                $"created for an order that is {string.Join(" or ", DeliverableSoStatuses)}.");

        var lines   = so.Lines.OrderBy(l => l.Id).ToList();
        var skipped = new List<SkippedSaleOrderLine>();

        // Not filtered on the organization on purpose: the order (already proven to be orgId's) is identified by its
        // globally unique uuid, so this only counts — and a delivery mis-stamped under another tenant (R-14, a super
        // admin's manual create) must still count, or this would deliver the same units twice.
        var inFlight = await _db.DeliveryOrderLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.SoLineUuid != null
                     && !l.DeliveryOrder.IsDelete
                     && l.DeliveryOrder.SaleOrderUuid == soUuid
                     && SaleOrderDeliveryQuantities.BeforeIssue.Contains(l.DeliveryOrder.Status))
            .GroupBy(l => l.SoLineUuid!.Value)
            .Select(g => new { SoLineUuid = g.Key, Qty = g.Sum(x => x.QtyOrdered) })
            .ToDictionaryAsync(x => x.SoLineUuid, x => x.Qty, ct);

        var routes = await _routes.GetAsync(orgId, [.. lineRoutes.Select(r => r.RouteUuid).Distinct()], ct);

        // Where the order's stock is held, per line and warehouse — the pick has to happen where the hold is (C-4).
        var holds = ((await _reservations.GetBySourceAsync(ReservationSourceType.SalesOrder, soUuid, ct)) ?? [])
            .Where(h => h.Status == "ACTIVE" && h.SourceLineUuid is not null && h.ReservedQty > 0)
            .ToList();

        Address? shipTo = null;
        var shipToLooked = false;

        // Groups in first-seen order: route, then warehouse (null = nothing held yet; release chooses).
        var groups = new List<(FulfillmentRouteSummary Route, Guid? Warehouse, List<Portion> Portions)>();

        foreach (var lineRoute in lineRoutes.DistinctBy(r => r.SoLineUuid))
        {
            var index = lines.FindIndex(l => l.UUID == lineRoute.SoLineUuid);
            if (index < 0)
            {
                skipped.Add(new(lineRoute.SoLineUuid, $"It is not a line of sale order {so.SoNumber}."));
                continue;
            }

            var line  = lines[index];
            var label = $"Line {index + 1}";

            if (line.Status == CancelledLine)
            {
                skipped.Add(new(line.UUID, $"{label} is cancelled."));
                continue;
            }

            if (line.FulfillmentMode == DropShipLine)
            {
                skipped.Add(new(line.UUID, $"{label} is drop-shipped: the vendor delivers it straight to the customer."));
                continue;
            }

            if (!routes.TryGetValue(lineRoute.RouteUuid, out var route))
            {
                skipped.Add(new(line.UUID,
                    $"{label}'s fulfillment route is not one of this organization's routes, so no delivery was created " +
                    "for it. Use \"Create delivery\" to raise one by hand."));
                continue;
            }

            var outstanding = line.Quantity - line.FulfilledQty - inFlight.GetValueOrDefault(line.UUID);
            if (outstanding <= 0)
            {
                skipped.Add(new(line.UUID, $"{label} has nothing left to deliver: it is fulfilled or already on a delivery."));
                continue;
            }

            if (route.RequiresShipping)
            {
                if (!shipToLooked)
                {
                    shipToLooked = true;
                    shipTo = so.ShippingAddressId is { } addressUuid
                        ? await _db.Addresses.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(a => a.UUID == addressUuid && a.OrganizationId == orgId && !a.IsDelete, ct)
                        : null;
                }

                if (shipTo is null)
                {
                    skipped.Add(new(line.UUID,
                        $"{label} follows route {route.Code}, which ships, but the order has no shipping address. Add " +
                        "one to the order, then use \"Create deliveries\"."));
                    continue;
                }
            }

            foreach (var (warehouse, qty) in SplitByWarehouse(line.UUID, outstanding, holds))
            {
                var group = groups.FirstOrDefault(g => g.Route.Uuid == route.Uuid && g.Warehouse == warehouse);
                if (group.Portions is null)
                {
                    group = (route, warehouse, []);
                    groups.Add(group);
                }
                group.Portions.Add(new Portion(line, index + 1, qty));
            }
        }

        if (groups.Count == 0)
            return new SaleOrderDeliveryCreationResult([], skipped);

        var descriptions = await _variants.DescribeVariantsAsync(
            [.. groups.SelectMany(g => g.Portions).Select(p => p.Line.VariantUuid).Distinct()]);

        var now     = DateTime.UtcNow;
        var created = new List<(DeliveryOrder Delivery, FulfillmentRouteSummary Route)>();

        foreach (var (route, warehouse, portions) in groups)
        {
            var mode = route.RequiresShipping ? DeliveryMode.Ship : DeliveryMode.SelfPickup;

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
                ShipToAddressId       = mode == DeliveryMode.Ship ? shipTo!.Id : null,
                RequestedDate         = so.ExpectedDeliveryDate,
                Priority              = LogisticsCode.Of(DeliveryPriority.Normal),
                Status                = LogisticsCode.Of(DeliveryStatus.Draft),
                FulfillmentRouteUuid  = route.Uuid,
                FulfillmentRouteCode  = route.Code,
                RouteSteps            = FulfillmentStepCode.Format(route.Steps),
                IsActive              = true,
                CreatedBy             = userId,
                CreatedDate           = now
            };

            var lineNo = 1;
            foreach (var portion in portions)
            {
                descriptions.TryGetValue(portion.Line.VariantUuid, out var variant);
                delivery.Lines.Add(new DeliveryOrderLine
                {
                    UUID            = Guid.NewGuid(),
                    OrganizationId  = orgId,
                    LineNo          = lineNo++,
                    VariantUuid     = portion.Line.VariantUuid,
                    ProductUuid     = variant?.ProductUuid,
                    ItemDescription = DeliveryFromSourceRepository.DescribeLine(portion.Line.VariantUuid, variant),
                    UnitOfMeasure   = variant?.UomCode,
                    QtyOrdered      = portion.Qty,
                    SourceLineUuid  = portion.Line.UUID,
                    SoLineUuid      = portion.Line.UUID,
                    UnitValue       = portion.Line.UnitPrice,
                    CreatedBy       = userId,
                    CreatedDate     = now
                });
            }

            _db.DeliveryOrders.Add(delivery);
            created.Add((delivery, route));
        }

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "A33: created {Count} delivery(ies) for sale order {SoNumber} ({SoUuid}) in organization {OrgId}; {Skipped} line(s) skipped.",
            created.Count, so.SoNumber, so.UUID, orgId, skipped.Count);

        return new SaleOrderDeliveryCreationResult(
            [.. created.Select(c => new CreatedSaleOrderDelivery(
                c.Delivery.UUID, c.Delivery.DeliveryNumber, c.Route.Uuid, c.Route.Code, c.Delivery.DeliveryMode!,
                c.Delivery.ShipFromWarehouseUuid, c.Delivery.Lines.Count))],
            skipped);
    }

    /// <summary>
    /// How a line's outstanding quantity is shared between the warehouses holding it (C-4): each holding warehouse
    /// takes what it holds, largest first, and whatever nothing holds yet (a back-to-back balance) rides with the
    /// largest hold, as a manual delivery would. A line nothing holds at all has no warehouse: release chooses.
    /// </summary>
    private static IEnumerable<(Guid? Warehouse, decimal Qty)> SplitByWarehouse(
        Guid lineUuid, decimal outstanding, IReadOnlyList<ReservationSummary> holds)
    {
        var held = holds
            .Where(h => h.SourceLineUuid == lineUuid)
            .GroupBy(h => h.WarehouseUuid)
            .Select(g => (Warehouse: g.Key, Qty: g.Sum(h => h.ReservedQty)))
            .OrderByDescending(x => x.Qty)
            .ThenBy(x => x.Warehouse)
            .ToList();

        if (held.Count == 0)
        {
            yield return (null, outstanding);
            yield break;
        }

        var shares    = new List<(Guid? Warehouse, decimal Qty)>();
        var remaining = outstanding;
        foreach (var (warehouse, qty) in held)
        {
            if (remaining <= 0) break;
            var take = Math.Min(qty, remaining);
            shares.Add((warehouse, take));
            remaining -= take;
        }
        if (remaining > 0)
            shares[0] = (shares[0].Warehouse, shares[0].Qty + remaining);

        foreach (var share in shares) yield return share;
    }
}
