using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Services;

/// <param name="Ran">Logistics' creator was called and returned (whatever it decided).</param>
/// <param name="QuantityCreated">Put on deliveries by this call; 0 on a replay.</param>
/// <param name="DeliveryUuid">The delivery this call created (the first, when split across held warehouses).</param>
/// <param name="LatestDeliveryUuid">The latest non-cancelled delivery made from the order, now or earlier — what was stamped.</param>
/// <param name="SkippedReason">Why nothing (or less) was created, or why the handoff did not run / failed.</param>
/// <param name="Failed">An unexpected failure: nothing was stamped and a pending order stays pending for the sweep.</param>
public sealed record ProductionDeliveryHandoffResult(
    bool    Ran,
    decimal QuantityCreated,
    Guid?   DeliveryUuid,
    string? DeliveryNumber,
    Guid?   LatestDeliveryUuid,
    string? LatestDeliveryNumber,
    string? SkippedReason,
    bool    Failed);

/// <summary>
/// A34 C6 (D-20, D-21, analysis §4.7, API-CONTRACT §7/§8.4) — a make-to-order production order's accepted output → its
/// DRAFT delivery, through Logistics' <see cref="IProductionDeliveryCreator"/> (Material cannot reference Logistics).
/// </summary>
public interface IProductionDeliveryHandoff
{
    /// <summary>
    /// The FGR hook (once the order turns COMPLETED) and the sweep. Never throws. Must be called with <b>no</b>
    /// transaction or lock held: the creator takes the sale order's lock on its own connection (A30 P4-21 bug-3).
    /// </summary>
    Task<ProductionDeliveryHandoffResult> RunAsync(Guid productionOrderUuid, int userId, CancellationToken ct = default);

    /// <summary>
    /// "Create delivery now": the caller's own organization only (404, super admin included); 400 unless made to order
    /// with accepted quantity; 409 on an unexpected failure. A repeat returns with nothing created.
    /// </summary>
    Task<ProductionDeliveryHandoffModel> CreateNowAsync(Guid productionOrderUuid, int userId, CancellationToken ct = default);
}

/// <summary>
/// The one place Material hands a production order's output to Logistics:
/// <list type="number">
/// <item>Loads the order by uuid and works in <b>its</b> organization (R-11: a super admin may confirm another
/// organization's FGR; the sweep has no tenant at all).</item>
/// <item>Calls the creator with the accepted quantity, the route, the sale order line and the fallback warehouse
/// (output, else production).</item>
/// <item>Stamps <c>DeliveryOrderUuid/Number</c> from <c>LatestDelivery</c> (so a replay repairs a lost reply) and clears
/// <c>DeliveryCreationPendingSince</c> on any returned result (REV-05).</item>
/// <item>Reports the outcome to Demand with <c>Completed</c> = the order is COMPLETED (REV-02), and the delivery number only
/// when this call created one.</item>
/// <item><c>PROD_SHORTFALL</c> to the order's creator once: at COMPLETED, planned &gt; accepted, and only by the run that
/// clears the flag FGR set in its completing commit (so a later "Create delivery now" never repeats it).</item>
/// <item><c>PROD_DELIVERY_CREATED</c> on the order's timeline when a delivery was created.</item>
/// </list>
/// </summary>
internal sealed class ProductionDeliveryHandoff : IProductionDeliveryHandoff
{
    private readonly MaterialDbContext             _db;
    private readonly ITenantContext                _tenant;
    private readonly IProductionDeliveryCreator?   _creator;
    private readonly ISaleOrderProductionFeedback? _feedback;
    private readonly INotificationService?         _notifications;
    private readonly IBackgroundJobClient?         _jobs;
    private readonly ILogger                       _log;

