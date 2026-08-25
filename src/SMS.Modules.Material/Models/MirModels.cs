using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Models;

// ── Requests ──────────────────────────────────────────────────────────────────

public class CreateMirRequest
{
    public string   RequestType    { get; set; } = string.Empty; // PROJECT | DEPARTMENT | MAINTENANCE
    public Guid?    ProjectUuid    { get; set; }
    public string?  Department     { get; set; }
    public string?  MaintenanceRef { get; set; }
    public DateTime? RequiredDate  { get; set; }
    public string   Priority       { get; set; } = "MEDIUM";
    public string?  Purpose        { get; set; }
    public string?  Notes          { get; set; }
    public List<CreateMirLineRequest> Lines { get; set; } = [];
}

public class CreateMirLineRequest
{
    public Guid    VariantUuid  { get; set; }
    public decimal RequestedQty { get; set; }
    public int?    WarehouseId  { get; set; }
    public string? Purpose      { get; set; }
    public string? Notes        { get; set; }
    public Guid?   PrLineId     { get; set; }
}

public class PatchMirRequest
{
    public Guid?    ProjectUuid    { get; set; }
    public string?  Department     { get; set; }
    public string?  MaintenanceRef { get; set; }
    public DateTime? RequiredDate  { get; set; }
    public string?  Priority       { get; set; }
    public string?  Purpose        { get; set; }
    public string?  Notes          { get; set; }
    public List<CreateMirLineRequest>? Lines { get; set; }
}

public class RejectMirRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class ApproveMirRequest
{
    public string? Remarks { get; set; }
}

// ── Filters ───────────────────────────────────────────────────────────────────

public class MirListFilter
{
    public string?   Status      { get; set; }
    public string?   RequestType { get; set; }
    public Guid?     ProjectUuid { get; set; }
    public string?   Department  { get; set; }
    public DateTime? DateFrom    { get; set; }
    public DateTime? DateTo      { get; set; }
    public string?   Search      { get; set; }
    public int       Page        { get; set; } = 1;
    public int       PageSize    { get; set; } = 20;
}

// ── Response models ───────────────────────────────────────────────────────────

public class MirListItemModel
{
    public Guid      UUID           { get; set; }
    public Guid      TraceId        { get; set; }
    public string    RequestNo      { get; set; } = string.Empty;
    public string    RequestType    { get; set; } = string.Empty;
    public string?   ProjectName    { get; set; }
    public string?   Department     { get; set; }
    public string?   MaintenanceRef { get; set; }
    public string    Status         { get; set; } = string.Empty;
    public string    Priority       { get; set; } = string.Empty;
    public decimal   EstimatedValue { get; set; }
    public DateTime? RequiredDate   { get; set; }
    public DateTime  CreatedDate    { get; set; }
    public int       TotalLines     { get; set; }
}

public class MirDetailModel
{
    public Guid      UUID             { get; set; }
    public Guid      TraceId          { get; set; }
    public string    RequestNo        { get; set; } = string.Empty;
    public string    RequestType      { get; set; } = string.Empty;
    public Guid?     ProjectUuid      { get; set; }
    public string?   ProjectName      { get; set; }
    public string?   Department       { get; set; }
    public string?   MaintenanceRef   { get; set; }
    public int       RequestedBy      { get; set; }
    public DateTime? RequiredDate     { get; set; }
    public string    Priority         { get; set; } = string.Empty;
    public string?   Purpose          { get; set; }
    public string    Status           { get; set; } = string.Empty;
    public decimal   EstimatedValue   { get; set; }
    public string?   RejectionReason  { get; set; }
    public string?   ApproverRemarks  { get; set; }
    public int?      ApprovedBy       { get; set; }
    public DateTime? ApprovedAt       { get; set; }
    public string?   Notes            { get; set; }
    public int       CreatedBy        { get; set; }
    public DateTime  CreatedDate      { get; set; }
    public List<MirLineModel> Lines   { get; set; } = [];

    // Populated when Status = PENDING_APPROVAL
    public Guid?   ActiveApprovalUuid  { get; set; }
    public int?    ActiveStepNumber    { get; set; }
    public string? ActiveStepName      { get; set; }
}

public class MirLineModel
{
    public Guid    UUID               { get; set; }
    public int     LineNo             { get; set; }
    public Guid    VariantUuid        { get; set; }
    public string? VariantSku         { get; set; }
    public string? VariantName        { get; set; }
    public string? ProductName        { get; set; }
    public string? ProductImageUrl    { get; set; }
    public string  ItemDescription    { get; set; } = string.Empty;
    public string? UnitOfMeasure      { get; set; }
    public decimal RequestedQty       { get; set; }
    public decimal UnitCost           { get; set; }
    public decimal EstimatedLineValue { get; set; }
    public string? Purpose            { get; set; }
    public string? Notes              { get; set; }
    public int?    WarehouseId        { get; set; }
    public string? WarehouseName      { get; set; }
    public Guid?   PrLineId           { get; set; }

    // Populated during PENDING_APPROVAL — highest step's approved qty for this line.
    public decimal? LatestApprovedQty { get; set; }
}

// ── Workflow request/response models ─────────────────────────────────────────

public class MirWorkflowSubmitRequest
{
    // No extra fields — all data comes from the MIR itself at submit time.
}

public class MirLineApprovalInput
{
    public Guid    LineUuid    { get; set; }
    public decimal ApprovedQty { get; set; }
}

public class MirWorkflowApproveRequest
{
    public Guid   ApprovalUUID  { get; set; }
    public string? Remarks      { get; set; }
    public List<MirLineApprovalInput> LineApprovals { get; set; } = [];
}

public class MirWorkflowRejectRequest
{
    public Guid   ApprovalUUID { get; set; }
    public string Reason       { get; set; } = string.Empty;
}

// ── Stock availability models ─────────────────────────────────────────────────

public class MirLineAvailabilityModel
{
    public Guid     LineUuid           { get; set; }
    public Guid     VariantUuid        { get; set; }
    public string   ItemDescription    { get; set; } = string.Empty;
    public decimal  RequestedQty       { get; set; }
    public decimal? LatestApprovedQty  { get; set; }
    public decimal  QtyOnHand          { get; set; }
    public decimal  QtyReserved        { get; set; }
    public decimal  QtyAvailable       { get; set; }
    public decimal? ReorderPoint       { get; set; }
    public string?  WarehouseName      { get; set; }
    public bool     IsAvailable        { get; set; }
}

public class MirStockAvailabilityResponse
{
    public Guid?                          WarehouseUuid  { get; set; }
    public string?                        WarehouseName  { get; set; }
    public List<MirLineAvailabilityModel> Lines          { get; set; } = [];
}
