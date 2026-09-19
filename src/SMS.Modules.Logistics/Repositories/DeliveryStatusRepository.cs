using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface IDeliveryStatusRepository
{
    Task<bool> HoldAsync(Guid uuid, DeliveryReasonRequest req, int userId);
    Task<bool> ResumeAsync(Guid uuid, int userId);
    Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId);
    Task<bool> ShortCloseAsync(Guid uuid, DeliveryReasonRequest req, int userId);
}

/// <summary>
/// The off-path transitions: pausing a delivery, abandoning it, and closing it for less than was
/// ordered.
/// <para>
/// Every transition here is validated by <see cref="DeliveryStateMachine"/> rather than by
/// hand-written status checks, so the rules live in exactly one place and the endpoints cannot
/// drift from what the detail response advertises as allowed.
/// </para>
/// </summary>
internal sealed class DeliveryStatusRepository : IDeliveryStatusRepository
{
    private static readonly DeliveryStateMachine Machine = DeliveryStateMachine.Instance;

    private readonly LogisticsDbContext       _db;
    private readonly IStockReservationService _reservations;

    public DeliveryStatusRepository(LogisticsDbContext db, IStockReservationService reservations)
    {
        _db           = db;
        _reservations = reservations;
    }

    // ── Hold ──────────────────────────────────────────────────────────────────

    public async Task<bool> HoldAsync(Guid uuid, DeliveryReasonRequest req, int userId)
    {
        var reason   = RequireReason(req, "Holding a delivery");
        var delivery = await FindAsync(uuid);
        if (delivery is null) return false;

        var current = Status(delivery);
        Machine.EnsureCanTransition(current, DeliveryStatus.OnHold);

        // Remembering where the hold started is what lets the resume put it back exactly there.
        // Without it the only safe target would be DRAFT, which would strip a released delivery
        // of its released state while its stock is still reserved.
        delivery.StatusBeforeHold = delivery.Status;
        delivery.Status           = LogisticsCode.Of(DeliveryStatus.OnHold);
        delivery.HoldReason       = reason;
        delivery.HeldAt           = DateTime.UtcNow;
        delivery.HeldBy           = userId;

        Touch(delivery, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResumeAsync(Guid uuid, int userId)
    {
        var delivery = await FindAsync(uuid);
        if (delivery is null) return false;

        if (Status(delivery) != DeliveryStatus.OnHold)
            throw new ConflictException(
                $"This delivery is {delivery.Status}, not ON_HOLD, so there is nothing to resume.");

        if (string.IsNullOrWhiteSpace(delivery.StatusBeforeHold))
            throw new ConflictException(
                "This delivery does not record the status its hold interrupted, so it cannot be " +
                "resumed automatically. Move it to the correct status explicitly.");

        var target = LogisticsCode.Parse<DeliveryStatus>(delivery.StatusBeforeHold);
        Machine.EnsureCanTransition(DeliveryStatus.OnHold, target);

        delivery.Status           = delivery.StatusBeforeHold;
        delivery.StatusBeforeHold = null;
        delivery.HoldReason       = null;
        delivery.HeldAt           = null;
        delivery.HeldBy           = null;

        Touch(delivery, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    public async Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId)
    {
        var reason   = RequireReason(req, "Cancelling a delivery");
        var delivery = await FindAsync(uuid);
        if (delivery is null) return false;

        // The state machine refuses this at or after GOODS_ISSUED: the stock has left the books,
        // and cancelling would leave the ledger asserting a movement the document denies.
        Machine.EnsureCanTransition(Status(delivery), DeliveryStatus.Cancelled);

        delivery.Status           = LogisticsCode.Of(DeliveryStatus.Cancelled);
        delivery.StatusBeforeHold = null;
        delivery.HoldReason       = null;
        Close(delivery, reason, userId);

        // Give the stock back. A cancelled delivery that keeps its hold makes those units
        // permanently unavailable to everyone else, and nothing would ever point at the cause.
        // Idempotent, so a delivery that never reached RELEASED simply frees nothing.
        await _reservations.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, delivery.UUID,
            $"Delivery {delivery.DeliveryNumber} cancelled: {reason}", userId);

        Touch(delivery, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Short close ───────────────────────────────────────────────────────────

    public async Task<bool> ShortCloseAsync(Guid uuid, DeliveryReasonRequest req, int userId)
    {
        var reason   = RequireReason(req, "Short-closing a delivery");
        var delivery = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

        if (delivery is null) return false;

        // Only reachable once something has actually been picked — short-closing a delivery that
        // picked nothing is a cancellation wearing a different name, and would leave a
        // "delivered short" document with no movement behind it.
        Machine.EnsureCanTransition(Status(delivery), DeliveryStatus.ShortClosed);

        foreach (var line in delivery.Lines)
        {
            var shortfall = line.QtyOrdered - line.QtyDelivered;
            if (shortfall <= 0) continue;

            line.QtyShort   = shortfall;
            line.ShortReason ??= reason;
        }

        delivery.Status = LogisticsCode.Of(DeliveryStatus.ShortClosed);
        Close(delivery, reason, userId);

        // The balance is not coming, so whatever is still held for it goes back to available.
        // What was actually issued was consumed at goods issue and is no longer an active hold.
        await _reservations.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, delivery.UUID,
            $"Delivery {delivery.DeliveryNumber} closed short: {reason}", userId);

        Touch(delivery, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<DeliveryOrder?> FindAsync(Guid uuid) =>
        _db.DeliveryOrders.FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

    private static DeliveryStatus Status(DeliveryOrder delivery) =>
        LogisticsCode.Parse<DeliveryStatus>(delivery.Status);

    private static string RequireReason(DeliveryReasonRequest? req, string action)
    {
        if (string.IsNullOrWhiteSpace(req?.Reason))
            throw new BadRequestException(
                $"{action} needs a reason — someone downstream will ask why, and the status alone " +
                "does not answer that.");

        return req.Reason.Trim();
    }

    private static void Close(DeliveryOrder delivery, string reason, int userId)
    {
        delivery.ClosureReason = reason;
        delivery.ClosedAt      = DateTime.UtcNow;
        delivery.ClosedBy      = userId;
        delivery.IsActive      = false;
    }

    private static void Touch(DeliveryOrder delivery, int userId)
    {
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = DateTime.UtcNow;
    }
}
