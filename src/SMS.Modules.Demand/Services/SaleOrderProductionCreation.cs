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

/// <summary>A34 D-12 / D-19 (REV-08) — one BOM level's manufacturing days of a variant, for the planned start.</summary>
internal interface IManufacturingLevelDays
{
    /// <summary>variant.ManufacturingLeadTimeDays ?? Product.LeadTimeDays (MANUFACTURE products) ?? 1; 1 when not the org's.</summary>
    Task<int> GetAsync(Guid organizationId, Guid variantUuid, CancellationToken ct = default);
}

/// <summary>Reads Inventory directly (Demand → Inventory is an allowed reference), the organization explicit.</summary>
internal sealed class InventoryManufacturingLevelDays : IManufacturingLevelDays
{
    private readonly SMS.Modules.Inventory.Data.InventoryDbContext _inv;

    public InventoryManufacturingLevelDays(SMS.Modules.Inventory.Data.InventoryDbContext inv) => _inv = inv;

    public async Task<int> GetAsync(Guid organizationId, Guid variantUuid, CancellationToken ct = default)
    {
        var row = await _inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Uuid == variantUuid && v.OrganizationId == organizationId)
            .Select(v => new { v.ManufacturingLeadTimeDays, v.Product.LeadTimeDays, v.Product.SupplyMethod })
            .FirstOrDefaultAsync(ct);
        if (row is null) return 1;
        var days = row.ManufacturingLeadTimeDays ?? (row.SupplyMethod == SupplyMethod.Manufacture ? row.LeadTimeDays : null) ?? 1;
        return Math.Max(0, days);
    }
}

/// <summary>
/// A34 D-17 / D-17a / D-19 — creates the make-to-order production orders of a confirmed sale order, shared by the
/// confirm (right after its commit), the sweep and the "Create production orders" button. Never throws.
/// <list type="number">
/// <item>Under the order's lock (<see cref="SaleOrderLocks.HoldsResource"/>), in its own Demand transaction: re-read the
/// order (CONFIRMED / PARTIALLY_FULFILLED, else stop), work out each make-to-order line's D-19 dates and call
/// <see cref="ISaleOrderProductionService.CreateDraftsAsync"/> (idempotent per line). No allocation demand is registered
/// here (D-17a: Material registers it at FGR). Nothing in Demand is written inside the lock.</item>
/// <item>After the lock: <see cref="ISaleOrderProductionService.PlanDraftsAsync"/> (slow, cascades).</item>
/// <item><see cref="SaleOrder.ProductionCreationPendingSince"/> is cleared when the run returned, or was refused for good
/// (400/404); an unexpected exception keeps it for the sweep. SO_PRODUCTION_FAILED goes out once per failure: on an
/// interactive run (confirm, button) or a final refusal, never on a silent sweep retry (REV-05).</item>
/// </list>
/// </summary>
internal static class SaleOrderProductionCreation
{
    /// <summary>How long the sweep leaves a pending order alone, so it never races the confirm's own call.</summary>
    internal static readonly TimeSpan SweepDelay = TimeSpan.FromMinutes(10);

    internal const string ProductionDraft = "DRAFT";

    /// <param name="Order">The order as read under the lock (tracked by the caller's context); null when not found.</param>
    /// <param name="Cleared">The order is no longer pending (success, nothing to do, or a final refusal).</param>
    internal sealed record Outcome(SaleOrder? Order, IReadOnlyList<SaleOrderProductionRef> ProductionOrders, Exception? Error, bool Cleared)
    {
        public bool Failed => Error is not null;

        public string? Message => Error switch
        {
            null => null,
            _ when IsRefusal(Error) =>
                $"Production orders could not be created or planned: {RefusalText(Error)} Use \"Create production orders\" on the order once that is put right.",
            _ when ProductionOrders.Count > 0 =>
                $"Production orders {string.Join(", ", ProductionOrders.Select(p => p.ProductionNumber))} were created but could not be " +
                "planned just now. They will be planned automatically shortly, or use \"Create production orders\" on the order.",
            _ => "Production orders could not be created just now. They will be created automatically shortly, " +
                 "or use \"Create production orders\" on the order."
        };
    }

