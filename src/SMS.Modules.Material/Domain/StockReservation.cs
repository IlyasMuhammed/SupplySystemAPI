using SMS.Shared.Common;

namespace SMS.Modules.Material.Domain;

internal class StockReservation : ITenantScopedEntity
{
    public int       Id              { get; set; }
    public Guid      UUID            { get; set; }
    public Guid      OrganizationId  { get; set; }
    public int       MirId           { get; set; }
    public int       MirLineId       { get; set; }
    // Cross-schema reference — stored as plain int (no EF navigation to inventory schema)
    public int       InventoryItemId { get; set; }
    public Guid      VariantUuid     { get; set; }   // denormalised from InventoryItem
    public int       WarehouseId     { get; set; }   // denormalised from InventoryItem
    public decimal   ReservedQty     { get; set; }
    public string    Status          { get; set; } = "ACTIVE";  // ACTIVE | RELEASED | CONSUMED | FLAGGED
    public DateTime  ReservedAt      { get; set; }
    public DateTime? ReleasedAt      { get; set; }
    public string?   ReleaseReason   { get; set; }
    public bool      IsFlagged       { get; set; }
    public DateTime? FlaggedAt       { get; set; }

    public MaterialIssueRequest       MaterialIssueRequest       { get; set; } = null!;
    public MaterialIssueRequestDetail MaterialIssueRequestDetail { get; set; } = null!;
}
