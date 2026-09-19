namespace SMS.Modules.Logistics.Constants;

// Compile-time constants for the codes declared by the module's enums.
//
// These exist because EF LINQ, migrations and seed data all need a constant expression, which a
// method call cannot provide. They duplicate the [Code] attributes in Domain/LogisticsEnums.cs —
// that duplication is deliberate and is made safe by VocabularyTests, which asserts the two sides
// agree exactly in both directions. Adding a member to an enum without adding it here fails a
// test rather than going unnoticed.
internal static class LogisticsStatuses
{
    internal static class Delivery
    {
        internal const string Draft              = "DRAFT";
        internal const string Released           = "RELEASED";
        internal const string Picking            = "PICKING";
        internal const string Picked             = "PICKED";
        internal const string Packed             = "PACKED";
        internal const string Staged             = "STAGED";
        internal const string PendingApproval    = "PENDING_APPROVAL";
        internal const string GoodsIssued        = "GOODS_ISSUED";
        internal const string InTransit          = "IN_TRANSIT";
        internal const string Delivered          = "DELIVERED";
        internal const string Closed             = "CLOSED";
        internal const string OnHold             = "ON_HOLD";
        internal const string PartiallyDelivered = "PARTIALLY_DELIVERED";
        internal const string ShortClosed        = "SHORT_CLOSED";
        internal const string Cancelled          = "CANCELLED";
    }

    internal static class Shipment
    {
        internal const string Draft             = "DRAFT";
        internal const string Rated             = "RATED";
        internal const string Booking           = "BOOKING";
        internal const string Booked            = "BOOKED";
        internal const string LabelReady        = "LABEL_READY";
        internal const string PickupRequested   = "PICKUP_REQUESTED";
        internal const string PickedUp          = "PICKED_UP";
        internal const string InTransit         = "IN_TRANSIT";
        internal const string OutForDelivery    = "OUT_FOR_DELIVERY";
        internal const string Delivered         = "DELIVERED";
        internal const string Exception         = "EXCEPTION";
        internal const string DeliveryAttempted = "DELIVERY_ATTEMPTED";
        internal const string ReturnedToOrigin  = "RETURNED_TO_ORIGIN";
        internal const string Cancelled         = "CANCELLED";
        internal const string Lost              = "LOST";
        internal const string BookingFailed     = "BOOKING_FAILED";
    }

    internal static class CarrierCommand
    {
        internal const string InFlight     = "IN_FLIGHT";
        internal const string Succeeded    = "SUCCEEDED";
        internal const string Refused      = "REFUSED";
        internal const string Unknown      = "UNKNOWN";
        internal const string NotPerformed = "NOT_PERFORMED";
    }

    internal static class Direction
    {
        internal const string Inbound  = "INBOUND";
        internal const string Outbound = "OUTBOUND";
        internal const string Transfer = "TRANSFER";
    }

    internal static class SourceType
    {
        internal const string Po       = "PO";
        internal const string Sro      = "SRO";
        internal const string Miv      = "MIV";
        internal const string Transfer = "TRANSFER";
        internal const string Manual   = "MANUAL";
    }

    internal static class Milestone
    {
        internal const string InfoReceived      = "INFO_RECEIVED";
        internal const string PickedUp          = "PICKED_UP";
        internal const string InTransit         = "IN_TRANSIT";
        internal const string ArrivedAtHub      = "ARRIVED_AT_HUB";
        internal const string DepartedHub       = "DEPARTED_HUB";
        internal const string CustomsHold       = "CUSTOMS_HOLD";
        internal const string OutForDelivery    = "OUT_FOR_DELIVERY";
        internal const string DeliveryAttempted = "DELIVERY_ATTEMPTED";
        internal const string Delivered         = "DELIVERED";
        internal const string Exception         = "EXCEPTION";
        internal const string ReturnInitiated   = "RETURN_INITIATED";
        internal const string Returned          = "RETURNED";
    }
}
