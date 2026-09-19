namespace SMS.Modules.Logistics.Domain;

// The module's closed vocabularies.
//
// Every member carries an explicit [Code] rather than deriving the persisted string from the
// member name. That is deliberate: renaming an enum member is a refactor a developer expects to
// be safe, and if the persisted value were derived from the name, that refactor would silently
// orphan every existing row. The codes below are the contract with the database; the names are
// not.
//
// Statuses persist as strings, matching PurchaseOrder.Status and SupplierReturnOrder.Status in
// the rest of the system. The enums exist so the state machines (T-05) and services can reason
// in types instead of magic strings.

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
internal sealed class CodeAttribute : Attribute
{
    internal string Value { get; }
    internal CodeAttribute(string value) => Value = value;
}

// ── Delivery ──────────────────────────────────────────────────────────────────

internal enum DeliveryDirection
{
    [Code("INBOUND")]  Inbound,
    [Code("OUTBOUND")] Outbound,
    [Code("TRANSFER")] Transfer
}

// Where a delivery came from. Drives direction and — critically — whether the delivery is the
// document that posts the stock movement. See DeliverySourceTypeInfo.
internal enum DeliverySourceType
{
    [Code("PO")]       Po,
    [Code("SRO")]      Sro,
    [Code("MIV")]      Miv,
    [Code("TRANSFER")] Transfer,
    [Code("MANUAL")]   Manual
}

internal enum DeliveryPriority
{
    [Code("LOW")]    Low,
    [Code("NORMAL")] Normal,
    [Code("HIGH")]   High,
    [Code("URGENT")] Urgent
}

internal enum DeliveryStatus
{
    [Code("DRAFT")]               Draft,
    [Code("RELEASED")]            Released,
    [Code("PICKING")]             Picking,
    [Code("PICKED")]              Picked,
    [Code("PACKED")]              Packed,
    [Code("STAGED")]              Staged,
    // Conditional — only entered when a shipping rule demands approval (high value,
    // restricted lane, hazardous goods). Not part of the default path.
    [Code("PENDING_APPROVAL")]    PendingApproval,
    [Code("GOODS_ISSUED")]        GoodsIssued,
    [Code("IN_TRANSIT")]          InTransit,
    [Code("DELIVERED")]           Delivered,
    [Code("CLOSED")]              Closed,
    // Off-path
    [Code("ON_HOLD")]             OnHold,
    [Code("PARTIALLY_DELIVERED")] PartiallyDelivered,
    [Code("SHORT_CLOSED")]        ShortClosed,
    [Code("CANCELLED")]           Cancelled
}

/// <summary>
/// A pick list's own lifecycle, which is shorter than the delivery's: it is generated, worked,
/// and either finished or abandoned.
/// </summary>
internal enum PickListStatus
{
    [Code("OPEN")]        Open,
    [Code("IN_PROGRESS")] InProgress,
    [Code("COMPLETED")]   Completed,
    [Code("CANCELLED")]   Cancelled
}

/// <summary>
/// Why a picker came back with less than the instruction asked for.
/// <para>
/// A closed vocabulary rather than free text, because these are the rows somebody reports on: "how
/// often is stock missing from the bin" is answerable, "how often did someone type 'not there'"
/// is not. A note can still be attached alongside for the detail the code cannot carry.
/// </para>
/// </summary>
internal enum PickShortReason
{
    /// <summary>The bin was empty — the system thinks stock is there and it is not.</summary>
    [Code("NOT_FOUND")]      NotFound,

    /// <summary>Some was there, but not enough.</summary>
    [Code("SHORT_ON_SHELF")] ShortOnShelf,

    [Code("DAMAGED")]        Damaged,
    [Code("EXPIRED")]        Expired,

    /// <summary>Physically present but not sellable — quarantined, awaiting inspection.</summary>
    [Code("QUALITY_HOLD")]   QualityHold,

    /// <summary>The bin held something else. A putaway error, not a stock error.</summary>
    [Code("WRONG_ITEM")]     WrongItem,

    [Code("OTHER")]          Other
}

// ── Shipment ──────────────────────────────────────────────────────────────────