    /// <summary>
    /// A business refusal (400/404) that retrying will not fix. Material plans every DRAFT order and rethrows an
    /// AggregateException when several failed: a refusal only when every inner error is one.
    /// </summary>
    internal static bool IsRefusal(Exception error) => error switch
    {
        BadRequestException or NotFoundException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsRefusal),
        _ => false
    };

    private static string RefusalText(Exception error) => error is AggregateException aggregate
        ? string.Join(" ", aggregate.InnerExceptions.Select(RefusalText))
        : error.Message;

    /// <summary>The order's make-to-order lines still to be made: MAKE_TO_ORDER, not cancelled, carrying the confirm snapshot.</summary>
    internal static IReadOnlyList<SaleOrderLine> MakeToOrderLines(SaleOrder order)
    {
        var mto       = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.MakeToOrder);
        var cancelled = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
        return order.Lines.Where(l => l.FulfillmentMode == mto && l.Status != cancelled && l.FulfillmentRouteUuid is not null)
            .OrderBy(l => l.Id).ToList();
    }

    internal static bool IsOpen(SaleOrder order) =>
        order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed)
     || order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled);

    /// <param name="interactive">A person asked (confirm, the button): a failure is always reported to them.</param>
    internal static async Task<Outcome> RunAsync(
        DemandDbContext db, ISaleOrderProductionService production, ILeadTimeCalculator? leadTimes, IManufacturingLevelDays? levelDays,
        IBackgroundJobClient jobs, INotificationService? notifications, ILogger log, Guid organizationId, Guid saleOrderUuid, int userId,
        bool interactive)
    {
        SaleOrder? order = null;
        IReadOnlyList<SaleOrderProductionRef> refs = [];
        Exception? error = null;
        try
        {
            refs = await SaleOrderHolds.OneChangeAtATimeAsync<IReadOnlyList<SaleOrderProductionRef>>(db, saleOrderUuid, async () =>
            {
                order = await db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
                    .FirstOrDefaultAsync(o => o.UUID == saleOrderUuid && o.OrganizationId == organizationId && !o.IsDeleted);
                if (order is null || !IsOpen(order)) return [];

                var lines = MakeToOrderLines(order);
                if (lines.Count == 0) return [];

                var requested = new List<SaleOrderProductionLine>(lines.Count);
                foreach (var line in lines)
                    requested.Add(await PlannedLineAsync(leadTimes, levelDays, log, organizationId, order, line));

                return await production.CreateDraftsAsync(organizationId,
                    new SaleOrderProductionRequest(order.UUID, order.SoNumber, order.TraceId, AllocationPriority.Normal, requested), userId);
            });

            // After the lock (BR-C5-04): planning explodes the BOM, raises material requests and chained orders.
            var drafts = refs.Where(r => r.Status == ProductionDraft).Select(r => r.ProductionOrderUuid).Distinct().ToList();
            if (drafts.Count > 0)
                await production.PlanDraftsAsync(organizationId, drafts, userId);
        }
        catch (Exception ex)
        {
            error = ex;
            log.LogWarning(ex, "Production orders for sale order {SaleOrderUuid} could not be created or planned.", saleOrderUuid);
        }

        var cleared = error is null || IsRefusal(error);
        if (order is not null && cleared && order.ProductionCreationPendingSince is not null)
        {
            order.ProductionCreationPendingSince = null;
            await db.SaveChangesAsync();
        }

        var outcome = new Outcome(order, refs, error, cleared);
        if (order is null) return outcome;

        if (error is null)
        {
            var created = refs.Where(r => r.Created).ToList();
            if (created.Count > 0)
                Timeline(jobs, order, SaleOrderTimelineEventTypes.SoProductionCreated, userId,
                    $"Production orders created: {string.Join(", ", created.Select(r => $"{r.ProductionNumber} (line {LineNumber(order, r.SoLineUuid)})"))}.");
        }
        else if (interactive || cleared)
        {
            Timeline(jobs, order, SaleOrderTimelineEventTypes.SoProductionFailed, userId, outcome.Message);
            if (notifications is not null)
                await notifications.TryCreateAsync(new NotificationRequest(
                    UserId:        userId > 0 ? userId : order.ModifiedBy ?? order.CreatedBy,
                    Type:          RouteClassificationNotificationTypes.ProductionFailed,
                    Title:         "Production orders not created",
                    Message:       $"Sale order {order.SoNumber}: {outcome.Message}",
                    Category:      "Sales",
                    EntityType:    "SO",
                    EntityUuid:    order.UUID.ToString(),
                    NavigationUrl: $"/portal/pages/sales/orders/{order.UUID}",
                    CreatedBy:     userId));
        }
        return outcome;
    }

    /// <summary>1-based by line Id, as A33's "Line N"; null when the line is not the order's.</summary>
    internal static int? LineNumber(SaleOrder order, Guid? lineUuid)
    {
        if (lineUuid is null) return null;
        var index = order.Lines.OrderBy(l => l.Id).Select(l => l.UUID).ToList().IndexOf(lineUuid.Value);
        return index < 0 ? null : index + 1;
    }

    /// <summary>
    /// D-19 — date-only. post = pick/pack + shipping (present only when the route ships) + sales buffer + QC + transfer;
    /// make = this BOM level's days (D-12, <see cref="IManufacturingLevelDays"/>) + manufacturing buffer — not the
    /// BOM-aware total, whose component waits would pull every material's need-by date to today (REV-08). With an
    /// effective date (the line's manual, else calculated, else the header's expected date): required = max(today,
    /// effective − post), start = max(today, required − make). Without one: required = today + the calculator's BOM-aware
    /// manufacturing total, start = tomorrow. Start never after required. A calculator failure gives the plain dates.
    /// </summary>
    internal static async Task<SaleOrderProductionLine> PlannedLineAsync(
        ILeadTimeCalculator? leadTimes, IManufacturingLevelDays? levelDays, ILogger log, Guid organizationId, SaleOrder order, SaleOrderLine line)
    {
        var today = DateTime.UtcNow.Date;
        int manufacturing = 0, buffer = 0, post = 0;
        if (leadTimes is not null)
        {
            try
            {
                var result = await leadTimes.CalculateAsync(organizationId,
                    new LeadTimeRequest(line.VariantUuid, line.Quantity, line.FulfillmentRouteUuid));
                int Days(string code) => result.Components.Where(c => c.Code == code).Sum(c => Math.Max(0, c.Days));
                manufacturing = Days(LeadTimeComponentCode.Manufacturing);
                buffer = Days(LeadTimeComponentCode.MfgBuffer);
                post = Days(LeadTimeComponentCode.PickPack) + Days(LeadTimeComponentCode.Shipping) + Days(LeadTimeComponentCode.SalesBuffer)
                     + Days(LeadTimeComponentCode.Qc) + Days(LeadTimeComponentCode.Transfer);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Lead time for sale order {SoNumber} line {LineUuid} could not be calculated; planning with plain dates.",
                    order.SoNumber, line.UUID);
            }
        }

        DateTime required, start;
        var effective = (line.ManualDeliveryDate ?? line.CalculatedDeliveryDate ?? order.ExpectedDeliveryDate)?.Date;
        if (effective is { } date)
        {
            var level = levelDays is null ? 1 : await LevelDaysAsync(levelDays, log, organizationId, line);
            required = Max(today, date.AddDays(-post));
            start    = Max(today, required.AddDays(-(level + buffer)));
        }
        else
        {
            required = today.AddDays(manufacturing);
            start    = today.AddDays(1);
        }
        if (start > required) start = required;

        return new SaleOrderProductionLine(line.UUID, line.VariantUuid, line.Quantity, line.FulfillmentRouteUuid!.Value, required, start);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static async Task<int> LevelDaysAsync(IManufacturingLevelDays levelDays, ILogger log, Guid organizationId, SaleOrderLine line)
    {
        try
        {
            return await levelDays.GetAsync(organizationId, line.VariantUuid);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Manufacturing days of variant {VariantUuid} could not be read; one day assumed (D-12).", line.VariantUuid);
            return 1;
        }
    }

    internal static void Timeline(IBackgroundJobClient jobs, SaleOrder order, string type, int userId, string? note)
    {
        var (traceId, uuid, number) = (order.TraceId, order.UUID, order.SoNumber);
        jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            traceId, new TimelineEvent(type, "SO", uuid, number, DateTime.UtcNow, userId, note), "SO", number));
    }
}

