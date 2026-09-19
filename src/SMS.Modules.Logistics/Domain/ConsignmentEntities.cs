using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// Layer C — the booked movement. The carrier-facing object: who carries it, under what service,
/// on which airway bill, for what money.
/// <para>
/// <b>On the name.</b> The architecture plan calls this the "Shipment". That name is taken: the
/// legacy <see cref="Shipment"/> entity maps to <c>logistics.shipments</c> and is still queried
/// directly by <c>SMS.Modules.Reports</c> (<c>ReportsRepository.GetShipmentTrackerAsync</c>), so
/// renaming it would break another module for no gain. "Consignment" is standard carrier
/// vocabulary for the same object, and it keeps both models compiling side by side until T-16
/// retires the legacy table. The HTTP surface can still speak of shipments.
/// </para>
/// <para>
/// Many-to-many with deliveries through <see cref="ConsignmentDelivery"/>: one consignment
/// consolidates several deliveries onto one truck, and one delivery splits across several
/// consignments when stock ships in waves. A one-to-many here is what makes "one truck, three
/// POs" a rewrite instead of a configuration.
/// </para>
/// </summary>
internal class Consignment : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>SHP-YYYY-NNNNN.</summary>
    public string ConsignmentNumber { get; set; } = string.Empty;

    // ── Carrier ───────────────────────────────────────────────────────────────

    public int?     CarrierId   { get; set; }
    public Carrier? Carrier     { get; set; }
    /// <summary>Denormalized so a consignment stays readable if the carrier row is later deactivated.</summary>
    public string?  CarrierName { get; set; }

    /// <summary>
    /// The account the consignment was booked on (T-37). Stored rather than re-resolved, so the
    /// booking job, its retries, the label fetch and the tracking poll all act on the same contract
    /// even if the carrier's default account changes in between. Null for a manual carrier.
    /// </summary>
    public int?            CarrierAccountId { get; set; }
    public CarrierAccount? CarrierAccount   { get; set; }

    /// <summary>Service level — Overnight, Economy and so on. Becomes a FK to carrier_services in Phase 2.</summary>
    public string? CarrierServiceCode { get; set; }

    /// <summary>See <see cref="ShipmentMode"/>. Stored as its code.</summary>
    public string Mode { get; set; } = LogisticsCode.Of(ShipmentMode.Courier);

    public string? MasterAwb        { get; set; }
    public string? CarrierReference { get; set; }

    // ── Commercial ────────────────────────────────────────────────────────────

    /// <summary>See <see cref="Domain.FreightTerms"/>. Stored as its code.</summary>
    public string FreightTerms { get; set; } = LogisticsCode.Of(Domain.FreightTerms.Prepaid);

    /// <summary>
    /// Cash to collect on delivery (decision G2). The columns exist from the first migration even
    /// though COD reconciliation is Phase 4 work — adding them later would mean a migration
    /// against live consignment rows, which is far more expensive than two unused columns.
    /// </summary>
    public decimal? CodAmount   { get; set; }
    public string?  CodCurrency { get; set; }

    // ── What the carriage costs (T-47, finding F37) ───────────────────────────

    /// <summary>
    /// What this consignment costs to move — every surcharge included, itemised in
    /// <see cref="Charges"/>.
    /// <para>
    /// <b>Why it lives here.</b> Until T-47 the only record of a price was the figure a carrier
    /// returned at booking, stored on the command ledger — so "what did this cost to ship" meant
    /// digging through carrier commands. Phase 4's three-way match compares an invoice against what
    /// was quoted and what was billed, and that comparison needs the figure on the consignment.
    /// </para>
    /// </summary>
    public decimal? FreightCost     { get; set; }
    public string?  FreightCurrency { get; set; }

    public DateTime? FreightRatedAt { get; set; }

    /// <summary>See <see cref="Domain.RateSource"/>. Stored as its code.</summary>
    public string? FreightRateSource { get; set; }

    /// <summary>Which card and lane, or which carrier service — so the figure stays explainable.</summary>
    public string? FreightRateNote { get; set; }

    /// <summary>
    /// The chargeable weight the quote was worked out against, kept beside it for the same reason
    /// the dim divisor is kept beside a package's dim weight: a price nobody can reproduce is a
    /// price nobody can dispute.
    /// </summary>
    public decimal? RatedChargeableWeightKg { get; set; }

    /// <summary>The service the quote was for, which may differ from what is finally booked.</summary>
    public string? RatedServiceCode { get; set; }

    // ── Timing ────────────────────────────────────────────────────────────────

    public DateTime? PickupWindowStart { get; set; }
    public DateTime? PickupWindowEnd   { get; set; }
    public DateTime? Etd               { get; set; }
    public DateTime? Eta               { get; set; }
    public DateTime? ActualDispatchAt  { get; set; }
    public DateTime? ActualArrivalAt   { get; set; }

    /// <summary>
    /// When the carrier event that last decided this consignment's status happened (T-39). An event
    /// older than this never moves the status: carriers deliver events late and out of order, and a
    /// delayed "in transit" must not undo a "delivered" that arrived first.
    /// </summary>
    public DateTime? LastStatusEventAt { get; set; }

    // ── Tracking poll and stuck detection (T-40) ──────────────────────────────

    /// <summary>When the latest carrier event of any kind happened. The measure of "no news".</summary>
    public DateTime? LastTrackingEventAt { get; set; }

    /// <summary>When the poll should next ask the carrier. Null means as soon as possible.</summary>
    public DateTime? TrackingNextPollAt   { get; set; }
    public DateTime? TrackingLastPolledAt { get; set; }

    /// <summary>Consecutive failed polls. Backs the interval off, and past a limit flags the consignment.</summary>
    public int       TrackingPollFailures { get; set; }
    public string?   TrackingLastError    { get; set; }

    /// <summary>
    /// Set when the consignment has gone quiet for longer than its status allows — not collected,
    /// no scan in transit, sitting in exception — and cleared as soon as it is not. Null means fine.
    /// </summary>
    public DateTime? StuckSince  { get; set; }
    public string?   StuckReason { get; set; }

    // ── The consignee's own view (T-62, decision G11) ─────────────────────────

    /// <summary>
    /// The unguessable address of this consignment's public tracking page. Null until somebody
    /// issues one, and null again once it is revoked.
    /// <para>
    /// <b>A token rather than the airway bill.</b> AWBs are sequential at most carriers, so
    /// addressing the page by one would make every other consignment's page one increment away.
    /// This is 256 bits of randomness, it belongs to one consignment, and revoking it costs
    /// nothing. What the page then shows is deliberately narrow — see <c>PublicTrackingService</c>.
    /// </para>
    /// </summary>
    public string? TrackingToken { get; set; }

    /// <summary>When the live token was issued. Re-issuing replaces it and invalidates the old one.</summary>
    public DateTime? TrackingTokenIssuedAt { get; set; }

    // ── Own fleet ─────────────────────────────────────────────────────────────

    public string? VehicleNumber { get; set; }
    public string? DriverName    { get; set; }
    public string? DriverPhone   { get; set; }

    // ── Routing ───────────────────────────────────────────────────────────────

    public int?     ShipFromAddressId { get; set; }
    public Address? ShipFromAddress   { get; set; }

    public int?     ShipToAddressId { get; set; }
    public Address? ShipToAddress   { get; set; }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>See <see cref="ShipmentStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = LogisticsCode.Of(ShipmentStatus.Draft);

    /// <summary>
    /// The key sent to the carrier and written to the command ledger <em>before</em> the booking
    /// call. It is what a retry consults instead of booking a second real parcel. Populated in
    /// Phase 2; the column exists now so the booking path never has to migrate to get it.
    /// </summary>
    public string? BookingIdempotencyKey { get; set; }

    public string? BookingFailureReason { get; set; }

    public string? Notes { get; set; }

    /// <summary>Optimistic concurrency — see the note on <see cref="DeliveryOrder.RowVersion"/>.</summary>
    public byte[] RowVersion { get; set; } = [];

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<ConsignmentDelivery> Deliveries { get; set; } = new List<ConsignmentDelivery>();
    public ICollection<ConsignmentStop>     Stops      { get; set; } = new List<ConsignmentStop>();
    public ICollection<ConsignmentCharge>   Charges    { get; set; } = new List<ConsignmentCharge>();
}