internal enum ShipmentStatus
{
    [Code("DRAFT")]              Draft,
    [Code("RATED")]              Rated,
    // In-flight, not a display state: a shipment sitting here has an outbound carrier call
    // that has not resolved. It is resolved by the idempotency ledger, never by re-clicking.
    [Code("BOOKING")]            Booking,
    [Code("BOOKED")]             Booked,
    [Code("LABEL_READY")]        LabelReady,
    [Code("PICKUP_REQUESTED")]   PickupRequested,
    [Code("PICKED_UP")]          PickedUp,
    [Code("IN_TRANSIT")]         InTransit,
    [Code("OUT_FOR_DELIVERY")]   OutForDelivery,
    [Code("DELIVERED")]          Delivered,
    // Off-path
    [Code("EXCEPTION")]          Exception,
    [Code("DELIVERY_ATTEMPTED")] DeliveryAttempted,
    [Code("RETURNED_TO_ORIGIN")] ReturnedToOrigin,
    [Code("CANCELLED")]          Cancelled,
    [Code("LOST")]               Lost,
    [Code("BOOKING_FAILED")]     BookingFailed
}

internal enum ShipmentMode
{
    [Code("COURIER")]   Courier,
    [Code("LTL")]       Ltl,
    [Code("FTL")]       Ftl,
    [Code("OWN_FLEET")] OwnFleet,
    [Code("HAND")]      Hand
}

// How a carrier is dealt with. MANUAL means a person keys in the airway bill and the carrier's
// own website does the tracking; API means an adapter books and tracks it (Phase 2); FILE means
// a batch manifest exchange. Every carrier starts MANUAL — that is what makes the module useful
// for carriers that will never have an API.
internal enum CarrierIntegrationMode
{
    [Code("MANUAL")] Manual,
    [Code("API")]    Api,
    [Code("FILE")]   File
}

// ── The carrier command ledger (T-36) ─────────────────────────────────────────

// Which carrier call a ledger row guards. Only calls that change something at the carrier are
// ledgered — fetching a label or tracking a parcel can be repeated freely.
internal enum CarrierCommandType
{
    [Code("BOOK")]   Book,
    [Code("CANCEL")] Cancel
}

// What is known about one carrier call. UNKNOWN is the status the ledger exists for: the call did
// not resolve, so the carrier may or may not have acted, and nothing may be assumed either way.
internal enum CarrierCommandStatus
{
    [Code("IN_FLIGHT")]     InFlight,
    [Code("SUCCEEDED")]     Succeeded,
    [Code("REFUSED")]       Refused,
    [Code("UNKNOWN")]       Unknown,
    [Code("NOT_PERFORMED")] NotPerformed
}

// Where a stored label came from (T-38): returned with the booking, or fetched afterwards.
internal enum ConsignmentLabelSource
{
    [Code("BOOKING")] Booking,
    [Code("FETCHED")] Fetched
}

// How a tracking event reached us (T-39, T-40).
internal enum TrackingEventSource
{
    [Code("WEBHOOK")] Webhook,
    [Code("POLL")]    Poll
}

// What became of one webhook delivery in the inbox (T-39).
internal enum WebhookDeliveryStatus
{
    [Code("RECEIVED")]  Received,
    [Code("PROCESSED")] Processed,
    [Code("REJECTED")]  Rejected,
    [Code("FAILED")]    Failed
}

// Which term decided a package's chargeable weight (T-44). Persisted on the package beside the
// figure itself, because a weight nobody can account for is a weight nobody can dispute an invoice
// with — and disputing invoices is what Phase 4 is for.
internal enum ChargeableWeightBasis
{
    /// <summary>The scale won: the parcel is denser than the service's divisor assumes.</summary>
    [Code("ACTUAL")]     Actual,
    /// <summary>Volume won: bulky and light, which is the case dimensional weight exists for.</summary>
    [Code("VOLUMETRIC")] Volumetric,
    /// <summary>Neither: the parcel came in under the service's billing floor.</summary>
    [Code("MINIMUM")]    Minimum,
    /// <summary>Neither a weight nor a full set of dimensions, so there is nothing to charge on.</summary>
    [Code("UNKNOWN")]    Unknown
}

