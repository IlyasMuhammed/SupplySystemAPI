using SMS.Shared.Common;

namespace SMS.Modules.Material.Domain;

internal class Project : ITenantScopedEntity
{
    public int      Id               { get; set; }
    public Guid     UUID             { get; set; }
    public Guid     OrganizationId   { get; set; }
    public string   ProjectCode      { get; set; } = string.Empty;
    public string   ProjectName      { get; set; } = string.Empty;
    public int      ProjectManagerId { get; set; }
    public Guid?    SiteWarehouseId  { get; set; }
    public string   Status           { get; set; } = "ACTIVE";
    public decimal  BudgetAmount     { get; set; }
    public string?  Description      { get; set; }
    public bool     IsActive         { get; set; } = true;
    public bool     IsDelete         { get; set; }
    public int      CreatedBy        { get; set; }
    public DateTime CreatedDate      { get; set; }
    public int?     ModifiedBy       { get; set; }
    public DateTime? ModifiedDate    { get; set; }
}

internal class MaterialIssueRequest : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     UUID           { get; set; }
    public Guid     TraceId        { get; set; }
    public Guid     OrganizationId { get; set; }
    public string   RequestNo      { get; set; } = string.Empty;
    public string   RequestType    { get; set; } = string.Empty;
    public int?     ProjectId      { get; set; }
    public string?  Department     { get; set; }
    public string?  MaintenanceRef { get; set; }
    public int      RequestedBy    { get; set; }
    public DateTime? RequiredDate  { get; set; }
    public string   Priority       { get; set; } = "MEDIUM";
    public string?  Purpose        { get; set; }
    public string   Status         { get; set; } = "DRAFT";
    public decimal  EstimatedValue { get; set; }
    public string?  RejectionReason  { get; set; }
    public string?  ApproverRemarks  { get; set; }
    public int?     ApprovedBy     { get; set; }
    public DateTime? ApprovedAt    { get; set; }
    public string?  Notes          { get; set; }
    public bool     IsActive       { get; set; } = true;
    public bool     IsDelete       { get; set; }
    public int      CreatedBy      { get; set; }
    public DateTime CreatedDate    { get; set; }
    public int?     ModifiedBy     { get; set; }
    public DateTime? ModifiedDate  { get; set; }

    public Project?  Project { get; set; }
    public ICollection<MaterialIssueRequestDetail> Lines { get; set; } = new List<MaterialIssueRequestDetail>();
}

internal class MaterialIssueRequestDetail : ITenantScopedEntity
{
    public int      Id                     { get; set; }
    public Guid     UUID                   { get; set; }
    public Guid     OrganizationId         { get; set; }
    public int      MaterialIssueRequestId { get; set; }
    public int      LineNo                 { get; set; }
    public Guid     ProductUuid            { get; set; }
    public string   ItemDescription        { get; set; } = string.Empty;
    public string?  UnitOfMeasure          { get; set; }
    public decimal  RequestedQty           { get; set; }
    public decimal  UnitCost               { get; set; }
    public decimal  EstimatedLineValue     { get; set; }
    public string?  Purpose                { get; set; }
    public string?  Notes                  { get; set; }
    public int?     WarehouseId            { get; set; }
    public string?  WarehouseName          { get; set; }
    public int?     PrLineId               { get; set; }
    // MIV tracking — updated atomically on each issue posting
    public decimal  IssuedQty              { get; set; }
    public decimal  PendingQty             { get; set; }
    public string   LineStatus             { get; set; } = "PENDING";  // PENDING | PARTIALLY_ISSUED | FULLY_ISSUED

    public MaterialIssueRequest MaterialIssueRequest { get; set; } = null!;
}
