using SMS.Shared.Common;

namespace SMS.Modules.Material.Domain;

internal class MaterialIssueVoucher : ITenantScopedEntity
{
    public int       Id          { get; set; }
    public Guid      UUID        { get; set; }
    public Guid      OrganizationId { get; set; }
    public string    IssueNo     { get; set; } = string.Empty;  // MIV-YYYY-NNNNN
    public int       MirId       { get; set; }
    public string    Status      { get; set; } = "DRAFT";        // DRAFT | POSTED | CANCELLED
    public string?   IssuedTo    { get; set; }
    public DateTime  IssueDate   { get; set; }
    public decimal   TotalValue  { get; set; }
    public string?   Notes       { get; set; }
    public int       CreatedBy   { get; set; }
    public DateTime  CreatedDate { get; set; }
    public int?      PostedBy    { get; set; }
    public DateTime? PostedDate  { get; set; }

    public MaterialIssueRequest                  MaterialIssueRequest { get; set; } = null!;
    public ICollection<MaterialIssueVoucherLine> Lines                { get; set; } = new List<MaterialIssueVoucherLine>();
}

internal class MaterialIssueVoucherLine : ITenantScopedEntity
{
    public int      Id              { get; set; }
    public Guid     UUID            { get; set; }
    public Guid     OrganizationId  { get; set; }
    public int      MivId           { get; set; }
    public int      MirLineId       { get; set; }
    // Denormalised from StockReservation — needed for atomic inventory update
    public int      InventoryItemId { get; set; }
    public Guid     VariantUuid     { get; set; }
    public string   ItemDescription { get; set; } = string.Empty;
    public string?  UnitOfMeasure   { get; set; }
    public decimal  IssuedQty       { get; set; }
    public decimal  UnitCost        { get; set; }   // snapshot at time of MIV creation
    public decimal  LineValue       { get; set; }   // IssuedQty × UnitCost
    public string?  Notes           { get; set; }

    public MaterialIssueVoucher       MaterialIssueVoucher      { get; set; } = null!;
    public MaterialIssueRequestDetail MirLine                   { get; set; } = null!;
    // Populated for batch/serial tracked products; empty for non-tracked
    public ICollection<MivLineBatchSerial> BatchSerials { get; set; } = new List<MivLineBatchSerial>();
}

// One record per batch-split or per serial selected for a given MIV line.
// For batch-tracked: each row = one batch consumed, IssuedQty = qty from that batch.
// For serial-tracked: each row = one serial unit, IssuedQty = 1.
internal class MivLineBatchSerial : ITenantScopedEntity
{
    public int       Id              { get; set; }
    public Guid      UUID            { get; set; }
    public Guid      OrganizationId  { get; set; }
    public int       MivLineId       { get; set; }
    public int       InventoryItemId { get; set; }  // specific batch/serial InventoryItem row
    public string?   BatchNumber     { get; set; }
    public string?   SerialNumber    { get; set; }
    public DateTime? ExpiryDate      { get; set; }
    public decimal   IssuedQty       { get; set; }  // = 1 for serial-tracked
    public decimal   UnitCost        { get; set; }  // snapshot at MIV creation
    public MaterialIssueVoucherLine MivLine { get; set; } = null!;
}
