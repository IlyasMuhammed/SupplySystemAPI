using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

internal sealed class AutoPurchaseOrderService : IAutoPurchaseOrderService
{
    // A PO in either of these is dead: it will never be received against, so a new one may be raised
    // for the same line. Everything else — including a DRAFT nobody has touched yet — is live.
    private static readonly string[] DeadStatuses = ["CANCELLED", "REJECTED"];

    private readonly DemandDbContext _db;
    private readonly IPurchaseOrderRepository _repo;
    private readonly IPurchaseOrderService _purchaseOrders;
    private readonly ISaleOrderConfigService _config;
    private readonly ISupplierNameLookupService _supplierNames;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<AutoPurchaseOrderService> _log;

    public AutoPurchaseOrderService(
        DemandDbContext db, IPurchaseOrderRepository repo, IPurchaseOrderService purchaseOrders,
        ISaleOrderConfigService config, ISupplierNameLookupService supplierNames,
        IBackgroundJobClient jobs, ILogger<AutoPurchaseOrderService> log)
    {
        _db             = db;
        _repo           = repo;
        _purchaseOrders = purchaseOrders;
        _config         = config;
        _supplierNames  = supplierNames;
        _jobs           = jobs;
        _log            = log;
    }

    public async Task<AutoPurchaseOrderResult> CreateFromSODeficitAsync(
        Guid saleOrderLineUuid, Guid supplierId, decimal qty, decimal price, int userId)
    {
        if (qty <= 0)
            throw new BadRequestException("The quantity to purchase must be greater than zero.");
        if (price < 0)
            throw new BadRequestException("The purchase price must not be negative.");

        var line = await _db.SaleOrderLines.Include(l => l.SaleOrder)
            .FirstOrDefaultAsync(l => l.UUID == saleOrderLineUuid)
            ?? throw new NotFoundException("SaleOrderLine", saleOrderLineUuid);
        var order = line.SaleOrder;

        // A deficit only exists once the order is confirmed, and a PO raised after a cancel would
        // never be found by the cancel that already ran (P4-04 cancels linked POs at that moment).
        if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed)
            && order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled))
            throw new UnprocessableEntityException(
                $"Sale order {order.SoNumber} is {order.Status}; only a CONFIRMED or PARTIALLY_FULFILLED order can have a purchase order raised against it.");

        // Idempotent on the PO's own link (LinkedSoLineId), not the line's back-pointer: the caller
        // is a retried Hangfire job, and if a previous attempt saved the PO but died before setting
        // the back-pointer, that PO is what proves the work was done. Repairs the back-pointer too.
        // Earliest live one: a PO split across suppliers (A29-P5-11) puts several POs on one line, and
        // the line's back-pointer should stay on the original rather than hop to the newest split.
        var existing = await _db.PurchaseOrders
            .Where(p => p.LinkedSoLineId == line.Id && !p.IsDelete && !DeadStatuses.Contains(p.Status))
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            if (line.LinkedPoId != existing.Id || line.SelectedSupplierId is null)
            {
                line.LinkedPoId        = existing.Id;
                line.SelectedSupplierId ??= existing.AutoSelectedSupplierId ?? existing.SupplierId;
                await _db.SaveChangesAsync();
            }
            return new AutoPurchaseOrderResult(existing.UUID, existing.PoNumber, existing.Status, existing.Source, true);
        }

        var names = await _supplierNames.GetNamesAsync([supplierId]);
        if (!names.TryGetValue(supplierId, out var supplierName))
            throw new NotFoundException("Supplier", supplierId);

        // §3.4 — DRAFT_ONLY: PO DRAFT, no workflow. AUTO_SEND: PO APPROVED, no human review.
        // REQUIRE_WORKFLOW: created as DRAFT and then genuinely submitted below, because
        // PENDING_APPROVAL with no approval record behind it would be a PO nobody can approve.
        // Anything unrecognised gets the safest treatment: a DRAFT nothing has been sent from.
        var config = await _config.GetConfigAsync();
        var (initialStatus, submit) = config.AutoPoApprovalMode switch
        {
            AutoPoApprovalModes.AutoSend        => ("APPROVED", false),
            AutoPoApprovalModes.RequireWorkflow => ("DRAFT", true),
            _                                    => ("DRAFT", false)
        };

        var isDropShip = line.FulfillmentMode == EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.DropShip);

        // §4.3 scenario 4 — drop ship only "if drop_ship_enabled". AvailabilityCheckService already
        // refuses to honour a DROP_SHIP line at confirm time when the org has it off, so a line that
        // still says DROP_SHIP here means the setting was switched off after the order was confirmed.
        // Refused rather than quietly downgraded to a back-to-back PO: that would send the goods to
        // our warehouse when the customer was promised direct shipment, without anyone deciding to.
        if (isDropShip && !config.DropShipEnabled)
            throw new UnprocessableEntityException(
                $"Sale order {order.SoNumber} has a DROP_SHIP line, but drop ship is disabled for this organization.");

        // The vendor ships to the customer's address, so a drop ship with no address (a self-pickup
        // order has none) has nowhere to go — copying a null into customer_shipping_address_id would
        // create exactly the PO §4.3 scenario 4 says must not exist.
        if (isDropShip && order.ShippingAddressId is null)
            throw new UnprocessableEntityException(
                $"Sale order {order.SoNumber} has a DROP_SHIP line but no shipping address, so a vendor has nowhere to ship it.");

        var created = await _repo.CreateFromSaleOrderDeficitAsync(new SaleOrderDeficitPo(
            Source:                    isDropShip ? PurchaseOrderSources.DropShip : PurchaseOrderSources.BackToBack,
            Status:                    initialStatus,
            TraceId:                   order.TraceId,
            SaleOrderId:               order.Id,
            SaleOrderLineId:           line.Id,
            SoNumber:                  order.SoNumber,
            SupplierId:                supplierId,
            SupplierName:              supplierName,
            VariantUuid:               line.VariantUuid,
            Quantity:                  qty,
            UnitPrice:                 price,
            // §6.3 — a drop ship PO carries the customer's address; a back-to-back one is received
            // into the org's own warehouse and has no use for it.
            CustomerShippingAddressId: isDropShip ? order.ShippingAddressId : null), userId);

        // The inverse of the PO's own link, and the one SaleOrderService.CancelAsync (P4-04) and
        // SaleOrderEmailService.SendPoApprovedAsync (P4-07) both resolve through.
        line.LinkedPoId         = created.Id;
        line.SelectedSupplierId = supplierId;
        // A29-P5-10 §14.2 — the margin this line now has, per unit, against what it will cost. Saved
        // with the back-pointer: both are "what the PO means for this line" and belong together.
        (line.Margin, line.MarginPercent) = SaleOrderMargin.Compute(line.UnitPrice, price);
        await _db.SaveChangesAsync();

        var status = submit ? await TrySubmitForApprovalAsync(created.Uuid, userId, created.PoNumber) : initialStatus;

        // §6.3.1 — appended to the sale order's own trace, since the PO carries its trace id. The
        // organization goes to the job explicitly (§13.7), taken from the sale order itself rather
        // than the ambient tenant: this service runs inside AutoPoCreationJob, a Hangfire job with no
        // HTTP request, which is exactly where the implicit route (TenantPropagatingJobFilter, which
        // only reads request claims) hands the timeline job no organization at all.
        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            order.TraceId,
            new TimelineEvent(
                SaleOrderTimelineEventTypes.PoCreatedFromSo, "PO", created.Uuid, created.PoNumber, DateTime.UtcNow, userId,
                $"{qty:0.####} units of {created.Sku ?? line.VariantUuid.ToString()} → {supplierName}"),
            null, null, order.OrganizationId));

        return new AutoPurchaseOrderResult(
            created.Uuid, created.PoNumber, status,
            isDropShip ? PurchaseOrderSources.DropShip : PurchaseOrderSources.BackToBack, false);
    }

    // The PO already exists by now, so a failure here (typically: no approval workflow configured
    // for POs in this org — the same failure the manual "submit for approval" button would hit) must
    // not lose it or fail the caller. It stays a DRAFT the team can review and submit themselves.
    private async Task<string> TrySubmitForApprovalAsync(Guid poUuid, int userId, string poNumber)
    {
        try
        {
            await _purchaseOrders.SubmitForApprovalAsync(poUuid, userId);
            return await _db.PurchaseOrders.AsNoTracking()
                .Where(p => p.UUID == poUuid).Select(p => p.Status).FirstAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Auto-created PO {PoNumber} could not be submitted for approval and was left as a DRAFT.", poNumber);
            return "DRAFT";
        }
    }
}
