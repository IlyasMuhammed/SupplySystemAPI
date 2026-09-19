using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Domain;

/// <summary>
/// A hold on stock, whatever put it there.
/// <para>
/// This is the detail behind <see cref="InventoryItem.QtyReserved"/>. That counter is what
/// availability is computed from; these rows are what say who is holding the units and what to
/// release them against.
/// </para>
/// <para>
/// <b>Source-agnostic on purpose.</b> The original reservation table lived in
/// SMS.Modules.Material with a non-nullable MIR id, so anything else that needed to reserve —
/// deliveries now, sales orders later — would have had to invent its own table. Three tables
/// holding the same stock means "does the sum of active holds equal QtyReserved?" becomes a union
/// across all of them, and any one that gets forgotten is a silent over-promise.
/// </para>
/// </summary>
internal class StockReservation : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>The exact stock row being held — variant × warehouse × batch.</summary>
    public int            InventoryItemId { get; set; }
    public InventoryItem? InventoryItem   { get; set; }

    /// <summary>Denormalised from the inventory item so a reservation reads without a join.</summary>
    public Guid VariantUuid { get; set; }
    public int  WarehouseId { get; set; }

    public decimal ReservedQty { get; set; }

    /// <summary>See <see cref="ReservationSourceType"/> — MIR, DELIVERY, SALES_ORDER.</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>The document holding the stock. A bare UUID: the source lives in another module.</summary>
    public Guid  SourceUuid     { get; set; }
    public Guid? SourceLineUuid { get; set; }

    /// <summary>
    /// ACTIVE — held. RELEASED — given back, the units returned to available. CONSUMED — the
    /// stock actually left. Released and consumed decrement the counter identically; the
    /// difference is what the audit trail says happened.
    /// </summary>
    public string Status { get; set; } = "ACTIVE";

    /// <summary>
    /// A hold someone has questioned — stale, or suspected wrong. Carried across from the MIR
    /// reservation table because the Reserved Stock report surfaces it; nothing sets it yet.
    /// </summary>
    public bool      IsFlagged { get; set; }
    public DateTime? FlaggedAt { get; set; }

    public DateTime  ReservedAt { get; set; }
    public int       ReservedBy { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public int?      ReleasedBy { get; set; }
    public string?   ReleaseReason { get; set; }

    internal const string StatusActive   = "ACTIVE";
    internal const string StatusReleased = "RELEASED";
    internal const string StatusConsumed = "CONSUMED";
}
