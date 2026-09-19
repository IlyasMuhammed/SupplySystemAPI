using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface IDeliveryReleaseRepository
{
    Task<bool> ReleaseAsync(Guid uuid, ReleaseDeliveryRequest? req, int userId);

    /// <summary>What this delivery could be released against right now, line by line.</summary>
    Task<DeliveryAvailabilityModel?> GetAvailabilityAsync(Guid uuid);
}

/// <summary>
/// Release — the transition from a planned delivery to a committed one.
/// <para>
/// This is the first consequential step in a delivery's life: it <b>hard-reserves</b> the stock,
/// so two deliveries cannot promise the same units. Everything before it is paperwork.
/// </para>
/// <para>
/// Reservation goes through <see cref="IStockReservationService"/>, which owns both the
/// reservation rows and <c>InventoryItem.QtyReserved</c>. Logistics therefore needs no reference
/// to the Inventory module at all — it asks for stock to be held and is told whether it was.
/// </para>
/// </summary>
internal sealed class DeliveryReleaseRepository : IDeliveryReleaseRepository
{
    private readonly LogisticsDbContext       _db;
    private readonly IStockReservationService _reservations;
    private readonly IDocumentNumberGenerator _numbers;

    public DeliveryReleaseRepository(
        LogisticsDbContext db,
        IStockReservationService reservations,
        IDocumentNumberGenerator numbers)
    {
        _db           = db;
        _reservations = reservations;
        _numbers      = numbers;
    }

    public async Task<bool> ReleaseAsync(Guid uuid, ReleaseDeliveryRequest? req, int userId)
    {
        var delivery = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

        if (delivery is null) return false;

        var current = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);
        DeliveryStateMachine.Instance.EnsureCanTransition(current, DeliveryStatus.Released);

        if (delivery.LinesUnknown)
            throw new ConflictException(
                "This delivery was migrated without line detail, so there is nothing to reserve " +
                "or pick. Raise a new delivery against the source document instead.");

        if (delivery.Lines.Count == 0)
            throw new ConflictException("A delivery with no lines cannot be released.");

        if (RequiresStockHold(delivery))
        {
            var onShortage = (req?.OnShortage ?? ShortageAction.Block).Trim().ToUpperInvariant();

            if (onShortage is not (ShortageAction.Block or ShortageAction.Split))
                throw new BadRequestException(
                    $"'{onShortage}' is not a valid shortage action. Valid values: " +
                    $"{ShortageAction.Block}, {ShortageAction.Split}.");

            if (onShortage == ShortageAction.Split)
                await SplitOutShortfallAsync(delivery, userId);

            await ReserveStockAsync(delivery, userId);
        }

        delivery.Status       = LogisticsCode.Of(DeliveryStatus.Released);
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Whether releasing this delivery should hold stock.
    /// <para>
    /// Only stock that is <em>leaving</em> can be over-promised. An inbound delivery is an advice
    /// that goods are arriving — there is nothing on hand to reserve, and holding stock for it
    /// would reduce availability for no reason.
    /// </para>
    /// </summary>
    private static bool RequiresStockHold(DeliveryOrder delivery) =>
        LogisticsCode.Parse<DeliveryDirection>(delivery.Direction) != DeliveryDirection.Inbound;

    // ── Availability ──────────────────────────────────────────────────────────

    public async Task<DeliveryAvailabilityModel?> GetAvailabilityAsync(Guid uuid)
    {
        var delivery = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

        if (delivery is null) return null;

        var model = new DeliveryAvailabilityModel
        {
            DeliveryUuid   = delivery.UUID,
            DeliveryNumber = delivery.DeliveryNumber,
            Status         = delivery.Status,
            RequiresStock  = RequiresStockHold(delivery)
        };

        if (!model.RequiresStock)
        {
            // An inbound delivery holds nothing, so there is nothing to be short of.
            model.CanReleaseInFull = true;
            return model;
        }

        var coverage = await CoverageAsync(delivery);

        foreach (var line in delivery.Lines.OrderBy(l => l.LineNo))
        {
            var cover = coverage[line.UUID];

            model.Lines.Add(new DeliveryAvailabilityLineModel
            {
                LineUuid        = line.UUID,
                LineNo          = line.LineNo,
                ItemDescription = line.ItemDescription,
                VariantUuid     = line.VariantUuid,
                QtyOrdered      = line.QtyOrdered,
                QtyAvailable    = cover.Available,
                Shortfall       = Math.Max(0m, line.QtyOrdered - cover.Available),
                WarehouseName   = cover.WarehouseName,
                Warning         = cover.Warning
            });
        }

        model.CanReleaseInFull    = model.Lines.All(l => l.Shortfall <= 0);
        model.CanReleasePartially = !model.CanReleaseInFull && model.Lines.Any(l => l.QtyAvailable > 0);

        return model;
    }