// Where a consignment's freight cost came from (T-47). Stored, because the three answers carry
// different weight in a dispute: a carrier's own quote is what it said, a card is what was agreed,
// and a keyed-in figure is somebody's word.
internal enum RateSource
{
    /// <summary>The carrier's own rate API answered.</summary>
    [Code("CARRIER")]   Carrier,

    /// <summary>Priced from a negotiated tariff, because the carrier would not or could not quote.</summary>
    [Code("RATE_CARD")] RateCard,

    /// <summary>Somebody keyed the price in — a figure emailed or quoted over the telephone.</summary>
    [Code("MANUAL")]    Manual
}

// What became of cash a carrier collects on delivery (T-57).
internal enum CodStatus
{
    /// <summary>The consignment collects cash, and the carrier has not said it has it.</summary>
    [Code("EXPECTED")]    Expected,

    /// <summary>The carrier says it took the money. It is holding our cash.</summary>
    [Code("COLLECTED")]   Collected,

    /// <summary>Everything expected has reached us.</summary>
    [Code("SETTLED")]     Settled,

    /// <summary>
    /// A shortfall somebody has accepted will not be recovered. Always with a reason: cash written
    /// off without one is cash nobody can account for.
    /// </summary>
    [Code("WRITTEN_OFF")] WrittenOff
}

// Why a carrier billed something other than what was expected (T-55).
//
// Named rather than left as a number, because "the bill is 140 out" is not something anybody can
// act on, and "the carrier billed 14 kg where we made it 12.5" is.
internal enum VarianceReason
{
    /// <summary>The carrier billed on a different chargeable weight than we worked out.</summary>
    [Code("WEIGHT")]      Weight,

    /// <summary>The bill carries charge codes our quote did not.</summary>
    [Code("SURCHARGE")]   Surcharge,

    /// <summary>The bill names a different service than the one that was quoted.</summary>
    [Code("SERVICE")]     Service,

    /// <summary>
    /// Out of tolerance and nothing on the bill explains why. The honest answer, and the one worth
    /// putting in front of a person — a guessed reason is worse than none.
    /// </summary>
    [Code("UNEXPLAINED")] Unexplained
}

// Whether one line of a carrier's bill has been tied to a movement (T-54).
internal enum InvoiceLineMatchStatus
{
    /// <summary>Nothing has been found for it yet. The queue that has to be worked.</summary>
    [Code("UNMATCHED")] Unmatched,

    /// <summary>Tied to a consignment. Several lines may be tied to the same one.</summary>
    [Code("MATCHED")]   Matched,

    /// <summary>
    /// More than one consignment fits. Left for a person rather than guessed at: charging the wrong
    /// movement reconciles to nothing and hides a real discrepancy behind a plausible one.
    /// </summary>
    [Code("AMBIGUOUS")] Ambiguous,

    /// <summary>
    /// Not a movement charge at all — a monthly account fee, a stationery charge. Deliberately
    /// tied to nothing, with a reason, so it stops appearing in the queue.
    /// </summary>
    [Code("EXCLUDED")]  Excluded
}

// How a line came to be tied to a consignment (T-54).
internal enum InvoiceLineMatchMethod
{
    /// <summary>On the airway bill — the carrier's own reference, and the strongest evidence there is.</summary>
    [Code("AWB")]       Awb,

    /// <summary>On our consignment number, where the carrier echoed back the reference we sent.</summary>
    [Code("REFERENCE")] Reference,

    /// <summary>Somebody decided. Recorded as such, because it is a weaker claim than the other two.</summary>
    [Code("MANUAL")]    Manual
}

// Where a carrier's bill stands (T-53).
//
// Deliberately only the two states T-53 can reach. MATCHED, DISPUTED and APPROVED arrive with the
// tasks that reach them (T-55, T-56) — declaring them now would repeat the defect found as F38,
// where a status existed in the state machine with no way into it.
internal enum CarrierInvoiceStatus
{
    /// <summary>Recorded, and not yet matched against anything.</summary>
    [Code("RECEIVED")]  Received,

    /// <summary>Every line is settled and every charge is within tolerance (T-55).</summary>
    [Code("MATCHED")]   Matched,

