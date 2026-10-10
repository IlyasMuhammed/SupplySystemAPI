using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

// A36 (DEM) — service lines on sale orders (D-10, D-11). Contract: docs/service-orders/API-CONTRACT.md §4.
// A line whose product is SERVICE carries FulfillmentMode SERVICE: route-exempt, never reserved/allocated, no deficit job,
// no delivery. After confirm each live service line gets a service order through IServiceOrderDemandService (Material);
// Material tells ISaleOrderServiceFulfillmentListener when one completes, closes or is cancelled.

/// <summary>A36 D-10 — which variants are services (their product's ProductType is SERVICE), for the given organization.</summary>
internal interface IServiceVariantClassifier
{
    Task<IReadOnlySet<Guid>> ServiceVariantsAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default);
}

/// <summary>Reads Inventory directly (Demand → Inventory is an allowed reference), the organization explicit.</summary>
internal sealed class InventoryServiceVariantClassifier : IServiceVariantClassifier
{
    private readonly SMS.Modules.Inventory.Data.InventoryDbContext _inv;

    public InventoryServiceVariantClassifier(SMS.Modules.Inventory.Data.InventoryDbContext inv) => _inv = inv;

    public async Task<IReadOnlySet<Guid>> ServiceVariantsAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        if (variantUuids.Count == 0) return new HashSet<Guid>();
        var ids = variantUuids.Distinct().ToList();
        var found = await _inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => ids.Contains(v.Uuid) && v.OrganizationId == organizationId && v.Product.ProductType == ProductType.Service)
            .Select(v => v.Uuid)
            .ToListAsync(ct);
        return found.ToHashSet();
    }
}

internal static class SaleOrderServiceLines
{
    /// <summary>D-11 — without it, confirm raises no service orders (service lines stay exempt from delivery).</summary>
    internal const string ServicesFeature = "MODULE_SERVICES";

    /// <summary>Service orders that count towards a line's fulfilment (D-10).</summary>
    internal static readonly string[] DoneStatuses = ["COMPLETED", "CLOSED"];

