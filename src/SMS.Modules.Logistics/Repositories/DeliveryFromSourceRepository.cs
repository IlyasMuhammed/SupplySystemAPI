using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface IDeliveryFromSourceRepository
{
    Task<Guid> CreateFromSourceAsync(CreateDeliveryFromSourceRequest req, int createdBy);
}

/// <summary>
/// Creates a delivery from an existing source document.
/// <para>
/// T-11 implements the PO path — an inbound ASN, the supplier's advice that goods are on their
/// way. It is the highest-value integration in the plan: a delivered inbound delivery goes on to
/// pre-fill the GRN, closing the loop back into procurement.
/// </para>
/// <para>SRO, MIV and TRANSFER follow in T-12; SALE_ORDER (A29 §7.3) is the outbound customer delivery.</para>
/// </summary>
internal sealed class DeliveryFromSourceRepository : IDeliveryFromSourceRepository
{
    /// <summary>
    /// PO statuses that can still receive goods. DRAFT has not been committed to a supplier;
    /// RECEIVED, CLOSED and CANCELLED have nothing left to arrive.
    /// </summary>
    private static readonly string[] AdvisablePoStatuses = ["APPROVED", "SENT", "PARTIALLY_RECEIVED"];

    /// <summary>
    /// Sale order statuses with goods still to go out. A DRAFT has reserved nothing; FULFILLED
    /// and beyond have nothing left; CANCELLED never will.
    /// </summary>
    private static readonly string[] DeliverableSoStatuses =
    [
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderStatus>.Of(Demand.Domain.SaleOrderStatus.Confirmed),
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderStatus>.Of(Demand.Domain.SaleOrderStatus.PartiallyFulfilled)
    ];

    private static readonly string CancelledSoLine =
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderLineStatus>.Of(Demand.Domain.SaleOrderLineStatus.Cancelled);

    private static readonly string DropShipLine =
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderLineFulfillmentMode>.Of(Demand.Domain.SaleOrderLineFulfillmentMode.DropShip);

    private const string ActiveReservation = "ACTIVE";

    private readonly LogisticsDbContext       _db;
    private readonly DemandDbContext          _demand;
    private readonly WarehouseDbContext       _warehouse;
    private readonly MaterialDbContext        _material;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly IAddressNormalizer       _addresses;
    private readonly IProductVariantResolver  _variants;
    private readonly IStockReservationService _reservations;

    public DeliveryFromSourceRepository(
        LogisticsDbContext db,
        DemandDbContext demand,
        WarehouseDbContext warehouse,
        MaterialDbContext material,
        IDocumentNumberGenerator numbers,
        IAddressNormalizer addresses,
        IProductVariantResolver variants,
        IStockReservationService reservations)
    {
        _db           = db;
        _demand       = demand;
        _warehouse    = warehouse;
        _material     = material;
        _numbers      = numbers;
        _addresses    = addresses;
        _variants     = variants;
        _reservations = reservations;
    }

