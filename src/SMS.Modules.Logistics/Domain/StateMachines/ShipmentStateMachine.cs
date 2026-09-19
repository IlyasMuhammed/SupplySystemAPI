namespace SMS.Modules.Logistics.Domain.StateMachines;

/// <summary>
/// The shipment lifecycle: owned by the carrier relationship, ending at delivery.
/// </summary>
internal sealed class ShipmentStateMachine : StateMachine<ShipmentStatus>
{
    /// <summary>
    /// Statuses in which the carrier already holds the goods, so an outbound cancellation must
    /// be tolerated as a business refusal ("already picked up") rather than treated as a fault.
    /// </summary>
    /// <remarks>
    /// Declared above <see cref="Instance"/> on purpose: static field initializers run in
    /// textual order, so anything <c>BuildTable</c> might come to read must be initialized
    /// before the instance that calls it.
    /// </remarks>
    internal static readonly ShipmentStatus[] CarrierHoldsGoods =
    [
        ShipmentStatus.PickedUp,
        ShipmentStatus.InTransit,
        ShipmentStatus.OutForDelivery,
        ShipmentStatus.DeliveryAttempted,
        ShipmentStatus.Exception
    ];

    internal static readonly ShipmentStateMachine Instance = new();

    protected override string DocumentName => "shipment";

    private ShipmentStateMachine() : base(BuildTable()) { }

    private static Dictionary<ShipmentStatus, ShipmentStatus[]> BuildTable() => new()
    {
        [ShipmentStatus.Draft] =
        [
            ShipmentStatus.Rated,
            ShipmentStatus.Booking,
            // The manual path. A carrier with no API is booked by a person — a phone call, a
            // portal, a paper consignment note — who then keys in the airway bill. There is no
            // in-flight request to guard, so routing it through BOOKING would claim a carrier
            // call happened that never did, and leave the idempotency ledger holding a key for
            // a request nobody made.
            ShipmentStatus.Booked,
            ShipmentStatus.Cancelled
        ],

        [ShipmentStatus.Rated] =
        [
            ShipmentStatus.Booking,

            // Re-rating (T-47) is deliberately *not* a transition. A fresh quote replaces a stale
            // one in place, leaving the status alone — which is what self-transitions being
            // illegal is for: nothing about the consignment's stage has changed.

            // The manual path again. Nothing about having a price stops a carrier being booked by
            // telephone — and until T-47 made RATED reachable this gap could not be reached, so
            // rating a consignment would quietly have made it unbookable by hand.
            ShipmentStatus.Booked,

            // Clearing the quote puts it back where it started.
            ShipmentStatus.Draft,
            ShipmentStatus.Cancelled
        ],

        // BOOKING is in-flight, not a display state. A shipment sitting here has an outbound
        // carrier call that has not resolved, and it is resolved by the idempotency ledger —
        // never by a user clicking again. Note there is no BOOKING → BOOKING and no
        // BOOKING → CANCELLED: cancelling a booking whose outcome is unknown is how a real
        // parcel ends up moving with no shipment record pointing at it.
        [ShipmentStatus.Booking] =
        [
            ShipmentStatus.Booked,
            ShipmentStatus.BookingFailed
        ],

        // A failed booking can be retried — through a new idempotency key, not by re-entering
        // BOOKING blindly.
        [ShipmentStatus.BookingFailed] =
        [
            ShipmentStatus.Booking,
            ShipmentStatus.Draft,
            ShipmentStatus.Cancelled
        ],

        [ShipmentStatus.Booked] =
        [
            ShipmentStatus.LabelReady,
            ShipmentStatus.PickupRequested,
            ShipmentStatus.Cancelled
        ],

        // Some carriers pool pickups by account rather than by shipment, so a labelled parcel
        // can be collected without an explicit pickup request.
        [ShipmentStatus.LabelReady] =
        [
            ShipmentStatus.PickupRequested,
            ShipmentStatus.PickedUp,
            ShipmentStatus.Cancelled
        ],

        [ShipmentStatus.PickupRequested] =
        [
            ShipmentStatus.PickedUp,
            ShipmentStatus.Exception,
            ShipmentStatus.Cancelled
        ],

        // ── Once the carrier holds the goods, cancellation is the carrier's to refuse. ──
        [ShipmentStatus.PickedUp] =
        [
            ShipmentStatus.InTransit,
            ShipmentStatus.Exception
        ],

        [ShipmentStatus.InTransit] =
        [
            ShipmentStatus.OutForDelivery,
            ShipmentStatus.Exception,
            ShipmentStatus.ReturnedToOrigin,
            ShipmentStatus.Lost
        ],

        [ShipmentStatus.OutForDelivery] =
        [
            ShipmentStatus.Delivered,
            ShipmentStatus.DeliveryAttempted,
            ShipmentStatus.Exception
        ],

        // A failed attempt goes back out for delivery, or eventually home.
        [ShipmentStatus.DeliveryAttempted] =
        [
            ShipmentStatus.OutForDelivery,
            ShipmentStatus.Exception,
            ShipmentStatus.ReturnedToOrigin
        ],

        // Exceptions are recoverable — a customs hold clears, a bad address is corrected — so
        // this is a waypoint, not an end state.
        [ShipmentStatus.Exception] =
        [
            ShipmentStatus.InTransit,
            ShipmentStatus.OutForDelivery,
            ShipmentStatus.Delivered,
            ShipmentStatus.ReturnedToOrigin,
            ShipmentStatus.Lost
        ],

        // Terminal.
        [ShipmentStatus.Delivered]        = [],
        [ShipmentStatus.ReturnedToOrigin] = [],
        [ShipmentStatus.Cancelled]        = [],
        [ShipmentStatus.Lost]             = []
    };
}
