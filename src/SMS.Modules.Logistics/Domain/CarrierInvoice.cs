using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// A bill from a carrier — the third leg of the three-way match, and the only one that had no
/// record anywhere until now.
/// <para>
/// <b>Why it is not a Finance <c>Invoice</c>.</b> That entity requires a <c>SupplierId</c> and its
/// lines are shaped for purchase orders and goods receipts. A carrier bill is matched against
/// consignments instead, and a carrier is not a supplier in this system's data. Whether an approved
/// one goes on to post into Finance is decision <b>G10</b>, and nothing here presumes the answer.
/// </para>
/// </summary>
internal class CarrierInvoice : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int     CarrierId { get; set; }
    public Carrier Carrier   { get; set; } = null!;

    /// <summary>Denormalized so the invoice stays readable if the carrier is later deactivated.</summary>
    public string? CarrierName { get; set; }

    /// <summary>
    /// The carrier's own number, as printed. Unique per carrier — keying the same bill twice is how
    /// one gets paid twice, and the second copy looks exactly as legitimate as the first.
    /// </summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    public DateTime  InvoiceDate { get; set; }
    public DateTime? DueDate     { get; set; }

    /// <summary>ISO 4217. A bill with no currency is a list of numbers.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// What the carrier says the whole thing comes to. Checked against the sum of the lines on
    /// every write: a bill whose lines do not add up to its own total is either mis-keyed or
    /// mis-parsed, and matching line by line against a header that says something else is how a
    /// discrepancy gets absorbed without anybody seeing it.
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>Tax, where the carrier states it separately. Not part of the line sum.</summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>See <see cref="CarrierInvoiceStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = LogisticsCode.Of(CarrierInvoiceStatus.Received);

    /// <summary>How it arrived — keyed by a person, or brought in in bulk.</summary>
    public string? Source { get; set; }

    public string? Notes { get; set; }

    /// <summary>Required when cancelling: a bill that disappears without a reason is unexplainable.</summary>
    public string? CancelReason { get; set; }

    // ── Querying it, and accepting it (T-56) ──────────────────────────────────

    /// <summary>When a query was raised with the carrier. Null if it never was.</summary>
    public DateTime? DisputeRaisedAt { get; set; }

    /// <summary>Required when querying — a dispute nobody can restate is a dispute nobody wins.</summary>
    public string? DisputeReason { get; set; }

    /// <summary>The carrier's own case or ticket number, so the query can be chased.</summary>
    public string? DisputeReference { get; set; }

    /// <summary>What we expect the carrier to credit. An expectation, not an entitlement.</summary>
    public decimal? ExpectedCreditAmount { get; set; }

    public DateTime? DisputeResolvedAt { get; set; }

    /// <summary>See <see cref="Domain.DisputeOutcome"/>. Stored as its code.</summary>
    public string? DisputeOutcome { get; set; }

    public string? DisputeResolutionNote { get; set; }

    /// <summary>
    /// What the company accepts it owes. Equal to <see cref="TotalAmount"/ > unless a query ended
    /// with the carrier crediting part of it — and it is <b>this</b> figure that would be paid,
    /// never the billed one.
    /// </summary>
    public decimal? ApprovedAmount { get; set; }

    public DateTime? ApprovedAt { get; set; }
    public int?      ApprovedBy { get; set; }

    /// <summary>Required when approving less than was billed.</summary>
    public string? ApprovalNote { get; set; }

    // ── Where it went in Finance (decision G10) ───────────────────────────────

    /// <summary>
    /// The payable this bill became, once approved. Null until it is.
    /// <para>
    /// Kept here rather than looked up on demand so a carrier bill can always say where the money
    /// went, even after somebody in Finance renames or reallocates the invoice.
    /// </para>
    /// </summary>
    public Guid?   PostedInvoiceUuid   { get; set; }
    public string? PostedInvoiceNumber { get; set; }
    public DateTime? PostedAt          { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<CarrierInvoiceLine> Lines { get; set; } = new List<CarrierInvoiceLine>();
}

/// <summary>
/// One charge on a carrier's bill.
/// <para>
/// The airway bill is what matching hangs off (T-54): it is the one reference a carrier always
/// prints and always knows, whereas our own consignment number appears only where the carrier
/// echoed a reference we sent it.
/// </para>
/// </summary>
internal class CarrierInvoiceLine : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int            CarrierInvoiceId { get; set; }
    public CarrierInvoice CarrierInvoice   { get; set; } = null!;

    public int LineNo { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>The carrier's airway bill for the movement being charged for.</summary>
    public string? AwbNumber { get; set; }

    /// <summary>Our consignment number, where the carrier echoed the reference we sent.</summary>
    public string? ConsignmentReference { get; set; }

    /// <summary>BASE, FUEL, COD and so on — the carrier's own code, unmapped.</summary>
    public string? ChargeCode { get; set; }

    public string? ServiceCode { get; set; }

    /// <summary>
    /// The weight the carrier says it billed on. Where it disagrees with ours, that disagreement
    /// is the most common reason a freight bill is wrong — and T-44 stored our figure so it could
    /// be put beside this one.
    /// </summary>
    public decimal? ChargeableWeightKg { get; set; }

    public DateTime? ShipDate { get; set; }

    /// <summary>Negative for a credit line. Zero is refused — it charges nothing and explains nothing.</summary>
    public decimal Amount { get; set; }

    // ── What it was charged against (T-54) ────────────────────────────────────

    /// <summary>
    /// The consignment this charge belongs to. <b>Many lines may point at one consignment</b> — base
    /// carriage and fuel are two lines for one movement — so this is deliberately not unique.
    /// </summary>
    public int?         MatchedConsignmentId { get; set; }
    public Consignment? MatchedConsignment   { get; set; }

    /// <summary>See <see cref="InvoiceLineMatchStatus"/>. Stored as its code.</summary>
    public string MatchStatus { get; set; } = LogisticsCode.Of(InvoiceLineMatchStatus.Unmatched);

    /// <summary>
    /// See <see cref="InvoiceLineMatchMethod"/>. Kept because a wrong match is only correctable by
    /// somebody who can see how it was made — matched on an airway bill and matched by hand are
    /// very different levels of confidence.
    /// </summary>
    public string? MatchMethod { get; set; }

    public DateTime? MatchedAt     { get; set; }
    public int?      MatchedByUser { get; set; }

    /// <summary>Why it is ambiguous, why it was excluded, or why somebody matched it by hand.</summary>
    public string? MatchNote { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