    private sealed record LineCoverage(decimal Available, string? WarehouseName, string? Warning);

    /// <summary>
    /// How much of each line the stock on hand could cover.
    /// <para>
    /// Deliberately reports the <b>best single warehouse</b>, not the sum across all of them,
    /// because that is the rule the reservation itself applies. Summing would show a line as
    /// coverable when no one warehouse can actually ship it, and the release would then refuse
    /// something this screen had just said was fine.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, LineCoverage>> CoverageAsync(DeliveryOrder delivery)
    {
        var coverage = new Dictionary<Guid, LineCoverage>();

        var variantUuids = delivery.Lines
            .Where(l => l.VariantUuid.HasValue)
            .Select(l => l.VariantUuid!.Value)
            .Distinct()
            .ToList();

        var stock = await _reservations.GetAvailableAsync(
            variantUuids, delivery.ShipFromWarehouseUuid);

        foreach (var line in delivery.Lines)
        {
            if (line.VariantUuid is not { } variantUuid)
            {
                coverage[line.UUID] = new LineCoverage(
                    0m, null,
                    "No stock item is resolved for this line, so its stock cannot be reserved.");
                continue;
            }

            var best = stock.FirstOrDefault(s => s.VariantUuid == variantUuid);

            coverage[line.UUID] = best is null
                ? new LineCoverage(0m, null, "No stock record exists for this item in the requested warehouse.")
                : new LineCoverage(best.Available, best.WarehouseName, null);
        }

        return coverage;
    }

    // ── Splitting out what cannot be covered ──────────────────────────────────

    /// <summary>
    /// Trims each line to what stock can cover and moves the remainder to a new draft delivery
    /// against the same source document.
    /// <para>
    /// The shortfall becomes a document somebody owns rather than a failed button press: it keeps
    /// the source reference, so the balance is still visible as outstanding against the PO or
    /// request it came from.
    /// </para>
    /// </summary>
    private async Task SplitOutShortfallAsync(DeliveryOrder delivery, int userId)
    {
        var coverage = await CoverageAsync(delivery);

        var shortfalls = delivery.Lines
            .Select(line => new
            {
                Line      = line,
                Available = Math.Min(line.QtyOrdered, coverage[line.UUID].Available),
                Shortfall = Math.Max(0m, line.QtyOrdered - coverage[line.UUID].Available)
            })
            .Where(x => x.Shortfall > 0)
            .ToList();

        if (shortfalls.Count == 0) return;   // everything is covered; nothing to split

        // Nothing at all is coverable. Splitting here would release an empty delivery and clone
        // the whole thing, which is two useless documents instead of one honest refusal.
        if (delivery.Lines.All(l => coverage[l.UUID].Available <= 0))
            throw new ConflictException(
                $"No stock is available for any line of delivery {delivery.DeliveryNumber}, so " +
                "there is nothing to release. Splitting would only duplicate the document.");

        var now = DateTime.UtcNow;

        var backorder = new DeliveryOrder
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = delivery.OrganizationId,
            // Same lineage and same source: the balance is still outstanding against whatever
            // this delivery was raised from.
            TraceId        = delivery.TraceId,
            DeliveryNumber = await _numbers.NextAsync(
                DocumentNumberPrefix.Delivery, now, delivery.OrganizationId),
            Direction      = delivery.Direction,
            SourceType     = delivery.SourceType,
            SourceUuid     = delivery.SourceUuid,
            SourceNumber   = delivery.SourceNumber,
            ShipFromAddressId     = delivery.ShipFromAddressId,
            ShipToAddressId       = delivery.ShipToAddressId,
            ShipFromWarehouseUuid = delivery.ShipFromWarehouseUuid,
            ShipToWarehouseUuid   = delivery.ShipToWarehouseUuid,
            RequestedDate  = delivery.RequestedDate,
            PromisedDate   = delivery.PromisedDate,
            Priority       = delivery.Priority,
            Incoterm       = delivery.Incoterm,
            Status         = LogisticsCode.Of(DeliveryStatus.Draft),
            Notes          = $"Balance split from delivery {delivery.DeliveryNumber}, which was "
                           + "released for the quantity stock could cover.",
            IsActive       = true,
            CreatedBy      = userId,
            CreatedDate    = now
        };

