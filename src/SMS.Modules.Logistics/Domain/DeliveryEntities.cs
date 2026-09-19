using System.ComponentModel.DataAnnotations.Schema;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// Layer A — the commitment to move goods. SAP calls this an Outbound/Inbound Delivery, D365 a Load.
/// <para>
/// Source-agnostic on purpose: it exists before any carrier is chosen and survives a carrier
/// change. That is the whole reason it is a separate object from the consignment — the legacy
/// <c>Shipment</c> fused the two and could therefore never answer "which units are on this truck".
/// </para>
/// </summary>
internal class DeliveryOrder : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Lineage back through GRN → PO → RFQ → PR, copied from the source document.</summary>
    public Guid TraceId { get; set; }

    /// <summary>DLV-YYYY-NNNNN.</summary>
    public string DeliveryNumber { get; set; } = string.Empty;

    /// <summary>See <see cref="DeliveryDirection"/>. Stored as its code.</summary>
    public string Direction { get; set; } = string.Empty;

    // ── Source document ───────────────────────────────────────────────────────

    /// <summary>See <see cref="DeliverySourceType"/>. Stored as its code.</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Cross-module reference — a bare UUID with no FK, as <c>GrnLine.PoLineUuid</c> is.</summary>
    public Guid?   SourceUuid   { get; set; }
    public string? SourceNumber { get; set; }

    /// <summary>
    /// Whether this delivery is the document that posts the stock movement.
    /// <para>
    /// <b>Computed, never stored.</b> It is a property of the source type, and persisting it as a
    /// column would let a bad import or a careless edit set it wrong — the failure mode being a
    /// silently double-deducted ledger. Deriving it means the rule has exactly one home:
    /// <see cref="DeliverySourceTypeInfo"/>.
    /// </para>
    /// </summary>
    [NotMapped]
    public bool PostsGoodsIssue =>
        DeliverySourceTypeInfo.PostsGoodsIssue(LogisticsCode.Parse<DeliverySourceType>(SourceType));

    // ── Addresses ─────────────────────────────────────────────────────────────
    // Real foreign keys: addresses live in this DbContext, unlike the cross-module references above.

    public int?     ShipFromAddressId { get; set; }
    public Address? ShipFromAddress   { get; set; }

    public int?     ShipToAddressId { get; set; }
    public Address? ShipToAddress   { get; set; }

    /// <summary>
    /// The warehouse the goods leave from, and the one they arrive at. Bare UUIDs with no FK —
    /// warehouses live in the Inventory module.
    /// <para>
    /// Separate from the addresses because they answer a different question: an address is where
    /// a courier drives, a warehouse is which stock ledger moves. A transfer needs both ends, a
    /// PO delivery needs only the destination, and stock reservation (T-22) and goods issue
    /// (T-27) both key off these rather than off an address.
    /// </para>
    /// </summary>
    public Guid? ShipFromWarehouseUuid { get; set; }
    public Guid? ShipToWarehouseUuid   { get; set; }

    // ── Planning ──────────────────────────────────────────────────────────────

    public DateTime? RequestedDate { get; set; }
    public DateTime? PromisedDate  { get; set; }

    /// <summary>See <see cref="DeliveryPriority"/>. Stored as its code.</summary>
    public string Priority { get; set; } = LogisticsCode.Of(DeliveryPriority.Normal);

    /// <summary>
    /// EXW, FOB, CIF, DDP and so on. Deliberately free text rather than an enum — the Incoterms
    /// set is revised periodically and a contract may quote an older edition.
    /// </summary>
    public string? Incoterm { get; set; }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>See <see cref="DeliveryStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = LogisticsCode.Of(DeliveryStatus.Draft);

    /// <summary>
    /// The status a hold interrupted, so resuming returns exactly there rather than to DRAFT.
    /// Null whenever the delivery is not ON_HOLD.
    /// </summary>
    public string?   StatusBeforeHold { get; set; }
    public string?   HoldReason       { get; set; }
    public DateTime? HeldAt           { get; set; }
    public int?      HeldBy           { get; set; }

    /// <summary>
    /// Why the delivery was cancelled or short-closed, and by whom. One pair of fields for both,
    /// because the status already says which happened and a delivery can only be closed once.
    /// <para>
    /// A reason is required for both: a delivery that stops short of what was ordered leaves
    /// someone downstream — a site waiting for material, a supplier expecting a return — asking
    /// why, and "cancelled" on its own does not answer that.
    /// </para>
    /// </summary>
    public string?   ClosureReason { get; set; }
    public DateTime? ClosedAt      { get; set; }
    public int?      ClosedBy      { get; set; }

    /// <summary>
    /// When the approval workflow cleared this delivery to be issued. Null when it has not been
    /// approved, or when approval was never required.
    /// <para>
    /// The approving user is deliberately not stored here — <c>IDocumentStatusHandler</c> is not
    /// told who acted, and the workflow engine's own history is the authoritative record of that.
    /// Duplicating it from a source that does not have it would mean inventing it.
    /// </para>
    /// </summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// When the stock left the books, and who issued it.
    /// <para>
    /// Stored rather than inferred from the status, because the status only says the delivery is
    /// <em>past</em> goods issue — it moves on to IN_TRANSIT and DELIVERED — while this is the
    /// timestamp the inventory movement has to be reconciled against.
    /// </para>
    /// </summary>
    public DateTime? GoodsIssuedAt { get; set; }
    public int?      GoodsIssuedBy { get; set; }

    /// <summary>
    /// True for rows backfilled from the legacy <c>shipments</c> table in T-16, which recorded a
    /// weight and a PO number but no lines. Such a delivery must never render as a complete one —
    /// it is missing the very thing the rebuild exists to add.
    /// </summary>
    public bool LinesUnknown { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Optimistic concurrency, following <c>DocumentTimeline</c>. Release and goods issue are the
    /// two transitions with financial consequence; neither may be lost to a last-write-wins race.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<DeliveryOrderLine>  Lines      { get; set; } = new List<DeliveryOrderLine>();
    public ICollection<ShipmentPackage>    Packages   { get; set; } = new List<ShipmentPackage>();
    public ICollection<ConsignmentDelivery> Consignments { get; set; } = new List<ConsignmentDelivery>();
}

