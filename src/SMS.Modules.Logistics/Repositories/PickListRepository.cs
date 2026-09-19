using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Repositories;

internal interface IPickListRepository
{
    Task<Guid> GenerateAsync(Guid deliveryUuid, GeneratePickListRequest? req, int userId);
    Task<PickListModel?> GetByUuidAsync(Guid uuid);
    Task<PickListModel?> GetForDeliveryAsync(Guid deliveryUuid);
    Task<PaginatedResponse<PickListListItemModel>> GetListAsync(PickListFilter filter);
    Task<bool> AssignAsync(Guid uuid, int assignToUserId, int userId);
    Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId);
    Task<ConfirmPickResultModel?> ConfirmAsync(Guid uuid, ConfirmPickRequest req, int userId);
}

/// <summary>
/// Turns a released delivery into a walk through the warehouse.
/// <para>
/// The stock was already chosen when the delivery was released — the reservation picked specific
/// rows, FEFO, in one warehouse. This repository <b>reports that choice</b>; it does not make a
/// new one. Choosing again here is the obvious-looking mistake: the pick list would send someone
/// to a bin whose stock this delivery is not holding, and two deliveries would be sent to the same
/// units.
/// </para>
/// </summary>
internal sealed class PickListRepository : IPickListRepository
{
    /// <summary>A delivery may only have one live pick list; these are the statuses that count.</summary>
    private static readonly string[] LiveStatuses =
    [
        LogisticsCode.Of(PickListStatus.Open),
        LogisticsCode.Of(PickListStatus.InProgress)
    ];

    private readonly LogisticsDbContext       _db;
    private readonly IStockReservationService _reservations;
    private readonly IDocumentNumberGenerator _numbers;

    public PickListRepository(
        LogisticsDbContext db,
        IStockReservationService reservations,
        IDocumentNumberGenerator numbers)
    {
        _db           = db;
        _reservations = reservations;
        _numbers      = numbers;
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<Guid> GenerateAsync(Guid deliveryUuid, GeneratePickListRequest? req, int userId)
    {
        var delivery = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.UUID == deliveryUuid && !d.IsDelete)
            ?? throw new NotFoundException("Delivery not found.");

        var current = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);
        DeliveryStateMachine.Instance.EnsureCanTransition(current, DeliveryStatus.Picking);

        if (LogisticsCode.Parse<DeliveryDirection>(delivery.Direction) == DeliveryDirection.Inbound)
            throw new ConflictException(
                "An inbound delivery has nothing to pick — the goods are arriving, not leaving. " +
                "Receive it against a GRN instead.");