/// <summary>
/// One line of what a consignment costs — the base carriage, then each surcharge.
/// <para>
/// <b>Itemised rather than a single total</b>, because Phase 4 compares this against a carrier
/// invoice, and invoices arrive itemised. A total on its own can say <em>that</em> the two disagree;
/// it can never say where, which is the only part anybody can act on.
/// </para>
/// </summary>
internal class ConsignmentCharge : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    /// <summary>BASE for the carriage itself, then the carrier's own surcharge codes — FUEL, COD.</summary>
    public string  Code        { get; set; } = string.Empty;
    public string? Description { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Base first, then surcharges in the order the quote listed them.</summary>
    public int Sequence { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}

/// <summary>
/// The many-to-many join between a consignment and the deliveries it carries.
/// </summary>
internal class ConsignmentDelivery : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    public int           DeliveryOrderId { get; set; }
    public DeliveryOrder DeliveryOrder   { get; set; } = null!;

    /// <summary>Load order on the vehicle; also the order stops are visited in.</summary>
    public int Sequence { get; set; }

    /// <summary>Which stop this delivery is dropped at, on a multi-stop route.</summary>
    public int?              ConsignmentStopId { get; set; }
    public ConsignmentStop?  ConsignmentStop   { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}

/// <summary>
/// A stop on a multi-stop route. A simple courier consignment has none; a milk run has several.
/// </summary>
internal class ConsignmentStop : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    public int Sequence { get; set; }

    public int?     AddressId { get; set; }
    public Address? Address   { get; set; }

    /// <summary>See <see cref="Domain.StopType"/>. Stored as its code.</summary>
    public string StopType { get; set; } = LogisticsCode.Of(Domain.StopType.Drop);

    public DateTime? PlannedArrival { get; set; }
    public DateTime? ActualArrival  { get; set; }

    public string? Notes { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }

    public ICollection<ConsignmentDelivery> Deliveries { get; set; } = new List<ConsignmentDelivery>();
}
