using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// The warehouse's instruction sheet for one delivery: where to walk, what to take, how much.
/// <para>
/// It is a <b>document, not a view</b>. A pick list that were computed on each request would show
/// a different route every time stock moved underneath it, and the paper in the picker's hand
/// would stop matching the screen the supervisor is looking at. Persisting it also gives pick
/// confirmation (T-25) something to confirm <em>against</em> — without that, a short pick has no
/// original quantity to be short of.
/// </para>
/// <para>
/// Its lines are written from the stock reservation made at release (T-22), not from a fresh
/// choice of stock: the units were already allocated to specific rows, and choosing again here
/// would send a picker to a bin the delivery is not holding.
/// </para>
/// </summary>
internal class PickList : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int           DeliveryOrderId { get; set; }
    public DeliveryOrder DeliveryOrder   { get; set; } = null!;

    public string PickListNumber { get; set; } = string.Empty;

    /// <summary>See <see cref="PickListStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Denormalised from the allocation rather than copied from the delivery header: the delivery
    /// may not name a warehouse, but the stock it holds always sits in exactly one.
    /// </summary>
    public Guid    WarehouseUuid { get; set; }
    public string? WarehouseName { get; set; }

    /// <summary>Who is walking it. Bare user id, as everywhere else in this module.</summary>
    public int? AssignedToUserId { get; set; }

    public DateTime  GeneratedAt { get; set; }
    public DateTime? StartedAt   { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? CancelReason { get; set; }
    public string? Notes        { get; set; }

    public bool     IsActive     { get; set; } = true;
    public bool     IsDelete     { get; set; }
    public int      CreatedBy    { get; set; }
    public DateTime CreatedDate  { get; set; }
    public int?     ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<PickListLine> Lines { get; set; } = new List<PickListLine>();
}

/// <summary>
/// One stop on the walk: take this quantity of this batch out of this bin.
/// <para>
/// A delivery line can produce several of these — stock split across batches or bins is normal,
/// and it is exactly why a pick list is a separate document rather than a rendering of the
/// delivery's own lines.
/// </para>
/// </summary>
internal class PickListLine : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int      PickListId { get; set; }
    public PickList PickList   { get; set; } = null!;

    public int               DeliveryOrderLineId { get; set; }
    public DeliveryOrderLine DeliveryOrderLine   { get; set; } = null!;

    /// <summary>Walk order, 1..n. See the sequencing note in <c>PickListRepository</c>.</summary>
    public int SeqNo { get; set; }

    /// <summary>
    /// The reservation row this instruction draws down. Bare UUID — reservations live in the
    /// Inventory module — and the link that lets T-25 consume exactly what was picked.
    /// </summary>
    public Guid ReservationUuid { get; set; }

    public Guid?   VariantUuid     { get; set; }
    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure   { get; set; }

    // ── Where to walk ─────────────────────────────────────────────────────────
    // Snapshotted, not joined: bins live in another module, and a pick list printed on Monday has
    // to still say where the picker was sent even if the stock is moved on Tuesday.

    public string? ZoneName { get; set; }
    public string? BinCode  { get; set; }

    // ── What to take ──────────────────────────────────────────────────────────

    public string?   BatchNumber  { get; set; }
    public string?   SerialNumber { get; set; }
    public DateTime? ExpiryDate   { get; set; }

    public decimal QtyToPick { get; set; }
    public decimal QtyPicked { get; set; }
    public decimal QtyShort  { get; set; }

    /// <summary>See <see cref="PickShortReason"/>. Required whenever the line comes up short.</summary>
    public string? ShortReasonCode { get; set; }

    /// <summary>Free text alongside the code, for what the code cannot carry.</summary>
    public string? ShortReason { get; set; }

    /// <summary>
    /// When the picker reported on this instruction — <b>not</b> when stock was taken. A line
    /// confirmed for zero is still confirmed, which is how "every line has been answered" is
    /// distinguished from "nobody has walked this yet".
    /// </summary>
    public DateTime? PickedAt { get; set; }
    public int?      PickedBy { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