    internal static readonly string Code = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.Service);

    internal static bool IsService(SaleOrderLine line) => line.FulfillmentMode == Code;

    /// <summary>Service lines still to be performed: SERVICE, not cancelled.</summary>
    internal static IReadOnlyList<SaleOrderLine> Live(SaleOrder order)
    {
        var cancelled = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
        return order.Lines.Where(l => IsService(l) && l.Status != cancelled).OrderBy(l => l.Id).ToList();
    }

    /// <summary>
    /// Marks each line SERVICE when its variant is a service, and puts a line marked SERVICE whose product no longer is one
    /// back to <paramref name="defaultMode"/>. Without a classifier nothing changes (unit hosts, no Inventory).
    /// </summary>
    internal static async Task MarkAsync(IServiceVariantClassifier? classifier, Guid organizationId, IEnumerable<SaleOrderLine> lines, string? defaultMode)
    {
        if (classifier is null) return;
        var list = lines.ToList();
        if (list.Count == 0) return;
        var services = await classifier.ServiceVariantsAsync(organizationId, list.Select(l => l.VariantUuid).Distinct().ToList());
        foreach (var line in list)
        {
            if (services.Contains(line.VariantUuid)) line.FulfillmentMode = Code;
            else if (IsService(line)) line.FulfillmentMode = defaultMode;
        }
    }

    /// <param name="Created">Service orders this run raised (a line that already had one is not asked again).</param>
    /// <param name="Error">The first failure, when some line could not get its service order; the rest were still tried.</param>
    internal sealed record Outcome(SaleOrder? Order, IReadOnlyList<SaleOrderServiceOrderRef> Created, Exception? Error)
    {
        public bool Failed => Error is not null;

        public string? Message => Error is null
            ? null
            : "Service orders could not be created for every service line just now. They are retried the next time the order is opened.";
    }

    /// <summary>
    /// Raises the service orders of a confirmed order's live service lines that have none yet (any status: a service
    /// order cancelled by hand in Services is not raised again behind anyone's back). Under the order's lock, like
    /// production creation, so it cannot race the cancel cascade; Material's Ensure is idempotent per (order, line).
    /// The scheduled date is the line's effective delivery date (manual ?? calculated ?? the header's expected date); a
    /// sale order has no warehouse, so Material picks its default. Never throws.
    /// </summary>
    /// <param name="interactive">Confirm: a failure goes on the timeline. A retry from the detail load stays silent.</param>
    internal static async Task<Outcome> RunAsync(
        DemandDbContext db, IServiceOrderDemandService services, IBackgroundJobClient jobs, ILogger log,
        Guid organizationId, Guid saleOrderUuid, int userId, bool interactive)
    {
        SaleOrder? order = null;
        var created = new List<SaleOrderServiceOrderRef>();
        Exception? error = null;
        try
        {
            await SaleOrderHolds.OneChangeAtATimeAsync<bool>(db, saleOrderUuid, async () =>
            {
                order = await db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
                    .FirstOrDefaultAsync(o => o.UUID == saleOrderUuid && o.OrganizationId == organizationId && !o.IsDeleted);
                if (order is null || !SaleOrderProductionCreation.IsOpen(order)) return false;

                var lines = Live(order);
                if (lines.Count == 0) return false;

                var existing = await services.GetForSaleOrderAsync(organizationId, order.UUID);
                foreach (var line in lines.Where(l => !existing.Any(r => r.SoLineUuid == l.UUID)))
                {
                    try
                    {
                        var date = (line.ManualDeliveryDate ?? line.CalculatedDeliveryDate ?? order.ExpectedDeliveryDate)?.Date;
                        created.Add(await services.EnsureForSaleOrderLineAsync(
                            order.UUID, line.UUID, order.SoNumber, order.PartnerId, line.VariantUuid, line.Quantity,
                            date, warehouseUuid: null, userId, order.TraceId));
                    }
                    catch (Exception ex)
                    {
                        error ??= ex;
                        log.LogWarning(ex, "Service order for sale order {SoNumber} line {LineUuid} could not be created.", order.SoNumber, line.UUID);
                    }
                }
                return true;
            });
        }
        catch (Exception ex)
        {
            error ??= ex;
            log.LogWarning(ex, "Service orders for sale order {SaleOrderUuid} could not be created.", saleOrderUuid);
        }

        if (order is not null)
        {
            if (created.Count > 0)
                SaleOrderProductionCreation.Timeline(jobs, order, SaleOrderTimelineEventTypes.SoServiceOrdersCreated, userId,
                    $"Service orders created: {string.Join(", ", created.Select(r => $"{r.ServiceNumber} (line {SaleOrderProductionCreation.LineNumber(order, r.SoLineUuid)})"))}.");
            if (error is not null && interactive)
                SaleOrderProductionCreation.Timeline(jobs, order, SaleOrderTimelineEventTypes.SoServiceOrdersFailed, userId,
                    $"{error.Message} — " + new Outcome(order, created, error).Message);
        }
        return new Outcome(order, created, error);
    }

    internal static string CancelledNote(SaleOrderServiceCancellation services)
    {
        var parts = new List<string>();
        if (services.Cancelled.Count > 0)
            parts.Add($"Service orders cancelled: {string.Join(", ", services.Cancelled.Select(s => s.ServiceNumber))}.");
        if (services.KeptRunning.Count > 0)
            parts.Add("Already started or finished, left as they are (cancel them in Services if they should stop): " +
                      $"{string.Join(", ", services.KeptRunning.Select(s => $"{s.ServiceNumber} ({s.Status})"))}.");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// A36 D-10 (P5-06 / P5-08) — Material calls this when a sale-order-sourced service order completes, closes or is
/// cancelled. Each service line of the order is recounted: FulfilledQty is SET to min(quantity, Σ quantity of its
/// COMPLETED/CLOSED service orders) and its status follows (FULFILLED / PARTIALLY_FULFILLED); then the order's status is
/// recomputed over every live line exactly as a completed delivery does (<see cref="SaleOrderFulfillmentService"/>), so a
/// mixed order is FULFILLED only once its deliveries and its services are both done, and a service-only order reaches
/// FULFILLED on its own. A replay changes nothing. Missing / other-organization orders are a no-op.
/// <para>Material's service is resolved at call time, not injected: Material's implementation receives this listener,
/// and a constructor dependency both ways would be a DI cycle.</para>
/// </summary>
internal sealed class SaleOrderServiceFulfillmentListener : ISaleOrderServiceFulfillmentListener
{
    private static readonly string Confirmed          = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed);
    private static readonly string PartiallyFulfilled = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled);
    private static readonly string Fulfilled          = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Fulfilled);
    private static readonly string CancelledLine      = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
    private static readonly string FulfilledLine      = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Fulfilled);
    private static readonly string PartialLine        = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.PartiallyFulfilled);
    private static readonly string InvoicedLine       = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Invoiced);

    private readonly DemandDbContext _db;
    private readonly IServiceProvider _provider;
    private readonly IBackgroundJobClient _jobs;
    private readonly ISaleOrderEmailService _email;
    private readonly ILogger<SaleOrderServiceFulfillmentListener> _log;

    public SaleOrderServiceFulfillmentListener(
        DemandDbContext db, IServiceProvider provider, IBackgroundJobClient jobs, ISaleOrderEmailService email,
        ILogger<SaleOrderServiceFulfillmentListener> log)
    {
        _db       = db;
        _provider = provider;
        _jobs     = jobs;
        _email    = email;
        _log      = log;
    }

    public async Task OnServiceOrderChangedAsync(
        Guid organizationId, Guid saleOrderUuid, Guid saleOrderLineUuid, int userId, CancellationToken ct = default)
    {
        var services = _provider.GetService<IServiceOrderDemandService>();
        if (services is null) return;

        var order = await _db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.UUID == saleOrderUuid && o.OrganizationId == organizationId && !o.IsDeleted, ct);
        if (order is null)
        {
            _log.LogInformation("Service order change for sale order {SaleOrderUuid}: not found in organization {OrganizationId}.",
                saleOrderUuid, organizationId);
            return;
        }

        var refs = await services.GetForSaleOrderAsync(organizationId, saleOrderUuid, ct);
        var changedLines = new List<(SaleOrderLine Line, decimal Before)>();
        foreach (var line in SaleOrderServiceLines.Live(order))
        {
            if (line.Status == InvoicedLine) continue;
            var done = refs.Where(r => r.SoLineUuid == line.UUID && SaleOrderServiceLines.DoneStatuses.Contains(r.Status)).Sum(r => r.Quantity);
            var fulfilled = Math.Min(line.Quantity, done);
            if (fulfilled == line.FulfilledQty) continue;

            changedLines.Add((line, line.FulfilledQty));
            line.FulfilledQty = fulfilled;
            line.Status = fulfilled >= line.Quantity ? FulfilledLine
                        : fulfilled > 0m ? PartialLine
                        : EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open);
        }

        // The order moves only while it is still being fulfilled, as for a delivery.
        var open       = order.Lines.Where(l => l.Status != CancelledLine).ToList();
        var ordered    = open.Sum(l => l.Quantity);
        var fulfilledQ = open.Sum(l => Math.Min(l.FulfilledQty, l.Quantity));
        var complete   = open.Count > 0 && open.All(l => l.FulfilledQty >= l.Quantity);
        var becameFulfilled = false;
        if (order.Status == Confirmed || order.Status == PartiallyFulfilled)
        {
            var target = complete ? Fulfilled : fulfilledQ > 0m ? PartiallyFulfilled : Confirmed;
            if (order.Status != target)
            {
                becameFulfilled    = target == Fulfilled;
                order.Status       = target;
                order.ModifiedBy   = userId;
                order.ModifiedDate = DateTime.UtcNow;
            }
        }

        if (changedLines.Count == 0 && !_db.ChangeTracker.HasChanges()) return;
        await _db.SaveChangesAsync(ct);

        var trigger = refs.Where(r => r.SoLineUuid == saleOrderLineUuid && SaleOrderServiceLines.DoneStatuses.Contains(r.Status))
            .Select(r => r.ServiceNumber).LastOrDefault() ?? "a service order";
        foreach (var (line, before) in changedLines)
        {
            var note = $"Line {SaleOrderProductionCreation.LineNumber(order, line.UUID)}: {line.FulfilledQty:0.####} of {line.Quantity:0.####} " +
                       $"performed by service order (was {before:0.####}).";
            SaleOrderProductionCreation.Timeline(_jobs, order, SaleOrderTimelineEventTypes.SoServiceCompleted, userId, note);
        }

        if (becameFulfilled)
        {
            var (traceId, uuid, number, org) = (order.TraceId, order.UUID, order.SoNumber, order.OrganizationId);
            var note = $"Fulfilled in full — {fulfilledQ:0.####} of {ordered:0.####} fulfilled; completed by service order {trigger}";
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                traceId,
                new TimelineEvent(SaleOrderTimelineEventTypes.SoFulfilled, "SO", uuid, number, DateTime.UtcNow, userId, note),
                "SO", number, org));
            await _email.SendFulfilledAsync(order.UUID, trigger, changedLines.Sum(c => c.Line.FulfilledQty - c.Before));
        }
    }
}