    public ProductionDeliveryHandoff(
        MaterialDbContext db, ITenantContext tenant, IProductionDeliveryCreator? creator = null,
        ISaleOrderProductionFeedback? feedback = null, INotificationService? notifications = null,
        IBackgroundJobClient? jobs = null, ILogger<ProductionDeliveryHandoff>? log = null)
    {
        _db            = db;
        _tenant        = tenant;
        _creator       = creator;
        _feedback      = feedback;
        _notifications = notifications;
        _jobs          = jobs;
        _log           = log ?? (ILogger)NullLogger.Instance;
    }

    private static ProductionDeliveryHandoffResult NotRun(string reason) => new(false, 0m, null, null, null, null, reason, false);
    private static ProductionDeliveryHandoffResult Failure(string reason) => new(false, 0m, null, null, null, null, reason, true);

    public async Task<ProductionDeliveryHandoffResult> RunAsync(Guid productionOrderUuid, int userId, CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(productionOrderUuid, userId, ct);
        }
        catch (Exception ex)
        {
            // Never throws (the FGR has already committed): the order stays pending for the sweep.
            _log.LogError(ex, "A34: the delivery hand-off for production order {ProductionOrderUuid} failed.", productionOrderUuid);
            return Failure(ex.Message);
        }
    }

    private async Task<ProductionDeliveryHandoffResult> RunCoreAsync(Guid uuid, int userId, CancellationToken ct)
    {
        // A30 P4-21 bug-3: never call another module from inside a transaction. The creator takes the sale order's
        // application lock on Logistics' connection; a transaction held here could deadlock with it.
        if (_db.Database.CurrentTransaction is not null)
        {
            _log.LogError("A34: the delivery hand-off for {ProductionOrderUuid} was called inside an open transaction; refused.", uuid);
            return Failure("The delivery hand-off was called inside an open transaction, so it did not run. It will be retried.");
        }

        var po = await _db.ProductionOrders.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.UUID == uuid, ct);
        if (po is null) return NotRun("Production order not found.");

        string? cannot = !po.IsMakeToOrder
            ? $"{po.ProductionNumber} is not made to order for a sale order, so no delivery is created from it."
            : po.Status == ProductionOrderStatus.Cancelled
                ? $"{po.ProductionNumber} is cancelled."
                : po.AcceptedQuantity <= 0
                    ? $"{po.ProductionNumber} has no accepted quantity yet."
                    : _creator is null
                        ? "Deliveries are not available (no Logistics module)."
                        : null;
        if (cannot is not null)
        {
            // Nothing will ever change that by itself: stop the sweep waiting on it.
            if (po.DeliveryCreationPendingSince is not null)
            {
                po.DeliveryCreationPendingSince = null;
                po.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            return NotRun(cannot);
        }

        var pendingAtStart = po.DeliveryCreationPendingSince is not null;
        var request = new ProductionDeliveryRequest(
            po.UUID, po.ProductionNumber, po.SourceUuid!.Value, po.SourceLineUuid!.Value, po.FulfillmentRouteUuid!.Value,
            po.AcceptedQuantity, po.OutputWarehouseUuid ?? po.WarehouseUuid);

        ProductionDeliveryResult result;
        try
        {
            result = await _creator!.CreateForProductionOrderAsync(po.OrganizationId, request, userId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A34: no delivery could be created from {ProductionNumber} ({ProductionOrderUuid}); left pending.",
                po.ProductionNumber, po.UUID);
            return Failure(ex.Message);
        }

        // Stamp and clear. If this save fails the delivery exists anyway: a replay (sweep / button) stamps it then.
        try
        {
            if (result.LatestDelivery is { } latest)
            {
                po.DeliveryOrderUuid = latest.DeliveryUuid;
                po.DeliveryNumber    = latest.DeliveryNumber;
            }
            po.DeliveryCreationPendingSince = null;
            po.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A34: {ProductionNumber} could not be stamped with its delivery; left pending.", po.ProductionNumber);
            try { await _db.Entry(po).ReloadAsync(ct); } catch { /* the sweep re-reads anyway */ }
            return Failure(ex.Message);
        }

        var created   = result.QuantityCreated > 0 ? result.Delivery : null;
        var completed = po.Status == ProductionOrderStatus.Completed;

        await ReportAsync(po, created?.DeliveryNumber, completed, userId, ct);

        if (completed && pendingAtStart && po.PlannedQuantity > po.AcceptedQuantity && _notifications is not null)
            await _notifications.TryCreateAsync(new NotificationRequest(
                UserId: po.CreatedBy, Type: RouteClassificationNotificationTypes.ProductionShortfall, Title: "Production Shortfall",
                Message: $"{po.ProductionNumber} completed with {po.AcceptedQuantity:0.####} of {po.PlannedQuantity:0.####} accepted; " +
                         $"sale order {po.SourceReference} is short by {po.PlannedQuantity - po.AcceptedQuantity:0.####}.",
                Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: userId));

        if (created is not null)
        {
            var numbers = (result.Deliveries is { Count: > 0 } all ? all : [created]).Select(d => d.DeliveryNumber);
            var note = $"Delivery {string.Join(", ", numbers)} created for {result.QuantityCreated:0.####} " +
                       $"to sale order {po.SourceReference}." + (result.SkippedReason is null ? "" : $" {result.SkippedReason}");
            _jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                po.TraceId,
                new TimelineEvent(ManufacturingTimelineEventTypes.ProdDeliveryCreated, ManufacturingInterfaceCodes.ProductionOrder,
                    po.UUID, po.ProductionNumber, DateTime.UtcNow, userId, note),
                ManufacturingInterfaceCodes.ProductionOrder, po.ProductionNumber));
        }

        return new ProductionDeliveryHandoffResult(
            true, result.QuantityCreated, created?.DeliveryUuid, created?.DeliveryNumber,
            result.LatestDelivery?.DeliveryUuid, result.LatestDelivery?.DeliveryNumber, result.SkippedReason, false);
    }

    /// <summary>Demand's side (D-21): shortfall, timeline, notifications. Idempotent there; a failure here is logged only.</summary>
    private async Task ReportAsync(ProductionOrder po, string? deliveryNumber, bool completed, int userId, CancellationToken ct)
    {
        if (_feedback is null) return;
        try
        {
            await _feedback.RecordOutcomeAsync(po.OrganizationId, new ProductionOutcome(
                po.SourceUuid!.Value, po.SourceLineUuid!.Value, po.UUID, po.ProductionNumber, po.PlannedQuantity,
                po.AcceptedQuantity, deliveryNumber, ZeroYield: false, Completed: completed), userId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A34: the outcome of {ProductionNumber} could not be recorded on its sale order.", po.ProductionNumber);
        }
    }

    public async Task<ProductionDeliveryHandoffModel> CreateNowAsync(Guid productionOrderUuid, int userId, CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        var po = await _db.ProductionOrders.IgnoreQueryFilters().AsNoTracking()
                     .FirstOrDefaultAsync(p => p.UUID == productionOrderUuid && p.OrganizationId == org, ct)
                 ?? throw new NotFoundException("Production order", productionOrderUuid);

        if (!po.IsMakeToOrder)
            throw new BadRequestException($"{po.ProductionNumber} is not made to order for a sale order, so no delivery is created from it.");
        if (po.AcceptedQuantity <= 0)
            throw new BadRequestException($"{po.ProductionNumber} has no accepted quantity yet.");

        var result = await RunAsync(po.UUID, userId, ct);
        if (result.Failed || !result.Ran)
            throw new ConflictException(
                $"The delivery from {po.ProductionNumber} could not be created: {result.SkippedReason} Try again in a moment.");

        return new ProductionDeliveryHandoffModel
        {
            ProductionOrderUuid  = po.UUID,
            QuantityCreated      = result.QuantityCreated,
            DeliveryUuid         = result.DeliveryUuid,
            DeliveryNumber       = result.DeliveryNumber,
            LatestDeliveryUuid   = result.LatestDeliveryUuid,
            LatestDeliveryNumber = result.LatestDeliveryNumber,
            SkippedReason        = result.SkippedReason
        };
    }
}
