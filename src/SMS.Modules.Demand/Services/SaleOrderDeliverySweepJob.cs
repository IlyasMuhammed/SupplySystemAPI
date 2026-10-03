using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A33 D-1 / D-12 — one call to Logistics' delivery creator for a confirmed order, shared by the confirm (right after
/// its commit) and the sweep. Never throws: the outcome says what happened, the timeline records it, and
/// <see cref="SaleOrder.DeliveryCreationPendingSince"/> is cleared when there is nothing left for the sweep to do (REV-01).
/// </summary>
internal static class SaleOrderDeliveryCreation
{
    /// <summary>How long the sweep leaves a pending order alone, so it never races the confirm's own call (contract §5).</summary>
    internal static readonly TimeSpan SweepDelay = TimeSpan.FromMinutes(10);

    /// <param name="Result">What the creator did; null when it threw.</param>
    /// <param name="Cleared">The order is no longer pending: the creator returned, or refused for good (400/404).</param>
    internal sealed record Outcome(SaleOrderDeliveryCreationResult? Result, Exception? Error, bool Cleared);

    /// <summary>The D-16 snapshots to send: lines confirmed with a route, not cancelled (DROP_SHIP lines have none, D-5).</summary>
    internal static IReadOnlyList<SaleOrderLineRoute> LineRoutes(SaleOrder order)
    {
        var cancelled = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
        return order.Lines
            .Where(l => l.FulfillmentRouteUuid is not null && l.RouteSource is not null && l.Status != cancelled)
            .OrderBy(l => l.Id)
            .Select(l => new SaleOrderLineRoute(l.UUID, l.FulfillmentRouteUuid!.Value))
            .ToList();
    }

    /// <param name="order">Tracked by <paramref name="db"/>; only its pending flag is written.</param>
    internal static async Task<Outcome> RunAsync(
        DemandDbContext db, ISaleOrderDeliveryCreator creator, IBackgroundJobClient jobs, ILogger log, SaleOrder order, int userId)
    {
        SaleOrderDeliveryCreationResult? result = null;
        Exception? error = null;
        try
        {
            result = await creator.CreateForConfirmedOrderAsync(order.OrganizationId, order.UUID, LineRoutes(order), userId);
        }
        catch (Exception ex)
        {
            error = ex;
            log.LogWarning(ex, "Deliveries for sale order {SoNumber} ({SaleOrderUuid}) could not be created.", order.SoNumber, order.UUID);
        }

        // 400/404: the creator will never manage it (the order is no longer creatable) — retrying forever helps no one.
        var cleared = error is null or BadRequestException or NotFoundException;
        if (cleared && order.DeliveryCreationPendingSince is not null)
        {
            order.DeliveryCreationPendingSince = null;
            await db.SaveChangesAsync();
        }

        string? type = null, note = null;
        if (result is not null && result.Created.Count > 0)
        {
            type = SaleOrderTimelineEventTypes.SoDeliveriesCreated;
            note = $"Deliveries created: {string.Join(", ", result.Created.Select(d => $"{d.DeliveryNumber} ({d.RouteCode})"))}."
                 + (result.Skipped.Count > 0 ? $" {result.Skipped.Count} line(s) skipped." : "");
        }
        else if (error is not null)
        {
            type = SaleOrderTimelineEventTypes.SoDeliveryCreationFailed;
            note = cleared ? $"Deliveries could not be created: {error.Message}" : "Deliveries could not be created; they will be retried.";
        }

        if (type is not null)
        {
            var (eventType, eventNote) = (type, note);
            jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                order.TraceId,
                new TimelineEvent(eventType, "SO", order.UUID, order.SoNumber, DateTime.UtcNow, userId, eventNote),
                "SO", order.SoNumber));
        }

        return new Outcome(result, error, cleared);
    }
}

/// <summary>
/// A33 D-12 (REV-01) — creates the deliveries of confirmed sale orders whose post-confirm creation never succeeded:
/// only orders still carrying <see cref="SaleOrder.DeliveryCreationPendingSince"/>, at least
/// <see cref="SaleOrderDeliveryCreation.SweepDelay"/> old, CONFIRMED / PARTIALLY_FULFILLED, in organizations that still
/// have MODULE_LOGISTICS and auto-create on. A delivery a user cancelled is therefore never re-created here; only the
/// explicit "Create deliveries" button creates for outstanding quantity again. The creator is idempotent, so a retry,
/// a double run or a race with that button creates nothing twice.
/// <para>No user and no ambient tenant: every read filters on the organization explicitly, under a
/// <see cref="HangfireTenantScope"/> per organization (the ReservationExpirySweepJob pattern).</para>
/// </summary>
internal sealed class SaleOrderDeliverySweepJob
{
    internal const string RecurringJobId = "sale-order-delivery-creation-sweep";
    internal const string Cron = "*/15 * * * *";

    /// <summary>Raised against the system, not a person.</summary>
    internal const int SystemUserId = 0;

    /// <summary>Orders per run; the rest wait for the next one (oldest first). Settable for tests.</summary>
    internal int BatchSize { get; set; } = 200;

    private readonly DemandDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SaleOrderDeliverySweepJob> _log;
    private readonly IEffectiveRouteResolver? _routes;
    private readonly ISaleOrderDeliveryCreator? _creator;

