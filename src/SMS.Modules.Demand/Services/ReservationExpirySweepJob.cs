using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A29-P4-05 §4.4/§5.1 — hourly Hangfire sweep of SALES_ORDER stock holds: releases what has
/// passed its <c>reservation_ttl_hours</c> expiry, and warns 24h ahead of it.
/// <para>
/// Hourly rather than daily like most other sweeps in this codebase (<see cref="RfqLinkExpiryJob"/>,
/// Inventory's <c>RateExpiryNotificationJob</c>) — a reservation's TTL is measured in hours, and
/// the 24h warning needs a window fine enough that a short-TTL order's warning can't fall between
/// two runs.
/// </para>
/// <para>
/// No ambient tenant on a bare recurring job, same as every other sweep here — every query and
/// write below runs inside a <see cref="HangfireTenantScope"/> block for the organization it
/// belongs to, resolved from what <see cref="IStockReservationService.GetExpiredAsync"/> and
/// <see cref="IStockReservationService.GetExpiringWithinAsync"/> already return with query filters
/// bypassed.
/// </para>
/// </summary>
internal sealed class ReservationExpirySweepJob
{
    internal const string RecurringJobId = "sale-order-reservation-expiry-sweep";

    /// <summary>Raised against the system, not a person — nobody pressed a button.</summary>
    internal const int SystemUserId = 0;

    private static readonly TimeSpan WarningWindow = TimeSpan.FromHours(24);

    private readonly DemandDbContext _db;
    private readonly IStockReservationService _stock;
    private readonly INotificationService _notifications;
    private readonly ILogger<ReservationExpirySweepJob> _log;

    public ReservationExpirySweepJob(
        DemandDbContext db, IStockReservationService stock,
        INotificationService notifications, ILogger<ReservationExpirySweepJob> log)
    {
        _db            = db;
        _stock         = stock;
        _notifications = notifications;
        _log           = log;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        var released = await ReleaseExpiredAsync();
        var warned   = await SendExpiryWarningsAsync();

        if (released > 0 || warned > 0)
            _log.LogInformation(
                "Sale order reservation sweep: released {Released} expired line(s), sent {Warned} expiry warning(s).",
                released, warned);
    }

    /// <summary>
    /// §4.4 — an expired hold is released and its line goes back to OPEN with the full quantity
    /// unfulfilled again, exactly as if it had never been reserved.
    /// <para>
    /// Released one reservation at a time via <see cref="IStockReservationService.ReleaseAllocationAsync"/>,
    /// not by source: today's only writer (<c>CheckAndReserveAsync</c>) always stamps every line of
    /// an order with the same <c>now + reservation_ttl_hours</c> expiry, but nothing in the ledger's
    /// shape guarantees that stays true — <c>ReleaseBySourceAsync</c> would free every active hold
    /// under the order, including a sibling line whose own reservation had not actually expired.
    /// Releasing by reservation UUID only ever touches the row <see cref="IStockReservationService.GetExpiredAsync"/>
    /// actually named.
    /// </para>
    /// <para>
    /// Saved after each line, not once at the end of the sweep: the release itself commits on
    /// Inventory's own database immediately, so batching every line's update into one final save
    /// here would leave a released hold whose line still shows RESERVED if the job died partway
    /// through — and a released reservation never reappears from <c>GetExpiredAsync</c> for a retry
    /// to pick back up. Saving right after each line keeps that window to one line.
    /// </para>
    /// </summary>
    private async Task<int> ReleaseExpiredAsync()
    {
        var expired = await _stock.GetExpiredAsync(ReservationSourceType.SalesOrder);
        if (expired.Count == 0) return 0;

        var released = 0;

        foreach (var orgGroup in expired.GroupBy(r => r.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = orgGroup.Key;
            try
            {
                foreach (var reservation in orgGroup)
                {
                    await _stock.ReleaseAllocationAsync(
                        reservation.ReservationUuid, reservation.ReservedQty,
                        "Reservation expired before the sale order was fulfilled.", SystemUserId);

                    if (reservation.SourceLineUuid is not { } lineUuid) continue;

                    var line = await _db.SaleOrderLines.FirstOrDefaultAsync(l => l.UUID == lineUuid);
                    if (line is null) continue;

                    line.Status     = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open);
                    line.DeficitQty = line.Quantity;

                    await _db.SaveChangesAsync();
                    released++;
                }
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        return released;
    }

    /// <summary>
    /// §5.1's "Reservation expiring" event — SO creator, "expires in 24h". Sent once per
    /// reservation, deduped through <see cref="IStockReservationService.MarkExpiryWarningSentAsync"/>
    /// exactly as RC-007's rate-expiry job dedupes via ExpiryNotifiedAt.
    /// <para>
    /// Goes through the real notification/email pipeline directly rather than another placeholder
    /// job: unlike P4-03/04's confirm/cancel emails, this event's own spec text is one line
    /// ("expires in 24h") with no §5.2 template to wait on, so there is nothing left to build
    /// around.
    /// </para>
    /// </summary>
    private async Task<int> SendExpiryWarningsAsync()
    {
        var expiring = await _stock.GetExpiringWithinAsync(ReservationSourceType.SalesOrder, WarningWindow);
        if (expiring.Count == 0) return 0;

        var sent = 0;

        foreach (var orgGroup in expiring.GroupBy(r => r.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = orgGroup.Key;
            try
            {
                var orderUuids = orgGroup.Select(r => r.SourceUuid).Distinct().ToList();
                var orders = await _db.SaleOrders
                    .Where(o => orderUuids.Contains(o.UUID))
                    .ToDictionaryAsync(o => o.UUID);

                var warnedUuids = new List<Guid>();

                foreach (var reservation in orgGroup)
                {
                    if (!orders.TryGetValue(reservation.SourceUuid, out var order))
                        continue;

                    await _notifications.TryCreateAsync(new NotificationRequest(
                        UserId:        order.CreatedBy,
                        Type:          "EXPIRING",
                        Title:         "Reservation Expiring",
                        Message:       $"Sale order {order.SoNumber}'s stock reservation expires at " +
                                       $"{reservation.ExpiresAt:dd MMM yyyy HH:mm} UTC — in less than 24 hours.",
                        Category:      "Demand",
                        EntityType:    "SaleOrder",
                        EntityUuid:    order.UUID.ToString(),
                        NavigationUrl: $"/portal/pages/demand/sale-orders/{order.UUID}",
                        SendEmail:     true));

                    warnedUuids.Add(reservation.ReservationUuid);
                    sent++;
                }

                if (warnedUuids.Count > 0)
                    await _stock.MarkExpiryWarningSentAsync(warnedUuids);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        return sent;
    }
}