        var lineNo = 1;

        foreach (var entry in shortfalls)
        {
            backorder.Lines.Add(new DeliveryOrderLine
            {
                UUID                    = Guid.NewGuid(),
                OrganizationId          = delivery.OrganizationId,
                LineNo                  = lineNo++,
                VariantUuid             = entry.Line.VariantUuid,
                ProductUuid             = entry.Line.ProductUuid,
                ItemDescription         = entry.Line.ItemDescription,
                UnitOfMeasure           = entry.Line.UnitOfMeasure,
                QtyOrdered              = entry.Shortfall,
                BatchNumber             = entry.Line.BatchNumber,
                SerialNumber            = entry.Line.SerialNumber,
                // Kept so the balance still points at the same source line — this is what stops
                // the outstanding quantity being advised twice against a purchase order.
                SourceLineUuid          = entry.Line.SourceLineUuid,
                UnitValue               = entry.Line.UnitValue,
                IsHazardous             = entry.Line.IsHazardous,
                IsFragile               = entry.Line.IsFragile,
                IsTemperatureControlled = entry.Line.IsTemperatureControlled,
                CreatedBy               = userId,
                CreatedDate             = now
            });
        }

        // Trim this delivery to what it can actually fulfil. A line nothing is available for is
        // removed outright — a line for zero cannot be picked and would fail validation anyway.
        foreach (var entry in shortfalls)
        {
            if (entry.Available <= 0) delivery.Lines.Remove(entry.Line);
            else                      entry.Line.QtyOrdered = entry.Available;
        }

        // Renumber so the remaining lines read 1, 2, 3 rather than carrying gaps.
        var remaining = 1;
        foreach (var line in delivery.Lines.OrderBy(l => l.LineNo))
            line.LineNo = remaining++;

        _db.DeliveryOrders.Add(backorder);
        await _db.SaveChangesAsync();
    }

    private async Task ReserveStockAsync(DeliveryOrder delivery, int userId)
    {
        var requests = new List<ReservationRequest>();

        foreach (var line in delivery.Lines.OrderBy(l => l.LineNo))
        {
            // A line whose product never resolved to a variant (see the SRO path in T-12) cannot
            // be reserved, because stock is held per variant. Refusing here is better than
            // releasing a delivery that silently holds nothing for that line.
            if (line.VariantUuid is not { } variantUuid)
                throw new ConflictException(
                    $"Line {line.LineNo} ({line.ItemDescription}) has no stock item resolved, so " +
                    "its stock cannot be reserved. Set the item on the line before releasing.");

            requests.Add(new ReservationRequest(
                variantUuid,
                delivery.ShipFromWarehouseUuid,
                line.QtyOrdered,
                line.UUID));
        }

        var result = await _reservations.ReserveAsync(
            ReservationSourceType.Delivery, delivery.UUID, requests, userId);

        if (result.Succeeded) return;

        // All-or-nothing: nothing was held, so the delivery stays in DRAFT and the message says
        // exactly which lines are short and by how much.
        var detail = string.Join(" ", result.Shortfalls.Select(s =>
        {
            var line = delivery.Lines.First(l => l.UUID == s.SourceLineUuid);
            return $"Line {line.LineNo} ({line.ItemDescription}): needed {s.Requested:0.###}, " +
                   $"{s.Available:0.###} available.";
        }));

        throw new ConflictException(
            $"There is not enough stock to release delivery {delivery.DeliveryNumber}. {detail}");
    }
}
