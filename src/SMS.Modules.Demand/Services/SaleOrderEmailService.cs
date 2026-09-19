using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

// A29-P4-07 §5.1/§5.2/§5.3 — see ISaleOrderEmailService for which of these seven have a real call
// site today. Every public method is a self-contained unit: resolve data, render, resolve
// recipients, write one SaleOrderIntimation row, hand the send to ISaleOrderIntimationDispatchJob.
// None of them throw outward — see the interface's own remarks on why.
internal sealed class SaleOrderEmailService : ISaleOrderEmailService
{
    private readonly DemandDbContext _db;
    private readonly IStockReservationService _stock;
    private readonly ISaleOrderConfigService _config;
    private readonly IOrgChartService _orgChart;
    private readonly IUserQueryService _users;
    private readonly ISupplierNameLookupService _partnerNames;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SaleOrderEmailService> _log;

    public SaleOrderEmailService(
        DemandDbContext db, IStockReservationService stock, ISaleOrderConfigService config,
        IOrgChartService orgChart, IUserQueryService users, ISupplierNameLookupService partnerNames,
        IBackgroundJobClient jobs, ILogger<SaleOrderEmailService> log)
    {
        _db           = db;
        _stock        = stock;
        _config       = config;
        _orgChart     = orgChart;
        _users        = users;
        _partnerNames = partnerNames;
        _jobs         = jobs;
        _log          = log;
    }