/// <summary>
/// A34 D-17 (REV-05) — creates the production orders of confirmed sale orders whose post-confirm creation never finished:
/// only orders still carrying <see cref="SaleOrder.ProductionCreationPendingSince"/>, at least
/// <see cref="SaleOrderProductionCreation.SweepDelay"/> old, CONFIRMED / PARTIALLY_FULFILLED. An organization that no
/// longer has manufacturing stops waiting (only the button creates then). A retry that fails again goes to the back of the
/// queue, silently. The A33 delivery sweep is untouched.
/// <para>No user and no ambient tenant: every read filters on the organization explicitly, under a
/// <see cref="HangfireTenantScope"/> per organization.</para>
/// </summary>
internal sealed class SaleOrderProductionSweepJob
{
    internal const string RecurringJobId = "sale-order-production-creation-sweep";
    internal const string Cron = "*/15 * * * *";
    internal const int SystemUserId = 0;

    /// <summary>Orders per run; the rest wait for the next one (oldest first). Settable for tests.</summary>
    internal int BatchSize { get; set; } = 200;

    private readonly DemandDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SaleOrderProductionSweepJob> _log;
    private readonly ISaleOrderProductionService? _production;
    private readonly ILeadTimeCalculator? _leadTimes;
    private readonly ITenantSnapshotProvider? _tenants;
    private readonly INotificationService? _notifications;
    private readonly IManufacturingLevelDays? _levelDays;