    /// <summary>
    /// Matched, and something does not agree — a charge out of tolerance, or lines nobody could
    /// tie to a movement. Not a failure: most freight bills that end up here are simply queried.
    /// </summary>
    [Code("DISPUTED")]  Disputed,

    /// <summary>
    /// Accepted for payment (T-56), for a stated amount. What happens next — whether it posts into
    /// Finance or is paid from here — is decision G10, and nothing in this module presumes it.
    /// </summary>
    [Code("APPROVED")]  Approved,

    /// <summary>Withdrawn — a duplicate, or one keyed against the wrong carrier. Always with a reason.</summary>
    [Code("CANCELLED")] Cancelled
}

// How a query with a carrier ended (T-56).
internal enum DisputeOutcome
{
    /// <summary>The carrier agreed and credited it. The bill is approved at the lower figure.</summary>
    [Code("CREDIT_RECEIVED")]   CreditReceived,

    /// <summary>The carrier was right. Approved at what it billed.</summary>
    [Code("ACCEPTED_AS_BILLED")] AcceptedAsBilled,

    /// <summary>
    /// Neither side moved and the difference is not worth pursuing. Approved at what was billed,
    /// with the shortfall recorded as a decision rather than an oversight.
    /// </summary>
    [Code("WRITTEN_OFF")]       WrittenOff
}

// Where a freight accrual stands (T-52).
internal enum FreightAccrualStatus
{
    /// <summary>The movement has happened and no invoice has settled it. An open liability.</summary>
    [Code("ACCRUED")]  Accrued,

    /// <summary>An invoice has been matched against it (T-55). Still open until it is approved.</summary>
    [Code("MATCHED")]  Matched,

    /// <summary>Settled. The invoice was approved and the estimate is no longer carried.</summary>
    [Code("CLOSED")]   Closed,

    /// <summary>
    /// Written back without an invoice — the consignment was cancelled, or accrued in error. Always
    /// with a reason: an accrual that vanishes without one is a hole in a ledger.
    /// </summary>
    [Code("REVERSED")] Reversed
}

// How a rate card's weight break charges (T-46).
internal enum RateBasis
{
    /// <summary>Multiplied by the chargeable weight. The usual arrangement.</summary>
    [Code("PER_KG")] PerKg,

    /// <summary>The whole charge for anything in this break, whatever it weighs.</summary>
    [Code("FLAT")]   Flat
}

internal enum FreightTerms
{
    [Code("PREPAID")]     Prepaid,
    [Code("COLLECT")]     Collect,
    [Code("THIRD_PARTY")] ThirdParty
}

internal enum StopType
{
    [Code("PICKUP")] Pickup,
    [Code("DROP")]   Drop
}

internal enum PackageType
{
    [Code("BOX")]      Box,
    [Code("PALLET")]   Pallet,
    [Code("CRATE")]    Crate,
    [Code("ENVELOPE")] Envelope,
    [Code("DRUM")]     Drum,
    [Code("BAG")]      Bag,
    [Code("LOOSE")]    Loose
}

// ── Tracking ──────────────────────────────────────────────────────────────────

// The closed set every courier adapter maps its own codes into. Reporting, SLA calculation and
// the tracking UI read only these, which is what lets a new carrier be added without touching a
// single screen. The raw carrier code is always persisted alongside for audit.
//
// Adding a member here is a breaking change for every adapter's mapping table — a test asserts
// this set has exactly 12 members so the addition cannot pass unnoticed.
internal enum TrackingMilestone
{
    [Code("INFO_RECEIVED")]      InfoReceived,
    [Code("PICKED_UP")]          PickedUp,
    [Code("IN_TRANSIT")]         InTransit,
    [Code("ARRIVED_AT_HUB")]     ArrivedAtHub,
    [Code("DEPARTED_HUB")]       DepartedHub,
    [Code("CUSTOMS_HOLD")]       CustomsHold,
    [Code("OUT_FOR_DELIVERY")]   OutForDelivery,
    [Code("DELIVERY_ATTEMPTED")] DeliveryAttempted,
    [Code("DELIVERED")]          Delivered,
    [Code("EXCEPTION")]          Exception,
    [Code("RETURN_INITIATED")]   ReturnInitiated,
    [Code("RETURNED")]           Returned
}