    public async Task SendConfirmationAsync(Guid saleOrderUuid)
    {
        try
        {
            var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == saleOrderUuid);
            if (order is null)
            {
                _log.LogWarning("SendConfirmationAsync: sale order {Uuid} not found.", saleOrderUuid);
                return;
            }

            var partnerName = await ResolvePartnerNameAsync(order.PartnerId);
            var rows = order.Lines.Select(l => new SaleOrderEmailTemplates.LineRow(
                l.VariantUuid.ToString(), l.Quantity, l.AvailableQtyAtConfirm, l.DeficitQty, l.FulfillmentMode, ActionTaken(l)
            )).ToList();

            var subject = $"[SMS] Sale Order {order.SoNumber} Confirmed - Availability Summary";
            var body    = SaleOrderEmailTemplates.Confirmation(order, partnerName, rows);
            var recipients = JoinRecipients(
                await ResolveDeptHeadEmailAsync(order.IntimationDepartmentId),
                await ResolveUserEmailAsync(order.CreatedBy));

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.SoConfirmed, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendConfirmationAsync failed for sale order {Uuid}.", saleOrderUuid);
        }
    }

    public async Task SendPoCreatedAsync(Guid saleOrderUuid, Guid purchaseOrderUuid)
    {
        try
        {
            var order = await _db.SaleOrders.FirstOrDefaultAsync(x => x.UUID == saleOrderUuid);
            var po    = await _db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.UUID == purchaseOrderUuid);
            if (order is null || po is null)
            {
                _log.LogWarning(
                    "SendPoCreatedAsync: sale order {SoUuid} or PO {PoUuid} not found.", saleOrderUuid, purchaseOrderUuid);
                return;
            }

            var rows = po.Lines.Select(l => new SaleOrderEmailTemplates.LineRow(
                l.ItemDescription, l.Quantity, null, null, null, $"{l.Quantity:0.####} @ {l.UnitPrice:0.00}"
            )).ToList();

            var subject = $"[SMS] Back-to-Back PO {po.PoNumber} Created for Sale Order {order.SoNumber}";
            var body    = SaleOrderEmailTemplates.PoCreated(order, po, rows);
            var recipients = await ResolveDeptHeadEmailAsync(order.IntimationDepartmentId) ?? string.Empty;

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.PoCreated, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendPoCreatedAsync failed for sale order {SoUuid}/PO {PoUuid}.", saleOrderUuid, purchaseOrderUuid);
        }
    }

    public async Task SendDropShipAsync(Guid saleOrderUuid)
    {
        try
        {
            var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == saleOrderUuid);
            if (order is null)
            {
                _log.LogWarning("SendDropShipAsync: sale order {Uuid} not found.", saleOrderUuid);
                return;
            }

            var partnerName  = await ResolvePartnerNameAsync(order.PartnerId);
            var dropShipCode = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.DropShip);
            var supplierIds  = order.Lines
                .Where(l => l.FulfillmentMode == dropShipCode && l.SelectedSupplierId is not null)
                .Select(l => l.SelectedSupplierId!.Value).Distinct().ToList();
            var supplierNames = supplierIds.Count == 0
                ? new List<string>()
                : (await _partnerNames.GetNamesAsync(supplierIds)).Values.ToList();

            var subject = $"[SMS] Drop Ship Order — Sale Order {order.SoNumber}";
            var body    = SaleOrderEmailTemplates.DropShip(order, partnerName, supplierNames);
            var recipients = await ResolveDeptHeadEmailAsync(order.IntimationDepartmentId) ?? string.Empty;

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.DropShip, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendDropShipAsync failed for sale order {Uuid}.", saleOrderUuid);
        }
    }

    public async Task SendReservedAsync(Guid saleOrderUuid)
    {
        try
        {
            var order = await _db.SaleOrders.FirstOrDefaultAsync(x => x.UUID == saleOrderUuid);
            if (order is null)
            {
                _log.LogWarning("SendReservedAsync: sale order {Uuid} not found.", saleOrderUuid);
                return;
            }

            var reservations = await _stock.GetBySourceAsync(ReservationSourceType.SalesOrder, saleOrderUuid);
            var activeCount  = reservations.Count(r => r.Status == "ACTIVE");
            if (activeCount == 0) return; // nothing held — nothing to report

            var config  = await _config.GetConfigAsync();
            var subject = $"[SMS] Stock Reserved — Sale Order {order.SoNumber}";
            var body    = SaleOrderEmailTemplates.Reserved(order, activeCount, config.ReservationTtlHours);
            var recipients = await ResolveUserEmailAsync(order.CreatedBy) ?? string.Empty;

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.Reserved, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendReservedAsync failed for sale order {Uuid}.", saleOrderUuid);
        }
    }

    public async Task SendPoApprovedAsync(Guid purchaseOrderUuid)
    {
        try
        {
            var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.UUID == purchaseOrderUuid);
            if (po is null)
            {
                _log.LogWarning("SendPoApprovedAsync: PO {Uuid} not found.", purchaseOrderUuid);
                return;
            }

            // The PO's own link first (PurchaseOrder.LinkedSoId, A29-P5-01): it names the order for
            // every PO raised for it, including one a split put alongside the original. The line's
            // back-pointer is only the fallback for a PO linked from that side alone.
            var orderId = po.LinkedSoId
                ?? await _db.SaleOrderLines.Where(l => l.LinkedPoId == po.Id).Select(l => (int?)l.SaleOrderId).FirstOrDefaultAsync();
            if (orderId is null) return; // not an SO-linked PO — nothing to notify about

            var order = await _db.SaleOrders.FirstOrDefaultAsync(x => x.Id == orderId.Value);
            if (order is null) return;

            var subject = $"[SMS] PO {po.PoNumber} Approved";
            var body    = SaleOrderEmailTemplates.PoApproved(order, po);
            var recipients = JoinRecipients(
                await ResolveUserEmailAsync(order.CreatedBy),
                await ResolveDeptHeadEmailAsync(order.IntimationDepartmentId));

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.PoApproved, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendPoApprovedAsync failed for PO {Uuid}.", purchaseOrderUuid);
        }
    }

    public async Task SendGrnReceivedAsync(Guid saleOrderUuid, string grnNumber, decimal receivedQty, decimal reservedQty)
    {
        try
        {
            var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == saleOrderUuid);
            if (order is null)
            {
                _log.LogWarning("SendGrnReceivedAsync: sale order {Uuid} not found.", saleOrderUuid);
                return;
            }

            // What is still awaited is whatever the order's lines are still short — the link service
            // has already brought DeficitQty down by this receipt before it calls here.
            var stillAwaited = order.Lines.Sum(l => l.DeficitQty ?? 0m);

            var subject = $"[SMS] Goods Received — Sale Order {order.SoNumber}";
            var body    = SaleOrderEmailTemplates.GrnReceived(order, grnNumber, receivedQty, reservedQty, stillAwaited);
            var recipients = await ResolveUserEmailAsync(order.CreatedBy) ?? string.Empty;

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.GrnReceived, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendGrnReceivedAsync failed for sale order {Uuid}, GRN {Grn}.", saleOrderUuid, grnNumber);
        }
    }

    public async Task SendExpiringAsync(Guid reservationUuid)
    {
        try
        {
            var reservation = await _stock.GetByUuidAsync(reservationUuid);
            if (reservation is null || reservation.SourceType != ReservationSourceType.SalesOrder)
            {
                _log.LogWarning(
                    "SendExpiringAsync: reservation {Uuid} not found, or not a sale order hold.", reservationUuid);
                return;
            }

            var order = await _db.SaleOrders.FirstOrDefaultAsync(x => x.UUID == reservation.SourceUuid);
            if (order is null) return;

            var subject = $"[SMS] Reservation Expiring — Sale Order {order.SoNumber}";
            var body    = SaleOrderEmailTemplates.Expiring(order, reservation.ExpiresAt);
            var recipients = await ResolveUserEmailAsync(order.CreatedBy) ?? string.Empty;

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.Expiring, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendExpiringAsync failed for reservation {Uuid}.", reservationUuid);
        }
    }

    public async Task SendFulfilledAsync(Guid saleOrderUuid, string deliveryNumber, decimal deliveredQty)
    {
        try
        {
            var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == saleOrderUuid);
            if (order is null)
            {
                _log.LogWarning("SendFulfilledAsync: sale order {Uuid} not found.", saleOrderUuid);
                return;
            }

            var partnerName = await ResolvePartnerNameAsync(order.PartnerId);
            var rows = order.Lines.Select(l => new SaleOrderEmailTemplates.LineRow(
                l.VariantUuid.ToString(), l.Quantity, null, null, l.FulfillmentMode,
                $"{l.FulfilledQty:0.####} of {l.Quantity:0.####} delivered."
            )).ToList();

            var subject = $"[SMS] Sale Order {order.SoNumber} Fulfilled";
            var body    = SaleOrderEmailTemplates.Fulfilled(order, partnerName, deliveryNumber, deliveredQty, rows);
            var recipients = JoinRecipients(
                await ResolveDeptHeadEmailAsync(order.IntimationDepartmentId),
                await ResolveUserEmailAsync(order.CreatedBy));

            await QueueAndDispatchAsync(order.Id, SaleOrderIntimationEventType.SoFulfilled, recipients, subject, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendFulfilledAsync failed for sale order {Uuid}.", saleOrderUuid);
        }
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static string ActionTaken(SaleOrderLine line)
    {
        if (line.FulfillmentMode == EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.InStock))
            return $"{line.Quantity:0.####} reserved from stock.";
        if (line.FulfillmentMode == EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.Split))
            return $"{line.AvailableQtyAtConfirm:0.####} reserved from stock. {line.DeficitQty:0.####} unit(s) require procurement.";
        if (line.FulfillmentMode == EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.BackToBack))
            return $"No stock available. {line.DeficitQty:0.####} unit(s) require procurement.";
        if (line.FulfillmentMode == EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.DropShip))
            return "Drop ship — no warehouse stock impact.";
        return "Not yet confirmed.";
    }

    private async Task<string?> ResolveDeptHeadEmailAsync(int? departmentId)
    {
        if (departmentId is not { } deptId) return null;
        var head = await _orgChart.GetDepartmentHeadAsync(deptId);
        return head is null ? null : await _users.GetUserEmailAsync(head.UserId);
    }

    private Task<string?> ResolveUserEmailAsync(int userId) => _users.GetUserEmailAsync(userId);

    private async Task<string> ResolvePartnerNameAsync(Guid partnerId)
    {
        var names = await _partnerNames.GetNamesAsync([partnerId]);
        return names.TryGetValue(partnerId, out var name) ? name : "(unknown customer)";
    }

    private static string JoinRecipients(params string?[] emails) =>
        string.Join(", ", emails.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase));

    // §5.3 — writes the row first (QUEUED, or FAILED immediately if there is no one to send to),
    // then hands the actual send to the dispatch job so a transient failure retries without
    // re-rendering or duplicating this row.
    private async Task QueueAndDispatchAsync(
        int saleOrderId, SaleOrderIntimationEventType eventType, string recipients, string subject, string bodyHtml)
    {
        var intimation = new SaleOrderIntimation
        {
            SaleOrderId = saleOrderId,
            EventType   = EnumCode<SaleOrderIntimationEventType>.Of(eventType),
            Recipients  = recipients,
            Subject     = subject,
            BodyHtml    = bodyHtml,
            Status      = EnumCode<SaleOrderIntimationStatus>.Of(SaleOrderIntimationStatus.Queued)
        };

        if (string.IsNullOrWhiteSpace(recipients))
        {
            intimation.Status       = EnumCode<SaleOrderIntimationStatus>.Of(SaleOrderIntimationStatus.Failed);
            intimation.ErrorMessage = "No recipient email address could be resolved.";
            _db.SaleOrderIntimations.Add(intimation);
            await _db.SaveChangesAsync();
            return;
        }

        _db.SaleOrderIntimations.Add(intimation);
        await _db.SaveChangesAsync();

        var jobId = _jobs.Enqueue<ISaleOrderIntimationDispatchJob>(j => j.DispatchAsync(intimation.UUID));
        intimation.HangfireJobId = jobId;
        await _db.SaveChangesAsync();
    }
}
