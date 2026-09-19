namespace SMS.Modules.Demand.Services;

// A29-P4-07 §5.1/§5.2/§5.3. Seven methods, one per §5.1 event. Every call renders a subject/body,
// resolves recipients, writes one demand.SaleOrderIntimations row (A29-P4-06), then hands the actual
// send to ISaleOrderIntimationDispatchJob via Hangfire so a transient SMTP/SendGrid failure retries
// without re-rendering or re-logging the event. Methods never throw outward — a notification-side
// failure (recipient unresolvable, DB hiccup while logging) must never abort the caller's own
// business transaction; each one logs and returns instead. See SaleOrderEmailService's own remarks
// for which of these have a real call site today and which are correct but not yet reachable,
// because the trigger they describe (Section 6's back-to-back PO, GRN↔SO linkage) isn't built.
public interface ISaleOrderEmailService
{
    /// <summary>SO Confirmed — intimation dept + SO creator. Full §5.2 template: header, per-line
    /// availability/mode/action table, self-pickup note. Wired into SaleOrderService.ConfirmAsync.</summary>
    Task SendConfirmationAsync(Guid saleOrderUuid);

    /// <summary>Back-to-back PO created — intimation dept. Real and correct, but nothing calls it
    /// yet: Section 6 (auto-PO creation) is still AutoPoCreationJob's placeholder.</summary>
    Task SendPoCreatedAsync(Guid saleOrderUuid, Guid purchaseOrderUuid);

    /// <summary>Drop ship detected — intimation dept. Same caveat as SendPoCreatedAsync.</summary>
    Task SendDropShipAsync(Guid saleOrderUuid);

    /// <summary>Stock reserved — SO creator. Real and correct; not wired into ConfirmAsync (which
    /// already sends SendConfirmationAsync for the same moment) to avoid two overlapping emails for
    /// one action without a spec-given rule for when they'd diverge.</summary>
    Task SendReservedAsync(Guid saleOrderUuid);

    /// <summary>PO approved (manual) — SO creator + dept. Resolves the owning SO via
    /// SaleOrderLine.LinkedPoId, which nothing sets yet (same Section 6 gap as SendPoCreatedAsync) —
    /// a no-op today, correct once it does.</summary>
    Task SendPoApprovedAsync(Guid purchaseOrderUuid);

    /// <summary>GRN received (SO-linked) — SO creator: what arrived, how much of it is now reserved
    /// for the order, and what is still awaited. Takes the sale order and the receipt's figures
    /// rather than a bare GRN id (A29-P5-06): Demand cannot read Warehouse's GRN tables, so the one
    /// caller that can — ISaleOrderGrnLinkService, fed by Warehouse — supplies them. Wired.</summary>
    Task SendGrnReceivedAsync(Guid saleOrderUuid, string grnNumber, decimal receivedQty, decimal reservedQty);

    /// <summary>Reservation expiring — SO creator, "expires in 24h". Real and correct; A29-P4-05's
    /// ReservationExpirySweepJob still sends this event directly via INotificationService rather
    /// than through here — left untouched deliberately, since this task's own dependencies don't
    /// include P4-05 and that job already has its own passing tests.</summary>
    Task SendExpiringAsync(Guid reservationUuid);

    /// <summary>A29-P6-06 §7.6 — the order is fulfilled in full: SO creator + intimation dept, with a
    /// line-by-line delivered/ordered table and the delivery that completed it. Beyond §5.1's seven
    /// events, because §7.6 asks for a fulfilment email and none of the seven covers it.</summary>
    Task SendFulfilledAsync(Guid saleOrderUuid, string deliveryNumber, decimal deliveredQty);
}