// Where a delivery exception stands (T-60).
internal enum ExceptionStatus
{
    /// <summary>Raised, and nobody has finished with it.</summary>
    [Code("OPEN")]        Open,

    /// <summary>Waiting on the carrier, the consignee or somebody else. Still ours to chase.</summary>
    [Code("WAITING")]     Waiting,

    /// <summary>Dealt with, with a stated resolution.</summary>
    [Code("RESOLVED")]    Resolved,

    /// <summary>
    /// Raised in error, or a duplicate of another. Distinct from resolved on purpose — counting a
    /// mistaken exception as one that was fixed flatters every figure a scorecard produces.
    /// </summary>
    [Code("WITHDRAWN")]   Withdrawn
}

// How much an exception matters (T-60). Three, not five: a scale nobody can apply consistently
// is a scale that ends up meaning "whatever the person who raised it was feeling".
internal enum ExceptionSeverity
{
    [Code("LOW")]      Low,
    [Code("NORMAL")]   Normal,

    /// <summary>The goods are at risk, the customer is waiting, or money is involved.</summary>
    [Code("CRITICAL")] Critical
}

// How an exception came to be known (T-60). One the carrier told us about and one we noticed
// ourselves carry different weight when the two accounts disagree.
internal enum ExceptionSource
{
    [Code("CARRIER")] Carrier,
    [Code("MANUAL")]  Manual,

    /// <summary>The stuck sweep noticed it had gone quiet (T-40).</summary>
    [Code("SYSTEM")]  System
}

// How a proof of delivery came to be (T-61). Two, because two is all that can happen: the carrier
// reported the handover, or somebody here recorded it. There is no driver app, and a member no code
// can reach is the defect F46, F38 and T-27 each turned out to be.
internal enum ProofSource
{
    /// <summary>From a DELIVERED scan. Whatever the carrier said, including when it named nobody.</summary>
    [Code("CARRIER")] Carrier,

    /// <summary>Keyed in by a person — the only way a manual carrier is ever proved delivered.</summary>
    [Code("MANUAL")]  Manual
}

// What an artefact on a proof of delivery is (T-61).
internal enum ProofFileKind
{
    /// <summary>The signature itself.</summary>
    [Code("SIGNATURE")] Signature,

    /// <summary>The goods at the door, or the damage they arrived with.</summary>
    [Code("PHOTO")]     Photo,

    /// <summary>A scanned delivery note, gate pass or customs release.</summary>
    [Code("DOCUMENT")]  Document
}

internal enum DeliveryExceptionType
{
    [Code("ADDRESS_INVALID")]       AddressInvalid,
    [Code("CONSIGNEE_UNREACHABLE")] ConsigneeUnreachable,
    [Code("REFUSED")]               Refused,
    [Code("DAMAGED")]               Damaged,
    [Code("CUSTOMS_HOLD")]          CustomsHold,
    [Code("LOST")]                  Lost,
    [Code("DELAYED")]               Delayed,
    [Code("COD_MISMATCH")]          CodMismatch
}

// ── Address ───────────────────────────────────────────────────────────────────

// An address that cannot be parsed is still saved — it is never a hard failure, because the
// legacy free-text addresses being backfilled in T-16 mostly will not parse. Booking a courier
// requires VALID; browsing and reporting do not.
internal enum AddressValidationStatus
{
    [Code("UNVALIDATED")] Unvalidated,
    [Code("VALID")]       Valid,
    [Code("INVALID")]     Invalid
}

// What sits at the far end of a delivery. Doubles as the consignee classification: paired with
// Address.ConsigneeUuid it says *which* warehouse, supplier or project this address belongs to.
//
// CUSTOMER exists ahead of a customer master (decision G1). Nothing populates it today, but
// having the slot means adding sales orders later does not require migrating every address row
// that has already been written.
internal enum AddressType
{
    [Code("WAREHOUSE")]    Warehouse,
    [Code("SUPPLIER")]     Supplier,
    [Code("PROJECT_SITE")] ProjectSite,
    [Code("CUSTOMER")]     Customer,
    [Code("OTHER")]        Other
}