    public SaleOrderDeliverySweepJob(
        DemandDbContext db, IBackgroundJobClient jobs, ILogger<SaleOrderDeliverySweepJob> log,
        IEffectiveRouteResolver? routes = null, ISaleOrderDeliveryCreator? creator = null)
    {
        _db      = db;
        _jobs    = jobs;
        _log     = log;
        _routes  = routes;
        _creator = creator;
    }

    /// <returns>How many orders are no longer pending after this run.</returns>
    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync()
    {
        if (_creator is null || _routes is null) return 0;

        var cutoff    = DateTime.UtcNow - SaleOrderDeliveryCreation.SweepDelay;
        var confirmed = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed);
        var partial   = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled);

        var due = await _db.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.DeliveryCreationPendingSince != null && o.DeliveryCreationPendingSince <= cutoff && !o.IsDeleted
                     && (o.Status == confirmed || o.Status == partial))
            .OrderBy(o => o.DeliveryCreationPendingSince)
            .Select(o => new { o.OrganizationId, o.UUID })
            .Take(BatchSize)
            .ToListAsync();

        var done = 0;
        foreach (var org in due.GroupBy(o => o.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                var config = await _db.SaleOrderConfigs.IgnoreQueryFilters().AsNoTracking()
                                 .FirstOrDefaultAsync(c => c.OrganizationId == org.Key)
                             ?? new SaleOrderConfig();
                if (!config.AutoCreateDeliveriesOnConfirm || !await _routes.RoutesEnabledAsync(org.Key))
                {
                    // REV-05 — the organization no longer wants deliveries made for it (auto-create switched off, or no
                    // Logistics any more): only the "Create deliveries" button creates them now. Leaving these pending
                    // would keep them at the head of every run and starve every other organization's retries.
                    done += await StopWaitingAsync(org.Key, org.Select(o => o.UUID).ToList());
                    continue;
                }

                foreach (var item in org)
                {
                    // Re-read: it may have been cancelled, or created by the recovery button, since the list was taken.
                    var order = await _db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
                        .FirstOrDefaultAsync(o => o.UUID == item.UUID && o.OrganizationId == org.Key && !o.IsDeleted);
                    if (order is null || order.DeliveryCreationPendingSince is null
                        || (order.Status != confirmed && order.Status != partial))
                        continue;

                    var outcome = await SaleOrderDeliveryCreation.RunAsync(_db, _creator, _jobs, _log, order, SystemUserId);
                    if (outcome.Cleared)
                    {
                        done++;
                    }
                    else
                    {
                        // REV-05 — still failing (and worth retrying): to the back of the queue, so an order that fails
                        // every time cannot hold the head of every run.
                        order.DeliveryCreationPendingSince = DateTime.UtcNow;
                        await _db.SaveChangesAsync();
                    }
                }
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        if (due.Count > 0)
            _log.LogInformation("Sale order delivery sweep: {Done} of {Due} pending order(s) settled.", done, due.Count);
        return done;
    }

    private async Task<int> StopWaitingAsync(Guid organizationId, IReadOnlyList<Guid> orderUuids)
    {
        var orders = await _db.SaleOrders.IgnoreQueryFilters()
            .Where(o => o.OrganizationId == organizationId && orderUuids.Contains(o.UUID) && o.DeliveryCreationPendingSince != null)
            .ToListAsync();
        foreach (var order in orders) order.DeliveryCreationPendingSince = null;
        if (orders.Count > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogInformation(
                "Sale order delivery sweep: organization {OrganizationId} no longer creates deliveries on confirm; " +
                "{Count} pending order(s) left to the Create deliveries button.", organizationId, orders.Count);
        }
        return orders.Count;
    }
}

/// <summary>
/// A33 L-7 / BR-C1-07 — "open sale order lines" still on a route: lines of DRAFT / CONFIRMED / PARTIALLY_FULFILLED
/// orders whose stored route (the override while DRAFT, the D-16 snapshot after) is it, and which are still to be
/// delivered (not cancelled, fulfilled or invoiced). Logistics asks every <see cref="IFulfillmentRouteUsage"/> before it
/// deactivates or deletes a route.
/// </summary>
internal sealed class SaleOrderRouteUsage : IFulfillmentRouteUsage
{
    internal const string Description = "open sale order lines";

    private static readonly string[] OpenOrders =
    [
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled)
    ];

    private static readonly string[] ClosedLines =
    [
        EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled),
        EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Fulfilled),
        EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Invoiced)
    ];

    private readonly DemandDbContext _db;

    public SaleOrderRouteUsage(DemandDbContext db) => _db = db;

    public async Task<FulfillmentRouteUsageCount> CountUsageAsync(Guid organizationId, Guid routeUuid, CancellationToken ct = default)
    {
        var count = await _db.SaleOrderLines.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(l => l.OrganizationId == organizationId && l.FulfillmentRouteUuid == routeUuid
                          && !ClosedLines.Contains(l.Status)
                          && !l.SaleOrder.IsDeleted && OpenOrders.Contains(l.SaleOrder.Status), ct);
        return new FulfillmentRouteUsageCount(Description, count);
    }
}