internal class DeliveryOrderLine : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int            DeliveryOrderId { get; set; }
    public DeliveryOrder  DeliveryOrder   { get; set; } = null!;

    public int LineNo { get; set; }

    /// <summary>
    /// The variant being moved. Nullable because the source modules disagree: PO and MIV lines
    /// carry a <c>VariantUuid</c>, SRO lines carry a <c>ProductUuid</c>. Both are kept so a line
    /// can always be traced back to whichever the source actually had.
    /// </summary>
    public Guid? VariantUuid { get; set; }
    public Guid? ProductUuid { get; set; }

    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure   { get; set; }

    // ── Quantities ────────────────────────────────────────────────────────────
    // Each stage records its own number rather than mutating one. A short pick, a partial pack
    // and a partial delivery are all normal, and collapsing them loses the ability to say where
    // the shortfall happened.

    public decimal QtyOrdered  { get; set; }
    public decimal QtyPicked   { get; set; }
    public decimal QtyPacked   { get; set; }
    public decimal QtyShipped  { get; set; }
    public decimal QtyDelivered { get; set; }
    public decimal QtyShort    { get; set; }

    public string? ShortReason { get; set; }

    public string? BatchNumber  { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>The PO / SRO / MIV line this came from. Bare UUID, no FK.</summary>
    public Guid? SourceLineUuid { get; set; }

    /// <summary>Bin to pick from. Bare UUID — bins live in the Inventory module.</summary>
    public Guid? BinUuid { get; set; }

    public decimal? UnitValue { get; set; }

    public bool IsHazardous            { get; set; }
    public bool IsFragile              { get; set; }
    public bool IsTemperatureControlled { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }

    public ICollection<PackageContent> PackageContents { get; set; } = new List<PackageContent>();
}