        // Generating a second list would print two sets of paper for one set of units, and a
        // picker working the stale one would short-pick against instructions that were already
        // superseded.
        var existing = await LiveListFor(delivery.Id);
        if (existing is not null)
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} already has pick list {existing.PickListNumber} " +
                $"({existing.Status}). Cancel it before generating another.");

        var allocations = await _reservations.GetAllocationsAsync(
            ReservationSourceType.Delivery, delivery.UUID);

        if (allocations.Count == 0)
            throw new ConflictException(
                $"No stock is currently held for delivery {delivery.DeliveryNumber}, so there is " +
                "nothing to pick. Release it first, or check whether its reservation was returned.");

        // Every hold for one delivery sits in one warehouse — the reservation allocates from a
        // single site. Asserted rather than assumed: a pick list spanning two buildings is not a
        // walk anybody can do, and silently taking the first would send the picker to one of them.
        var warehouses = allocations.Select(a => a.WarehouseUuid).Distinct().ToList();
        if (warehouses.Count > 1)
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} holds stock in {warehouses.Count} warehouses, " +
                "which cannot be one pick list. Split the delivery so each covers one site.");

        var now = DateTime.UtcNow;

        var pickList = new PickList
        {
            UUID             = Guid.NewGuid(),
            OrganizationId   = delivery.OrganizationId,
            DeliveryOrderId  = delivery.Id,
            PickListNumber   = await _numbers.NextAsync(
                                   DocumentNumberPrefix.PickList, now, delivery.OrganizationId),
            Status           = LogisticsCode.Of(PickListStatus.Open),
            WarehouseUuid    = allocations[0].WarehouseUuid,
            WarehouseName    = allocations[0].WarehouseName,
            AssignedToUserId = req?.AssignedToUserId,
            GeneratedAt      = now,
            Notes            = string.IsNullOrWhiteSpace(req?.Notes) ? null : req!.Notes!.Trim(),
            IsActive         = true,
            CreatedBy        = userId,
            CreatedDate      = now
        };

        var seq = 1;

        foreach (var allocation in InWalkOrder(allocations, delivery))
        {
            var line = LineFor(delivery, allocation);

            pickList.Lines.Add(new PickListLine
            {
                UUID                = Guid.NewGuid(),
                OrganizationId      = delivery.OrganizationId,
                SeqNo               = seq++,
                DeliveryOrderLineId = line.Id,
                ReservationUuid     = allocation.ReservationUuid,
                VariantUuid         = line.VariantUuid,
                ItemDescription     = line.ItemDescription,
                UnitOfMeasure       = line.UnitOfMeasure,
                ZoneName            = allocation.ZoneName,
                BinCode             = allocation.BinCode,
                BatchNumber         = allocation.BatchNumber,
                SerialNumber        = allocation.SerialNumber,
                ExpiryDate          = allocation.ExpiryDate,
                QtyToPick           = allocation.Quantity,
                CreatedBy           = userId,
                CreatedDate         = now
            });
        }

        delivery.Status       = LogisticsCode.Of(DeliveryStatus.Picking);
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = now;

        _db.PickLists.Add(pickList);
        await _db.SaveChangesAsync();

        return pickList.UUID;
    }

    /// <summary>
    /// The delivery line an allocation belongs to.
    /// <para>
    /// Matched on <c>SourceLineUuid</c>, which the release wrote as the delivery line's own UUID.
    /// A hold that matches nothing is refused rather than guessed at: picking against the wrong
    /// line would credit the quantity to the wrong item.
    /// </para>
    /// </summary>
    private static DeliveryOrderLine LineFor(DeliveryOrder delivery, StockAllocation allocation)
    {
        var line = allocation.SourceLineUuid is { } lineUuid
            ? delivery.Lines.FirstOrDefault(l => l.UUID == lineUuid)
            : null;

        return line ?? throw new ConflictException(
            $"A stock reservation on delivery {delivery.DeliveryNumber} does not match any of its " +
            "lines, so it cannot be picked. The delivery's lines may have changed since it was " +
            "released — cancel it and raise it again.");
    }

    /// <summary>
    /// The order to walk the warehouse in.
    /// <para>
    /// <b>Zone, then bin code</b> — which is a location walk, not a distance calculation. Bins
    /// carry no coordinates and no pick sequence (see F28), so there is nothing in the schema that
    /// says which bin is nearer the dock, and inventing a distance would be a guess dressed up as
    /// routing. Sorting by code keeps a picker inside one zone and moving through it in aisle
    /// order, which is what a location sequence is for; if a real walk order is wanted later, a
    /// <c>PickSequence</c> on <c>Bin</c> is the column to add and this is the one place to read it.
    /// </para>
    /// <para>
    /// Stock not yet put away has no bin at all, and sorts last: the picker is told the item is in
    /// the warehouse but not where, which is honest, rather than being sent to bin "" first.
    /// </para>
    /// </summary>
    private static IEnumerable<StockAllocation> InWalkOrder(
        IReadOnlyList<StockAllocation> allocations, DeliveryOrder delivery)
    {
        var lineNo = delivery.Lines.ToDictionary(l => l.UUID, l => l.LineNo);

        return allocations
            .OrderBy(a => string.IsNullOrWhiteSpace(a.ZoneName) ? 1 : 0)
            .ThenBy(a => a.ZoneName)
            .ThenBy(a => string.IsNullOrWhiteSpace(a.BinCode) ? 1 : 0)
            .ThenBy(a => a.BinCode)
            // Within one location, oldest stock first, so the FEFO choice survives into the order
            // the picker actually takes things off the shelf in.
            .ThenBy(a => a.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(a => a.ExpiryDate)
            .ThenBy(a => a.SourceLineUuid is { } id && lineNo.TryGetValue(id, out var no) ? no : int.MaxValue);
    }

    private Task<PickList?> LiveListFor(int deliveryOrderId) =>
        _db.PickLists
            .Where(p => p.DeliveryOrderId == deliveryOrderId && !p.IsDelete)
            .Where(p => LiveStatuses.Contains(p.Status))
            .FirstOrDefaultAsync();

    // ── Read ──────────────────────────────────────────────────────────────────

    public Task<PickListModel?> GetByUuidAsync(Guid uuid) =>
        Detail(p => p.UUID == uuid);

    public async Task<PickListModel?> GetForDeliveryAsync(Guid deliveryUuid)
    {
        var deliveryId = await _db.DeliveryOrders
            .Where(d => d.UUID == deliveryUuid && !d.IsDelete)
            .Select(d => (int?)d.Id)
            .FirstOrDefaultAsync();

        if (deliveryId is null) return null;

        // The live one if there is one, otherwise the most recent, so a cancelled list is still
        // visible rather than the screen claiming the delivery never had one.
        var live = await LiveListFor(deliveryId.Value);

        return live is not null
            ? await Detail(p => p.Id == live.Id)
            : await Detail(p => p.DeliveryOrderId == deliveryId.Value, mostRecent: true);
    }

    private async Task<PickListModel?> Detail(
        System.Linq.Expressions.Expression<Func<PickList, bool>> predicate, bool mostRecent = false)
    {
        var query = _db.PickLists
            .Include(p => p.DeliveryOrder)
            .Include(p => p.Lines).ThenInclude(l => l.DeliveryOrderLine)
            .AsNoTracking()
            .Where(p => !p.IsDelete)
            .Where(predicate);

        if (mostRecent) query = query.OrderByDescending(p => p.Id);

        var pickList = await query.FirstOrDefaultAsync();
        if (pickList is null) return null;

        return new PickListModel
        {
            UUID             = pickList.UUID,
            PickListNumber   = pickList.PickListNumber,
            Status           = pickList.Status,
            DeliveryUuid     = pickList.DeliveryOrder.UUID,
            DeliveryNumber   = pickList.DeliveryOrder.DeliveryNumber,
            WarehouseUuid    = pickList.WarehouseUuid,
            WarehouseName    = pickList.WarehouseName,
            AssignedToUserId = pickList.AssignedToUserId,
            GeneratedAt      = pickList.GeneratedAt,
            StartedAt        = pickList.StartedAt,
            CompletedAt      = pickList.CompletedAt,
            Notes            = pickList.Notes,
            CancelReason     = pickList.CancelReason,
            Lines            = [.. pickList.Lines.OrderBy(l => l.SeqNo).Select(l => new PickListLineModel
            {
                UUID             = l.UUID,
                SeqNo            = l.SeqNo,
                DeliveryLineUuid = l.DeliveryOrderLine.UUID,
                DeliveryLineNo   = l.DeliveryOrderLine.LineNo,
                VariantUuid      = l.VariantUuid,
                ItemDescription  = l.ItemDescription,
                UnitOfMeasure    = l.UnitOfMeasure,
                ZoneName         = l.ZoneName,
                BinCode          = l.BinCode,
                BatchNumber      = l.BatchNumber,
                SerialNumber     = l.SerialNumber,
                ExpiryDate       = l.ExpiryDate,
                QtyToPick        = l.QtyToPick,
                QtyPicked        = l.QtyPicked,
                QtyShort         = l.QtyShort,
                ShortReasonCode  = l.ShortReasonCode,
                ShortReason      = l.ShortReason,
                PickedAt         = l.PickedAt,
                IsConfirmed      = l.PickedAt is not null
            })]
        };
    }

    public async Task<PaginatedResponse<PickListListItemModel>> GetListAsync(PickListFilter filter)
    {
        var q = _db.PickLists.Where(p => !p.IsDelete);

        if (!string.IsNullOrWhiteSpace(filter.Status))
            q = q.Where(p => p.Status == filter.Status);

        if (filter.WarehouseUuid.HasValue)
            q = q.Where(p => p.WarehouseUuid == filter.WarehouseUuid.Value);

        if (filter.AssignedToUserId.HasValue)
            q = q.Where(p => p.AssignedToUserId == filter.AssignedToUserId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(p => p.PickListNumber.ToLower().Contains(s)
                          || p.DeliveryOrder.DeliveryNumber.ToLower().Contains(s));
        }

        var page     = filter.Page     < 1 ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var total = await q.CountAsync();

        var data = await q
            .OrderByDescending(p => p.GeneratedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PickListListItemModel
            {
                UUID             = p.UUID,
                PickListNumber   = p.PickListNumber,
                Status           = p.Status,
                DeliveryUuid     = p.DeliveryOrder.UUID,
                DeliveryNumber   = p.DeliveryOrder.DeliveryNumber,
                WarehouseName    = p.WarehouseName,
                AssignedToUserId = p.AssignedToUserId,
                LineCount        = p.Lines.Count,
                QtyToPick        = p.Lines.Sum(l => l.QtyToPick),
                QtyPicked        = p.Lines.Sum(l => l.QtyPicked),
                GeneratedAt      = p.GeneratedAt
            })
            .ToListAsync();

        return new PaginatedResponse<PickListListItemModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> AssignAsync(Guid uuid, int assignToUserId, int userId)
    {
        var pickList = await _db.PickLists.FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete);
        if (pickList is null) return false;

        EnsureLive(pickList, "reassigned");

        pickList.AssignedToUserId = assignToUserId;
        pickList.ModifiedBy       = userId;
        pickList.ModifiedDate     = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Abandons the walk and returns the delivery to <c>RELEASED</c>.
    /// <para>
    /// The stock stays reserved. The delivery is still going out — only the instruction sheet is
    /// being torn up, usually to regenerate it — and releasing the hold here would let something
    /// else take units this delivery has already promised.
    /// </para>
    /// </summary>
    public async Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId)
    {
        if (string.IsNullOrWhiteSpace(req?.Reason))
            throw new BadRequestException("A reason is required to cancel a pick list.");

        var pickList = await _db.PickLists
            .Include(p => p.DeliveryOrder)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete);

        if (pickList is null) return false;

        EnsureLive(pickList, "cancelled");

        if (pickList.Lines.Any(l => l.QtyPicked > 0))
            throw new ConflictException(
                $"Pick list {pickList.PickListNumber} has stock already picked against it. " +
                "Complete it and short-close the delivery instead — cancelling now would lose " +
                "the record of what was taken off the shelf.");

        var now = DateTime.UtcNow;

        pickList.Status       = LogisticsCode.Of(PickListStatus.Cancelled);
        pickList.CancelReason = req.Reason.Trim();
        pickList.IsActive     = false;
        pickList.ModifiedBy   = userId;
        pickList.ModifiedDate = now;

        // Back to where it was before the list existed, so another can be generated.
        pickList.DeliveryOrder.Status       = LogisticsCode.Of(DeliveryStatus.Released);
        pickList.DeliveryOrder.ModifiedBy   = userId;
        pickList.DeliveryOrder.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Confirmation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Records what the picker actually found, instruction by instruction.
    /// <para>
    /// Lines may be confirmed in any order and in any number of calls — a picker walks the list
    /// over time, and an RF gun reports each stop as it happens. A line already answered may be
    /// answered again while the list is live, which is how a miscount gets corrected.
    /// </para>
    /// <para>
    /// <b>The stock reconciliation happens once, on completion, not per line.</b> Releasing the
    /// shortfall as each line came in would make a correction impossible: stock handed back cannot
    /// be re-held on demand — something else may already have taken it — so a picker who under-
    /// reported and then fixed it would find the units gone. Deferring means the hold is squared
    /// against final quantities exactly once.
    /// </para>
    /// </summary>
    public async Task<ConfirmPickResultModel?> ConfirmAsync(
        Guid uuid, ConfirmPickRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var pickList = await _db.PickLists
            .Include(p => p.DeliveryOrder).ThenInclude(d => d.Lines)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete);

        if (pickList is null) return null;

        EnsureLive(pickList, "confirmed against");

        // A delivery that has been put on hold is not being picked, whatever the list says. The
        // state machine owns that rule; this is where it applies to the shop floor.
        var deliveryStatus = LogisticsCode.Parse<DeliveryStatus>(pickList.DeliveryOrder.Status);
        if (deliveryStatus != DeliveryStatus.Picking)
            throw new ConflictException(
                $"Delivery {pickList.DeliveryOrder.DeliveryNumber} is {pickList.DeliveryOrder.Status}, " +
                "not PICKING, so nothing can be picked against it.");

        if (req.Lines.Count == 0)
            throw new BadRequestException("Confirm at least one line.");

        var duplicate = req.Lines.GroupBy(l => l.LineUuid).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new BadRequestException(
                $"Line {duplicate.Key} appears more than once in the same confirmation. " +
                "Send one quantity per line — two would silently overwrite each other.");

        var now = DateTime.UtcNow;

        foreach (var confirmation in req.Lines)
            Apply(pickList, confirmation, userId, now);

        if (pickList.StartedAt is null) pickList.StartedAt = now;

        pickList.Status       = LogisticsCode.Of(PickListStatus.InProgress);
        pickList.ModifiedBy   = userId;
        pickList.ModifiedDate = now;

        var outstanding = pickList.Lines.Count(l => l.PickedAt is null);
        var returned    = 0m;

        if (outstanding == 0)
            returned = await CompleteAsync(pickList, userId, now);

        await _db.SaveChangesAsync();

        return new ConfirmPickResultModel
        {
            PickListUuid       = pickList.UUID,
            PickListStatus     = pickList.Status,
            DeliveryStatus     = pickList.DeliveryOrder.Status,
            Completed          = outstanding == 0,
            LinesConfirmed     = pickList.Lines.Count - outstanding,
            LinesOutstanding   = outstanding,
            QtyPicked          = pickList.Lines.Sum(l => l.QtyPicked),
            QtyShort           = pickList.Lines.Sum(l => l.QtyShort),
            QtyReturnedToStock = returned
        };
    }

    private static void Apply(
        PickList pickList, ConfirmPickLineRequest confirmation, int userId, DateTime now)
    {
        var line = pickList.Lines.FirstOrDefault(l => l.UUID == confirmation.LineUuid)
            ?? throw new BadRequestException(
                $"Line {confirmation.LineUuid} does not belong to pick list {pickList.PickListNumber}.");

        if (confirmation.QtyPicked < 0)
            throw new BadRequestException(
                $"Line {line.SeqNo}: a picked quantity cannot be negative.");

        // Over-picking is refused rather than absorbed. The surplus is not held by this delivery,
        // so accepting it would ship units another document has already promised — and the goods
        // issue would then deduct stock this delivery never reserved.
        if (confirmation.QtyPicked > line.QtyToPick)
            throw new ConflictException(
                $"Line {line.SeqNo} ({line.ItemDescription}): {confirmation.QtyPicked:0.###} picked " +
                $"but only {line.QtyToPick:0.###} is reserved for this delivery. Stock beyond the " +
                "instruction belongs to another document.");

        var shortfall = line.QtyToPick - confirmation.QtyPicked;

        if (shortfall > 0)
        {
            if (string.IsNullOrWhiteSpace(confirmation.ShortReasonCode))
                throw new BadRequestException(
                    $"Line {line.SeqNo} ({line.ItemDescription}) is short by {shortfall:0.###}, " +
                    $"so a reason is required. Valid values: " +
                    $"{string.Join(", ", LogisticsCode.Codes<PickShortReason>())}.");

            if (!LogisticsCode.TryParse<PickShortReason>(confirmation.ShortReasonCode, out var reason))
                throw new BadRequestException(
                    $"'{confirmation.ShortReasonCode}' is not a valid short reason. Valid values: " +
                    $"{string.Join(", ", LogisticsCode.Codes<PickShortReason>())}.");

            line.ShortReasonCode = LogisticsCode.Of(reason);
            line.ShortReason     = Trim(confirmation.ShortNote);
        }
        else
        {
            // A corrected line that is no longer short must not keep yesterday's excuse.
            line.ShortReasonCode = null;
            line.ShortReason     = null;
        }

        line.QtyPicked = confirmation.QtyPicked;
        line.QtyShort  = shortfall;
        line.PickedAt  = now;
        line.PickedBy  = userId;
    }

    /// <summary>
    /// Closes the list: rolls the picked quantities up onto the delivery, hands back the stock
    /// that was not picked, and moves the delivery to PICKED.
    /// </summary>
    /// <returns>How much stock was returned to available.</returns>
    private async Task<decimal> CompleteAsync(PickList pickList, int userId, DateTime now)
    {
        var delivery = pickList.DeliveryOrder;

        // Roll up. A delivery line can be spread over several instructions, so this is a sum over
        // the line's own instructions rather than a copy of one of them.
        foreach (var deliveryLine in delivery.Lines)
        {
            deliveryLine.QtyPicked = pickList.Lines
                .Where(l => l.DeliveryOrderLineId == deliveryLine.Id)
                .Sum(l => l.QtyPicked);
        }

        // Hand back what was not picked, per instruction, so only the bin that came up short
        // shrinks. Without this the goods issue would consume the full original hold and deduct
        // stock that never moved.
        var returned = 0m;

        foreach (var line in pickList.Lines.Where(l => l.QtyShort > 0))
        {
            returned += await _reservations.ReleaseAllocationAsync(
                line.ReservationUuid, line.QtyShort,
                $"Short picked on {pickList.PickListNumber}: {line.ShortReasonCode}", userId);
        }

        pickList.Status      = LogisticsCode.Of(PickListStatus.Completed);
        pickList.CompletedAt = now;
        pickList.IsActive    = false;

        DeliveryStateMachine.Instance.EnsureCanTransition(
            LogisticsCode.Parse<DeliveryStatus>(delivery.Status), DeliveryStatus.Picked);

        delivery.Status       = LogisticsCode.Of(DeliveryStatus.Picked);
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = now;

        return returned;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureLive(PickList pickList, string verb)
    {
        if (!LiveStatuses.Contains(pickList.Status))
            throw new ConflictException(
                $"Pick list {pickList.PickListNumber} is {pickList.Status} and can no longer be {verb}.");
    }
}
