using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

internal sealed class SaleOrderFulfillmentService : ISaleOrderFulfillmentService
{
    private static readonly FulfillmentResult NotFound = new(false, null, false, 0m, 0m);

    private static readonly string Confirmed          = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed);
    private static readonly string PartiallyFulfilled = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled);
    private static readonly string Fulfilled          = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Fulfilled);
    private static readonly string CancelledLine      = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);

    private readonly DemandDbContext _db;
    private readonly ISaleOrderEmailService _email;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SaleOrderFulfillmentService> _log;

    public SaleOrderFulfillmentService(
        DemandDbContext db, ISaleOrderEmailService email, IBackgroundJobClient jobs,
        ILogger<SaleOrderFulfillmentService> log)
    {
        _db    = db;
        _email = email;
        _jobs  = jobs;
        _log   = log;
    }

    public async Task<FulfillmentResult> RecordDeliveryCompletedAsync(DeliveryCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        var order = await _db.SaleOrders.Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == completion.SaleOrderUuid && !x.IsDeleted);
        if (order is null)
        {
            _log.LogWarning("Delivery {Delivery} completed for sale order {So}, which does not exist here.",
                completion.DeliveryNumber, completion.SaleOrderUuid);
            return NotFound;
        }

        // A cancelled line has nothing to fulfil; counting it would hold the order open for ever.
        var open      = order.Lines.Where(l => l.Status != CancelledLine).ToList();
        var ordered   = open.Sum(l => l.Quantity);
        var fulfilled = open.Sum(l => Math.Min(l.FulfilledQty, l.Quantity));
        var complete  = open.Count > 0 && open.All(l => l.FulfilledQty >= l.Quantity);

        // Only an order still being fulfilled moves. FULFILLED and beyond are already past this
        // point; DRAFT never reserved anything; CANCELLED stays cancelled even if its goods went
        // out — the status says what the business decided, not what the warehouse did.
        if (order.Status != Confirmed && order.Status != PartiallyFulfilled)
            return new FulfillmentResult(true, order.Status, false, ordered, fulfilled);

        var target = complete ? Fulfilled : fulfilled > 0m ? PartiallyFulfilled : Confirmed;
        var becameFulfilled = target == Fulfilled;

        if (order.Status != target)
        {
            order.Status       = target;
            order.ModifiedBy   = completion.UserId;
            order.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        if (becameFulfilled)
        {
            var delivered = completion.Lines.Sum(l => l.QtyDelivered);

            // §13.3 SO_FULFILLED (SALE_ORDER → FULFILLMENT): the delivery that completed the order
            // is the target. Organization passed explicitly (§13.7) — a collection can be recorded
            // from a background path with no request claims to fall back on.
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                order.TraceId,
                new TimelineEvent(
                    SaleOrderTimelineEventTypes.SoFulfilled, "SO", order.UUID, order.SoNumber,
                    DateTime.UtcNow, completion.UserId,
                    $"Fulfilled in full — {fulfilled:0.####} of {ordered:0.####} delivered; " +
                    $"completed by delivery {completion.DeliveryNumber} ({delivered:0.####})"),
                "DELIVERY", completion.DeliveryNumber, order.OrganizationId));

            await _email.SendFulfilledAsync(order.UUID, completion.DeliveryNumber, delivered);
        }

        return new FulfillmentResult(true, target, becameFulfilled, ordered, fulfilled);
    }
}