    public async Task<Guid> CreateFromSourceAsync(CreateDeliveryFromSourceRequest req, int createdBy)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!LogisticsCode.TryParse<DeliverySourceType>(req.SourceType, out var sourceType))
            throw new BadRequestException(
                $"'{req.SourceType}' is not a valid source type. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<DeliverySourceType>())}.");

        return sourceType switch
        {
            DeliverySourceType.Po        => await CreateFromPurchaseOrderAsync(req, createdBy),
            DeliverySourceType.Sro       => await CreateFromSupplierReturnAsync(req, createdBy),
            DeliverySourceType.Miv       => await CreateFromMaterialIssueAsync(req, createdBy),
            DeliverySourceType.SaleOrder => await CreateFromSaleOrderAsync(req, createdBy),

            // Neither has a source document to read lines from — the caller states the lines.
            DeliverySourceType.Manual or DeliverySourceType.Transfer => throw new BadRequestException(
                $"A {LogisticsCode.Of(sourceType)} delivery has no source document to copy lines from. " +
                "Use POST /api/logistics/deliveries and supply the lines directly."),

            _ => throw new BadRequestException(
                $"Creating a delivery from a {LogisticsCode.Of(sourceType)} is not supported.")
        };
    }

    // ── PO → inbound ASN ──────────────────────────────────────────────────────

    private async Task<Guid> CreateFromPurchaseOrderAsync(
        CreateDeliveryFromSourceRequest req, int createdBy)
    {
        var po = await _demand.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == req.SourceUuid && !p.IsDelete)
            ?? throw new NotFoundException("PurchaseOrder", req.SourceUuid);

        if (!AdvisablePoStatuses.Contains(po.Status))
            throw new BadRequestException(
                $"Purchase order {po.PoNumber} is {po.Status}, so nothing can be advised against it. " +
                $"Deliveries can only be raised for a PO that is {string.Join(", ", AdvisablePoStatuses)}.");

        var outstanding = await OutstandingByPoLineAsync(po.UUID, po.Lines);
        var selections  = ResolveSelections(req.Lines, po.Lines, outstanding);

        if (selections.Count == 0)
            throw new BadRequestException(
                $"Purchase order {po.PoNumber} has nothing left to advise — every line is already " +
                "received or already on a delivery.");

        var now = DateTime.UtcNow;

        var delivery = new DeliveryOrder
        {
            UUID           = Guid.NewGuid(),
            // Inherited, not generated: this is what lets a parcel trace back through
            // GRN → PO → RFQ → PR.
            TraceId        = po.TraceId,
            DeliveryNumber = await _numbers.NextAsync(DocumentNumberPrefix.Delivery, now),
            Direction      = LogisticsCode.Of(DeliveryDirection.Inbound),
            SourceType     = LogisticsCode.Of(DeliverySourceType.Po),
            SourceUuid     = po.UUID,
            SourceNumber   = po.PoNumber,
            // The PO's delivery warehouse is where the goods are heading, unless overridden.
            ShipToWarehouseUuid   = req.ShipToWarehouseUuid ?? po.DeliveryWarehouseId,
            ShipFromWarehouseUuid = req.ShipFromWarehouseUuid,
            RequestedDate  = req.RequestedDate ?? po.DeliveryDate,
            PromisedDate   = req.PromisedDate,
            Priority       = LogisticsCode.Of(ParsePriority(req.Priority)),
            Incoterm       = Trim(req.Incoterm)?.ToUpperInvariant(),
            Status         = LogisticsCode.Of(DeliveryStatus.Draft),
            Notes          = Trim(req.Notes),
            IsActive       = true,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        delivery.ShipFromAddress = await BuildAddressAsync(req.ShipFromAddress, createdBy, now);
        delivery.ShipToAddress   = await BuildAddressAsync(req.ShipToAddress, createdBy, now);

        var lineNo = 1;
        foreach (var (poLine, qty) in selections)
        {
            delivery.Lines.Add(new DeliveryOrderLine
            {
                UUID            = Guid.NewGuid(),
                LineNo          = lineNo++,
                VariantUuid     = poLine.VariantUuid,
                ItemDescription = poLine.ItemDescription,
                UnitOfMeasure   = poLine.UnitOfMeasure,
                QtyOrdered      = qty,
                SourceLineUuid  = poLine.UUID,
                UnitValue       = poLine.UnitPrice,
                CreatedBy       = createdBy,
                CreatedDate     = now
            });
        }

        _db.DeliveryOrders.Add(delivery);
        await _db.SaveChangesAsync();

        return delivery.UUID;
    }

    // ── SRO → outbound return to supplier ─────────────────────────────────────

    /// <summary>
    /// Turns an approved supplier return into a tracked outbound delivery, replacing the
    /// free-text <c>DispatchCarrier</c> / <c>DispatchTrackingRef</c> fields the SRO carries today.
    /// </summary>
    private async Task<Guid> CreateFromSupplierReturnAsync(
        CreateDeliveryFromSourceRequest req, int createdBy)
    {
        var sro = await _warehouse.SupplierReturnOrders
            .Include(s => s.Lines)
            // No IsDelete on this entity — supplier returns are not soft-deleted.
            .FirstOrDefaultAsync(s => s.UUID == req.SourceUuid)
            ?? throw new NotFoundException("SupplierReturnOrder", req.SourceUuid);

        // Only an approved return is ready to leave. A draft has not been agreed, and anything
        // already dispatched has its goods on the road.
        if (sro.Status != "APPROVED")
            throw new BadRequestException(
                $"Supplier return {sro.ReturnNumber} is {sro.Status}. A delivery can only be raised " +
                "for a return in APPROVED.");

        var lines = sro.Lines.Where(l => l.QtyToReturn > 0).OrderBy(l => l.LineNo).ToList();

        if (lines.Count == 0)
            throw new BadRequestException(
                $"Supplier return {sro.ReturnNumber} has no lines with a quantity to return.");

        // F8 — SRO lines are product-scoped while stock is variant-level. Resolve them the same
        // way SroRepository.DispatchAsync does, so the delivery and the dispatch cannot disagree
        // about which variant moved.
        var productUuids = lines.Where(l => l.ProductUuid.HasValue)
                                .Select(l => l.ProductUuid!.Value)
                                .Distinct()
                                .ToList();

        var variants = await _variants.ResolveDefaultVariantsAsync(productUuids);

        var now = DateTime.UtcNow;

        var delivery = new DeliveryOrder
        {
            UUID           = Guid.NewGuid(),
            TraceId        = Guid.NewGuid(),
            DeliveryNumber = await _numbers.NextAsync(DocumentNumberPrefix.Delivery, now),
            Direction      = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType     = LogisticsCode.Of(DeliverySourceType.Sro),
            SourceUuid     = sro.UUID,
            SourceNumber   = sro.ReturnNumber,
            // The return leaves the warehouse it was raised against.
            ShipFromWarehouseUuid = req.ShipFromWarehouseUuid ?? sro.WarehouseUuid,
            ShipToWarehouseUuid   = req.ShipToWarehouseUuid,
            RequestedDate  = req.RequestedDate,
            PromisedDate   = req.PromisedDate,
            Priority       = LogisticsCode.Of(ParsePriority(req.Priority)),
            Incoterm       = Trim(req.Incoterm)?.ToUpperInvariant(),
            Status         = LogisticsCode.Of(DeliveryStatus.Draft),
            Notes          = Trim(req.Notes),
            IsActive       = true,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        delivery.ShipFromAddress = await BuildAddressAsync(req.ShipFromAddress, createdBy, now);
        delivery.ShipToAddress   = await BuildAddressAsync(req.ShipToAddress, createdBy, now);

        var lineNo = 1;
        foreach (var line in lines)
        {
            Guid? variantUuid = null;

            if (line.ProductUuid is { } productUuid)
            {
                // Recorded explicitly when it resolves, and left explicitly null when it does
                // not — with the product still on the line, so nothing downstream has to guess.
                if (variants.TryGetValue(productUuid, out var variant))
                    variantUuid = variant.VariantUuid;
            }

            delivery.Lines.Add(new DeliveryOrderLine
            {
                UUID            = Guid.NewGuid(),
                LineNo          = lineNo++,
                ProductUuid     = line.ProductUuid,
                VariantUuid     = variantUuid,
                ItemDescription = line.ItemDescription,
                UnitOfMeasure   = line.UnitOfMeasure,
                QtyOrdered      = line.QtyToReturn,
                SourceLineUuid  = line.UUID,
                UnitValue       = line.UnitCost,
                CreatedBy       = createdBy,
                CreatedDate     = now
            });
        }

        _db.DeliveryOrders.Add(delivery);
        await _db.SaveChangesAsync();

        return delivery.UUID;
    }

    // ── MIV → outbound issue to a project site ────────────────────────────────

    /// <summary>
    /// Turns a posted material issue voucher into a tracked delivery to site, so the issue has
    /// real proof of receipt rather than only a status change.
    /// </summary>
    private async Task<Guid> CreateFromMaterialIssueAsync(
        CreateDeliveryFromSourceRequest req, int createdBy)
    {
        var miv = await _material.MaterialIssueVouchers
            .Include(m => m.Lines)
            .Include(m => m.MaterialIssueRequest)
            .FirstOrDefaultAsync(m => m.UUID == req.SourceUuid)
            ?? throw new NotFoundException("MaterialIssueVoucher", req.SourceUuid);

        // A draft voucher has not deducted stock yet, and a cancelled one never will.
        if (miv.Status != "POSTED")
            throw new BadRequestException(
                $"Material issue voucher {miv.IssueNo} is {miv.Status}. A delivery can only be raised " +
                "for a voucher that has been POSTED.");

        var lines = miv.Lines.Where(l => l.IssuedQty > 0).ToList();

        if (lines.Count == 0)
            throw new BadRequestException(
                $"Material issue voucher {miv.IssueNo} has no issued quantities.");

        var now = DateTime.UtcNow;

        var delivery = new DeliveryOrder
        {
            UUID           = Guid.NewGuid(),
            // Inherited from the originating request, so an issue to site traces back to the MIR.
            TraceId        = miv.MaterialIssueRequest?.TraceId ?? Guid.NewGuid(),
            DeliveryNumber = await _numbers.NextAsync(DocumentNumberPrefix.Delivery, now),
            Direction      = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType     = LogisticsCode.Of(DeliverySourceType.Miv),
            SourceUuid     = miv.UUID,
            SourceNumber   = miv.IssueNo,
            ShipFromWarehouseUuid = req.ShipFromWarehouseUuid,
            ShipToWarehouseUuid   = req.ShipToWarehouseUuid,
            RequestedDate  = req.RequestedDate ?? miv.IssueDate,
            PromisedDate   = req.PromisedDate,
            Priority       = LogisticsCode.Of(ParsePriority(req.Priority)),
            Incoterm       = Trim(req.Incoterm)?.ToUpperInvariant(),
            Status         = LogisticsCode.Of(DeliveryStatus.Draft),
            Notes          = Trim(req.Notes),
            IsActive       = true,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        delivery.ShipFromAddress = await BuildAddressAsync(req.ShipFromAddress, createdBy, now);
        delivery.ShipToAddress   = await BuildAddressAsync(req.ShipToAddress, createdBy, now);

        var lineNo = 1;
        foreach (var line in lines)
        {
            delivery.Lines.Add(new DeliveryOrderLine
            {
                UUID            = Guid.NewGuid(),
                LineNo          = lineNo++,
                VariantUuid     = line.VariantUuid,
                ItemDescription = line.ItemDescription,
                UnitOfMeasure   = line.UnitOfMeasure,
                QtyOrdered      = line.IssuedQty,
                SourceLineUuid  = line.UUID,
                UnitValue       = line.UnitCost,
                CreatedBy       = createdBy,
                CreatedDate     = now
            });
        }

        _db.DeliveryOrders.Add(delivery);
        await _db.SaveChangesAsync();

        return delivery.UUID;
    }

    // ── Sale order → outbound customer delivery (A29 §7.3) ────────────────────

    /// <summary>
    /// Turns a confirmed sale order — or the part of it the caller picks — into a delivery to the
    /// customer. Confirming the order only reserved the stock; this is the document that picks,
    /// packs and finally issues it, and one order may need several (§7.6).
    /// </summary>
    private async Task<Guid> CreateFromSaleOrderAsync(
        CreateDeliveryFromSourceRequest req, int createdBy)
    {
        var so = await _demand.SaleOrders
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.UUID == req.SourceUuid && !s.IsDeleted)
            ?? throw new NotFoundException("SaleOrder", req.SourceUuid);

        if (!DeliverableSoStatuses.Contains(so.Status))
            throw new BadRequestException(
                $"Sale order {so.SoNumber} is {so.Status}, so nothing can be delivered against it. " +
                $"Deliveries can only be raised for an order that is {string.Join(", ", DeliverableSoStatuses)}.");

        var mode = ResolveDeliveryMode(so, req.DeliveryMode);

        // A drop-ship line never touches this warehouse — the vendor sends it straight to the
        // customer (§4.3 scenario 4) — and a cancelled line has nothing to send.
        var deliverable = so.Lines
            .Where(l => l.Status != CancelledSoLine && l.FulfillmentMode != DropShipLine)
            .OrderBy(l => l.Id)
            .ToList();

        var outstanding = await OutstandingBySoLineAsync(so.UUID, deliverable);
        var selections  = ResolveSaleOrderSelections(req.Lines, deliverable, outstanding);

        if (selections.Count == 0)
            throw new BadRequestException(
                $"Sale order {so.SoNumber} has nothing left to deliver — every line is already " +
                "fulfilled or already on a delivery.");

        await EnsurePartialFulfilmentAllowedAsync(so, deliverable, selections);

        var descriptions = await _variants.DescribeVariantsAsync(
            selections.Select(s => s.Line.VariantUuid).Distinct().ToList());

        var now = DateTime.UtcNow;

        var delivery = new DeliveryOrder
        {
            UUID           = Guid.NewGuid(),
            // Inherited: one trace id spans SO → PO → GRN → reservation → this delivery → invoice.
            TraceId        = so.TraceId,
            DeliveryNumber = await _numbers.NextAsync(DocumentNumberPrefix.Delivery, now),
            Direction      = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType     = LogisticsCode.Of(DeliverySourceType.SaleOrder),
            SourceUuid     = so.UUID,
            SourceNumber   = so.SoNumber,
            SaleOrderUuid  = so.UUID,
            DeliveryMode   = LogisticsCode.Of(mode),
            ShipFromWarehouseUuid = req.ShipFromWarehouseUuid
                                    ?? await WarehouseHoldingAsync(so, selections.Select(s => s.Line.UUID)),
            ShipToWarehouseUuid   = req.ShipToWarehouseUuid,
            RequestedDate  = req.RequestedDate ?? so.ExpectedDeliveryDate,
            PromisedDate   = req.PromisedDate,
            Priority       = LogisticsCode.Of(ParsePriority(req.Priority)),
            Incoterm       = Trim(req.Incoterm)?.ToUpperInvariant(),
            Status         = LogisticsCode.Of(DeliveryStatus.Draft),
            Notes          = Trim(req.Notes),
            IsActive       = true,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        delivery.ShipFromAddress = await BuildAddressAsync(req.ShipFromAddress, createdBy, now);

        if (req.ShipToAddress is not null)
            delivery.ShipToAddress = await BuildAddressAsync(req.ShipToAddress, createdBy, now);
        else if (mode == DeliveryMode.Ship)
            // The order's own address row (§7.7) — referenced, not copied: address rows are never
            // edited in place, so the delivery keeps exactly what the order was taken against.
            delivery.ShipToAddressId = (await ShippingAddressOfAsync(so)).Id;

        var lineNo = 1;
        foreach (var (soLine, qty) in selections)
        {
            descriptions.TryGetValue(soLine.VariantUuid, out var variant);

            delivery.Lines.Add(new DeliveryOrderLine
            {
                UUID            = Guid.NewGuid(),
                LineNo          = lineNo++,
                VariantUuid     = soLine.VariantUuid,
                ProductUuid     = variant?.ProductUuid,
                ItemDescription = DescribeLine(soLine.VariantUuid, variant),
                UnitOfMeasure   = variant?.UomCode,
                QtyOrdered      = qty,
                SourceLineUuid  = soLine.UUID,
                SoLineUuid      = soLine.UUID,
                UnitValue       = soLine.UnitPrice,
                CreatedBy       = createdBy,
                CreatedDate     = now
            });
        }

        _db.DeliveryOrders.Add(delivery);
        await _db.SaveChangesAsync();

        return delivery.UUID;
    }

    /// <summary>
    /// §7.6 — one order into many deliveries is what the organization's "allow partial fulfilment"
    /// setting permits. With it off, a delivery has to carry every line in full for everything still
    /// owed: the order goes out in one piece, or waits until it can.
    /// </summary>
    private async Task EnsurePartialFulfilmentAllowedAsync(
        Demand.Domain.SaleOrder so,
        List<Demand.Domain.SaleOrderLine> deliverable,
        List<(Demand.Domain.SaleOrderLine Line, decimal Qty)> selections)
    {
        var config = await _demand.SaleOrderConfigs.AsNoTracking().FirstOrDefaultAsync();
        if (config is null || config.PartialFulfillmentAllowed) return;

        var chosen = selections.ToDictionary(s => s.Line.UUID, s => s.Qty);

        var notCovered = deliverable
            .Select((line, index) => (Label: $"Line {index + 1}", Owed: line.Quantity - line.FulfilledQty, line.UUID))
            .Where(x => x.Owed > 0 && chosen.GetValueOrDefault(x.UUID) < x.Owed)
            .Select(x => x.Label)
            .ToList();

        if (notCovered.Count > 0)
            throw new BadRequestException(
                $"Partial fulfilment is switched off for this organization, so sale order {so.SoNumber} has to go " +
                $"out in one delivery that covers everything still owed. This delivery does not cover " +
                $"{string.Join(", ", notCovered)} in full.");
    }

    private static DeliveryMode ResolveDeliveryMode(Demand.Domain.SaleOrder so, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return LogisticsCode.TryParse<DeliveryMode>(so.DeliveryMode, out var own)
                ? own
                : throw new ConflictException(
                    $"Sale order {so.SoNumber} has delivery mode '{so.DeliveryMode}', which this module " +
                    "does not recognise. Correct the order before raising a delivery.");

        return LogisticsCode.TryParse<DeliveryMode>(requested, out var mode)
            ? mode
            : throw new BadRequestException(
                $"'{requested}' is not a valid delivery mode. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<DeliveryMode>())}.");
    }

    private async Task<Address> ShippingAddressOfAsync(Demand.Domain.SaleOrder so)
    {
        var address = so.ShippingAddressId is { } uuid
            ? await _db.Addresses.FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete)
            : null;

        return address ?? throw new BadRequestException(
            $"Sale order {so.SoNumber} is to be shipped but has no shipping address on file. " +
            "Add one to the order, or supply ShipToAddress on this request.");
    }

    /// <summary>
    /// The warehouse the order's stock is held in, so the delivery picks where the reservation
    /// actually is. Null when none of the chosen lines is reserved yet (a back-to-back line still
    /// waiting on its PO), in which case release will choose.
    /// </summary>
    private async Task<Guid?> WarehouseHoldingAsync(Demand.Domain.SaleOrder so, IEnumerable<Guid> soLineUuids)
    {
        var wanted = soLineUuids.ToHashSet();
        var holds  = await _reservations.GetBySourceAsync(ReservationSourceType.SalesOrder, so.UUID);

        var warehouses = holds
            .Where(h => h.Status == ActiveReservation && h.SourceLineUuid is { } line && wanted.Contains(line))
            .Select(h => h.WarehouseUuid)
            .Distinct()
            .ToList();

        return warehouses.Count switch
        {
            0 => null,
            1 => warehouses[0],
            _ => throw new BadRequestException(
                $"The chosen lines of sale order {so.SoNumber} are reserved in {warehouses.Count} different " +
                "warehouses, and one delivery ships from one. Either state ShipFromWarehouseUuid or " +
                "select only the lines held in a single warehouse.")
        };
    }

    private static string DescribeLine(Guid variantUuid, VariantDescription? variant)
    {
        var description = variant?.DisplayName ?? $"Variant {variantUuid}";
        return description.Length > 300 ? description[..300] : description;
    }

    /// <summary>
    /// How much of each sale order line may still be put on a delivery:
    /// <c>Quantity − FulfilledQty − in flight</c>, the same shape as the PO rule below.
    /// <para>
    /// <c>FulfilledQty</c> grows at goods issue (§7.5), so a delivery counts as in flight only
    /// until then: DRAFT through STAGED, PENDING_APPROVAL and ON_HOLD. Once issued, its units are
    /// in <c>FulfilledQty</c> and counting them again would block the balance; cancelled and
    /// short-closed deliveries are not going, so their quantity returns to the pool.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> OutstandingBySoLineAsync(
        Guid saleOrderUuid, IEnumerable<Demand.Domain.SaleOrderLine> soLines)
    {
        string[] beforeIssue =
        [
            LogisticsCode.Of(DeliveryStatus.Draft),
            LogisticsCode.Of(DeliveryStatus.Released),
            LogisticsCode.Of(DeliveryStatus.Picking),
            LogisticsCode.Of(DeliveryStatus.Picked),
            LogisticsCode.Of(DeliveryStatus.Packed),
            LogisticsCode.Of(DeliveryStatus.Staged),
            LogisticsCode.Of(DeliveryStatus.PendingApproval),
            LogisticsCode.Of(DeliveryStatus.OnHold)
        ];

        var inFlight = await _db.DeliveryOrderLines
            .Where(l => l.SoLineUuid != null
                     && !l.DeliveryOrder.IsDelete
                     && l.DeliveryOrder.SaleOrderUuid == saleOrderUuid
                     && beforeIssue.Contains(l.DeliveryOrder.Status))
            .GroupBy(l => l.SoLineUuid!.Value)
            .Select(g => new { SoLineUuid = g.Key, Qty = g.Sum(x => x.QtyOrdered) })
            .ToDictionaryAsync(x => x.SoLineUuid, x => x.Qty);

        return soLines.ToDictionary(
            line => line.UUID,
            line => Math.Max(0m, line.Quantity - line.FulfilledQty - inFlight.GetValueOrDefault(line.UUID)));
    }

    private static List<(Demand.Domain.SaleOrderLine Line, decimal Qty)> ResolveSaleOrderSelections(
        List<SourceLineSelection>? requested,
        List<Demand.Domain.SaleOrderLine> soLines,
        Dictionary<Guid, decimal> outstanding)
    {
        if (requested is null || requested.Count == 0)
            return [.. soLines
                .Where(l => outstanding.GetValueOrDefault(l.UUID) > 0)
                .Select(l => (l, outstanding[l.UUID]))];

        var selections = new List<(Demand.Domain.SaleOrderLine, decimal)>();

        foreach (var selection in requested)
        {
            var soLine = soLines.FirstOrDefault(l => l.UUID == selection.SourceLineUuid)
                ?? throw new BadRequestException(
                    $"Line {selection.SourceLineUuid} does not belong to this sale order, or cannot be " +
                    "delivered from here (cancelled, or shipped by the vendor directly).");

            // Sale order lines carry no line number; their position in the order is the label.
            var label     = $"Line {soLines.IndexOf(soLine) + 1} ({soLine.VariantUuid})";
            var available = outstanding.GetValueOrDefault(soLine.UUID);

            if (available <= 0)
                throw new BadRequestException(
                    $"{label} has nothing left to deliver — it is already fulfilled or already on " +
                    "another delivery.");

            var qty = selection.Qty ?? available;

            if (qty <= 0)
                throw new BadRequestException($"{label}: quantity must be greater than zero.");

            if (qty > available)
                throw new BadRequestException(
                    $"{label}: only {available:0.###} is left to deliver, but {qty:0.###} was requested.");

            selections.Add((soLine, qty));
        }

        return selections;
    }

    /// <summary>
    /// How much of each PO line may still be advised.
    /// <para>
    /// <b>The problem this solves.</b> A purchase order line records <c>Quantity</c> and
    /// <c>QtyReceived</c> — cumulative across GRNs — and nothing else. There is no notion
    /// anywhere in the system of a quantity that has been <em>advised but not yet received</em>.
    /// Left alone, two ASNs raised against one PO would each see the full outstanding balance and
    /// each claim all of it, and the warehouse would expect twice the goods that are coming.
    /// </para>
    /// <para>
    /// <b>The rule.</b> Outstanding = <c>Quantity − QtyReceived − AdvisedInFlight</c>, where
    /// <c>AdvisedInFlight</c> counts delivery lines on ASNs that have <em>not yet arrived</em>.
    /// </para>
    /// <para>
    /// The "in flight" qualifier is what keeps the two terms from overlapping. An ASN that has
    /// been delivered has had its goods booked in by a GRN, so those units are already inside
    /// <c>QtyReceived</c>; counting them again would understate what is left and wrongly block a
    /// legitimate second delivery. An ASN still in transit is not in <c>QtyReceived</c> yet, so
    /// it has to be subtracted separately or two ASNs would each claim the same balance.
    /// Cancelled and short-closed deliveries drop out entirely — their goods are not coming, so
    /// the quantity returns to the pool.
    /// </para>
    /// <para>
    /// <b>Known gap.</b> Between an ASN being marked delivered and its GRN being posted, its
    /// quantity is in neither term, so the line briefly looks more available than it is. The
    /// window is short and the consequence is a duplicate advice the GRN will reconcile, which is
    /// far milder than permanently blocking legitimate deliveries. It closes properly when a
    /// delivered ASN pre-fills its GRN and the two are linked.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> OutstandingByPoLineAsync(
        Guid poUuid, IEnumerable<Demand.Domain.PurchaseOrderLine> poLines)
    {
        var poCode = LogisticsCode.Of(DeliverySourceType.Po);

        // Statuses in which an advice no longer represents goods that are still on their way:
        // either they have arrived (and are therefore in QtyReceived) or they never will.
        string[] settled =
        [
            LogisticsCode.Of(DeliveryStatus.Delivered),
            LogisticsCode.Of(DeliveryStatus.Closed),
            LogisticsCode.Of(DeliveryStatus.Cancelled),
            LogisticsCode.Of(DeliveryStatus.ShortClosed)
        ];

        var inFlight = await _db.DeliveryOrderLines
            .Where(l => l.SourceLineUuid != null
                     && !l.DeliveryOrder.IsDelete
                     && l.DeliveryOrder.SourceType == poCode
                     && l.DeliveryOrder.SourceUuid == poUuid
                     && !settled.Contains(l.DeliveryOrder.Status))
            .GroupBy(l => l.SourceLineUuid!.Value)
            .Select(g => new { SourceLineUuid = g.Key, Qty = g.Sum(x => x.QtyOrdered) })
            .ToDictionaryAsync(x => x.SourceLineUuid, x => x.Qty);

        return poLines.ToDictionary(
            line => line.UUID,
            line => Math.Max(0m, line.Quantity - line.QtyReceived - inFlight.GetValueOrDefault(line.UUID)));
    }

    private static List<(Demand.Domain.PurchaseOrderLine Line, decimal Qty)> ResolveSelections(
        List<SourceLineSelection>? requested,
        ICollection<Demand.Domain.PurchaseOrderLine> poLines,
        Dictionary<Guid, decimal> outstanding)
    {
        // Nothing asked for: advise everything that is still outstanding.
        if (requested is null || requested.Count == 0)
            return [.. poLines
                .Where(l => outstanding.GetValueOrDefault(l.UUID) > 0)
                .OrderBy(l => l.LineNo)
                .Select(l => (l, outstanding[l.UUID]))];

        var selections = new List<(Demand.Domain.PurchaseOrderLine, decimal)>();

        foreach (var selection in requested)
        {
            var poLine = poLines.FirstOrDefault(l => l.UUID == selection.SourceLineUuid)
                ?? throw new BadRequestException(
                    $"Line {selection.SourceLineUuid} does not belong to this purchase order.");

            var available = outstanding.GetValueOrDefault(poLine.UUID);

            if (available <= 0)
                throw new BadRequestException(
                    $"Line {poLine.LineNo} ({poLine.ItemDescription}) has nothing left to advise — " +
                    "it is already received or already on another delivery.");

            var qty = selection.Qty ?? available;

            if (qty <= 0)
                throw new BadRequestException(
                    $"Line {poLine.LineNo}: quantity must be greater than zero.");

            if (qty > available)
                throw new BadRequestException(
                    $"Line {poLine.LineNo}: only {available:0.###} of " +
                    $"{poLine.ItemDescription} is left to advise, but {qty:0.###} was requested.");

            selections.Add((poLine, qty));
        }

        return selections;
    }

    // ── Shared with DeliveryRepository ────────────────────────────────────────

    private async Task<Address?> BuildAddressAsync(AddressRequest? req, int createdBy, DateTime now)
    {
        if (req is null) return null;

        var address = new Address
        {
            UUID           = Guid.NewGuid(),
            Line1          = req.Line1,
            Line2          = req.Line2,
            CityId         = req.CityId,
            CityName       = req.CityName,
            State          = req.State,
            PostalCode     = req.PostalCode,
            CountryName    = req.CountryName,
            CountryIsoCode = req.CountryIsoCode,
            ContactName    = req.ContactName,
            ContactPhone   = req.ContactPhone,
            ContactEmail   = req.ContactEmail,
            Latitude       = req.Latitude,
            Longitude      = req.Longitude,
            ConsigneeUuid  = req.ConsigneeUuid,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        await _addresses.NormalizeAsync(address);
        return address;
    }

    private static DeliveryPriority ParsePriority(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DeliveryPriority.Normal;

        return LogisticsCode.TryParse<DeliveryPriority>(code, out var priority)
            ? priority
            : throw new BadRequestException(
                $"'{code}' is not a valid priority. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<DeliveryPriority>())}.");
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