    public SaleOrderProductionSweepJob(
        DemandDbContext db, IBackgroundJobClient jobs, ILogger<SaleOrderProductionSweepJob> log,
        ISaleOrderProductionService? production = null, ILeadTimeCalculator? leadTimes = null,
        ITenantSnapshotProvider? tenants = null, INotificationService? notifications = null,
        IManufacturingLevelDays? levelDays = null)
    {
        _levelDays     = levelDays;
        _db            = db;
        _jobs          = jobs;
        _log           = log;
        _production    = production;
        _leadTimes     = leadTimes;
        _tenants       = tenants;
        _notifications = notifications;
    }

    /// <returns>How many orders are no longer pending after this run.</returns>
    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync()
    {
        var cutoff    = DateTime.UtcNow - SaleOrderProductionCreation.SweepDelay;
        var confirmed = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed);
        var partial   = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled);

        var due = await _db.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.ProductionCreationPendingSince != null && o.ProductionCreationPendingSince <= cutoff && !o.IsDeleted
                     && (o.Status == confirmed || o.Status == partial))
            .OrderBy(o => o.ProductionCreationPendingSince)
            .Select(o => new { o.OrganizationId, o.UUID })
            .Take(BatchSize)
            .ToListAsync();

        var done = 0;
        foreach (var org in due.GroupBy(o => o.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                if (_production is null || !await ManufacturingEnabledAsync(org.Key))
                {
                    done += await StopWaitingAsync(org.Key, org.Select(o => o.UUID).ToList());
                    continue;
                }

                foreach (var item in org)
                {
                    var outcome = await SaleOrderProductionCreation.RunAsync(
                        _db, _production, _leadTimes, _levelDays, _jobs, _notifications, _log, org.Key, item.UUID, SystemUserId, interactive: false);
                    if (outcome.Cleared)
                    {
                        done++;
                    }
                    else if (outcome.Order is { } order)
                    {
                        // Still failing, and worth retrying: to the back of the queue (A33 REV-05).
                        order.ProductionCreationPendingSince = DateTime.UtcNow;
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
            _log.LogInformation("Sale order production sweep: {Done} of {Due} pending order(s) settled.", done, due.Count);
        return done;
    }

    private async Task<bool> ManufacturingEnabledAsync(Guid organizationId)
    {
        if (_tenants is null) return true;
        var tenant = await _tenants.GetSnapshotAsync(organizationId);
        return tenant is not null && tenant.EnabledFeatureCodes.Contains(EffectiveRouteResolver.ManufacturingFeature);
    }

    private async Task<int> StopWaitingAsync(Guid organizationId, IReadOnlyList<Guid> orderUuids)
    {
        var orders = await _db.SaleOrders.IgnoreQueryFilters()
            .Where(o => o.OrganizationId == organizationId && orderUuids.Contains(o.UUID) && o.ProductionCreationPendingSince != null)
            .ToListAsync();
        foreach (var order in orders) order.ProductionCreationPendingSince = null;
        if (orders.Count > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogInformation(
                "Sale order production sweep: organization {OrganizationId} no longer has manufacturing; " +
                "{Count} pending order(s) left to the Create production orders button.", organizationId, orders.Count);
        }
        return orders.Count;
    }
}

/// <summary>
/// A34 PE-05 Demand side (D-21, D-21a, D-23) — a make-to-order production outcome, recorded on the sale order line.
/// <list type="bullet">
/// <item>A delivery created from production (DeliveryNumber set): SO_DELIVERY_FROM_PRODUCTION on the timeline and to the
/// SO creator.</item>
/// <item>A final outcome (Completed, or ZeroYield at QI): <see cref="SaleOrderLine.ProductionShortfallQty"/> is SET to
/// planned − accepted (the line quantity on zero yield; null when nothing is short), and when that value changed,
/// SO_PRODUCTION_SHORTFALL on the timeline and PROD_SHORTFALL / PROD_ZERO_YIELD to the SO creator. Material notifies the
/// PO side.</item>
/// </list>
/// A replay changes and sends nothing. Missing / other-organization orders and lines are a no-op. Called with no
/// transaction or lock held.
/// </summary>
internal sealed class SaleOrderProductionFeedback : ISaleOrderProductionFeedback
{
    private readonly DemandDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SaleOrderProductionFeedback> _log;
    private readonly INotificationService? _notifications;

    public SaleOrderProductionFeedback(
        DemandDbContext db, IBackgroundJobClient jobs, ILogger<SaleOrderProductionFeedback> log, INotificationService? notifications = null)
    {
        _db            = db;
        _jobs          = jobs;
        _log           = log;
        _notifications = notifications;
    }

    public async Task RecordOutcomeAsync(Guid organizationId, ProductionOutcome outcome, int userId, CancellationToken ct = default)
    {
        var order = await _db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.UUID == outcome.SaleOrderUuid && o.OrganizationId == organizationId && !o.IsDeleted, ct);
        var line = order?.Lines.FirstOrDefault(l => l.UUID == outcome.SoLineUuid);
        if (order is null || line is null)
        {
            _log.LogInformation("Production outcome of {ProductionNumber}: sale order {SaleOrderUuid} line {LineUuid} not found in organization {OrganizationId}.",
                outcome.ProductionNumber, outcome.SaleOrderUuid, outcome.SoLineUuid, organizationId);
            return;
        }

        var lineNumber = SaleOrderProductionCreation.LineNumber(order, line.UUID);
        var url = $"/portal/pages/sales/orders/{order.UUID}";

        if (!string.IsNullOrWhiteSpace(outcome.DeliveryNumber))
        {
            var note = $"Delivery {outcome.DeliveryNumber} created from production order {outcome.ProductionNumber} for line {lineNumber} " +
                       $"({outcome.AcceptedQuantity:0.####} of {outcome.PlannedQuantity:0.####} accepted so far).";
            SaleOrderProductionCreation.Timeline(_jobs, order, SaleOrderTimelineEventTypes.SoDeliveryFromProduction, userId, note);
            await NotifyAsync(order.CreatedBy, RouteClassificationNotificationTypes.DeliveryFromProduction,
                "Delivery from production", $"Sale order {order.SoNumber}: {note}", order, url, userId);
        }

        // D-21a — only a final outcome sets the shortfall; set, never added to.
        if (!outcome.Completed && !outcome.ZeroYield) return;

        decimal? shortfall = outcome.ZeroYield
            ? line.Quantity
            : outcome.PlannedQuantity - outcome.AcceptedQuantity is var gap && gap > 0m ? gap : null;
        if (line.ProductionShortfallQty == shortfall) return;

        line.ProductionShortfallQty = shortfall;
        await _db.SaveChangesAsync(ct);
        if (shortfall is null) return;

        var shortNote = outcome.ZeroYield
            ? $"Production order {outcome.ProductionNumber}: quality inspection accepted nothing, so line {lineNumber} is short by {shortfall:0.####}."
            : $"Production order {outcome.ProductionNumber} accepted {outcome.AcceptedQuantity:0.####} of {outcome.PlannedQuantity:0.####}: " +
              $"line {lineNumber} is short by {shortfall:0.####}.";
        SaleOrderProductionCreation.Timeline(_jobs, order, SaleOrderTimelineEventTypes.SoProductionShortfall, userId, shortNote);
        await NotifyAsync(order.CreatedBy,
            outcome.ZeroYield ? RouteClassificationNotificationTypes.ProductionZeroYield : RouteClassificationNotificationTypes.ProductionShortfall,
            outcome.ZeroYield ? "Production yielded nothing" : "Production shortfall",
            $"Sale order {order.SoNumber}: {shortNote}", order, url, userId);
    }

    private Task NotifyAsync(int recipient, string type, string title, string message, SaleOrder order, string url, int userId) =>
        _notifications is null || recipient <= 0
            ? Task.CompletedTask
            : _notifications.TryCreateAsync(new NotificationRequest(
                UserId: recipient, Type: type, Title: title, Message: message, Category: "Sales",
                EntityType: "SO", EntityUuid: order.UUID.ToString(), NavigationUrl: url, CreatedBy: userId));
}
