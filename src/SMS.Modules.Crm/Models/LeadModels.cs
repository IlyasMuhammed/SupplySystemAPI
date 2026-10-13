namespace SMS.Modules.Crm.Models;

// A41 API-CONTRACT §3 — leads, conversion, kanban, duplicates, cross-references.

public sealed class CrmRef
{
    public Guid    Uuid   { get; set; }
    public string  Number { get; set; } = string.Empty;
    public string? Name   { get; set; }
}

public sealed class CrmAccountModel
{
    public Guid     Uuid         { get; set; }
    public string   Code         { get; set; } = string.Empty;
    public string   Name         { get; set; } = string.Empty;
    public bool     IsCustomer   { get; set; }
    public bool     IsVendor     { get; set; }
    public bool     IsActive     { get; set; }
    public string?  Phone        { get; set; }
    public string?  Email        { get; set; }
    public string?  CustomerType { get; set; }
    public DateTime CreatedAt    { get; set; }
}

public sealed class CrmContactModel
{
    public int     Id          { get; set; }
    public Guid    AccountUuid { get; set; }
    public string  Name        { get; set; } = string.Empty;
    public string? Title       { get; set; }
    public string? Phone       { get; set; }
    public string? Email       { get; set; }
    public bool    IsPrimary   { get; set; }
    public bool    IsActive    { get; set; }
}

public class LeadListItem
{
    public Guid      Uuid             { get; set; }
    public string    LeadNumber       { get; set; } = string.Empty;
    public string    FullName         { get; set; } = string.Empty;
    public string    Topic            { get; set; } = string.Empty;
    public string    CompanyName      { get; set; } = string.Empty;
    public string?   Email            { get; set; }
    public string?   Phone            { get; set; }
    public string?   MobilePhone      { get; set; }
    public Guid?     LeadSourceUuid   { get; set; }
    public string?   LeadSourceName   { get; set; }
    public Guid?     CampaignUuid     { get; set; }
    public string?   CampaignName     { get; set; }
    public string    Rating           { get; set; } = string.Empty;
    public string    Priority         { get; set; } = string.Empty;
    public string    Status           { get; set; } = string.Empty;
    public Guid?     PipelineStageUuid { get; set; }
    public string?   PipelineStageName { get; set; }
    public int?      AssignedToUserId { get; set; }
    public string?   AssignedToName   { get; set; }
    public Guid?     SalesTeamUuid    { get; set; }
    public string?   SalesTeamName    { get; set; }
    public decimal?  EstimatedRevenue { get; set; }
    public string?   CurrencyCode     { get; set; }
    public DateTime? EstimatedCloseDate { get; set; }
    public DateTime  CreatedAt        { get; set; }
}

public sealed class LeadDetail : LeadListItem
{
    public string?   Description          { get; set; }
    public string?   Salutation           { get; set; }
    public string    FirstName            { get; set; } = string.Empty;
    public string    LastName             { get; set; } = string.Empty;
    public string?   JobTitle             { get; set; }
    public string?   Website              { get; set; }
    public string?   AddressLine1         { get; set; }
    public string?   AddressLine2         { get; set; }
    public string?   City                 { get; set; }
    public string?   StateProvince        { get; set; }
    public string?   PostalCode           { get; set; }
    public string?   Country              { get; set; }
    public CrmAccountModel? Account       { get; set; }
    public CrmContactModel? ContactPerson { get; set; }
    public string?   SourceMedium         { get; set; }
    public string?   SourceReference      { get; set; }
    public string?   ExternalSourceSystem { get; set; }
    public string?   ExternalSourceId     { get; set; }
    public decimal?  BudgetAmount         { get; set; }
    public string?   BudgetCurrencyCode   { get; set; }
    public bool      BudgetConfirmed      { get; set; }
    public bool      AuthorityConfirmed   { get; set; }
    public bool      NeedIdentified       { get; set; }
    public string?   PurchaseTimeframe    { get; set; }
    public DateTime? AssignedAt           { get; set; }
    public int?      PreviousOwnerUserId  { get; set; }
    public string?   PreviousOwnerName    { get; set; }
    public int?      PipelineStageProbability { get; set; }
    public string?   StatusReason         { get; set; }
    public DateTime? QualifiedAt          { get; set; }
    public DateTime? DisqualifiedAt       { get; set; }
    public DateTime? ConvertedAt          { get; set; }
    public CrmRef?   ConvertedOpportunity { get; set; }
    public int       CreatedByUserId      { get; set; }
    public string?   CreatedByName        { get; set; }
    public DateTime  ModifiedAt           { get; set; }
    /// <summary>The status transitions this caller may make now (D-9) — drives the action buttons.</summary>
    public List<string> AllowedActions    { get; set; } = [];
    public bool      CanEdit              { get; set; }
    public int       ActivityCount        { get; set; }
}

public sealed class LeadUpsert
{
    public string?   Topic                { get; set; }
    public string?   Description          { get; set; }
    public string?   Salutation           { get; set; }
    public string?   FirstName            { get; set; }
    public string?   LastName             { get; set; }
    public string?   JobTitle             { get; set; }
    public string?   Email                { get; set; }
    public string?   Phone                { get; set; }
    public string?   MobilePhone          { get; set; }
    /// <summary>Blank = an individual (B2C): the full name is used (BR-03).</summary>
    public string?   CompanyName          { get; set; }
    public string?   Website              { get; set; }
    public string?   AddressLine1         { get; set; }
    public string?   AddressLine2         { get; set; }
    public string?   City                 { get; set; }
    public string?   StateProvince        { get; set; }
    public string?   PostalCode           { get; set; }
    public string?   Country              { get; set; }
    public Guid?     LeadSourceUuid       { get; set; }
    public Guid?     CampaignUuid         { get; set; }
    public string?   SourceMedium         { get; set; }
    public string?   SourceReference      { get; set; }
    public string?   ExternalSourceSystem { get; set; }
    public string?   ExternalSourceId     { get; set; }
    public decimal?  BudgetAmount         { get; set; }
    public string?   BudgetCurrencyCode   { get; set; }
    public bool?     BudgetConfirmed      { get; set; }
    public bool?     AuthorityConfirmed   { get; set; }
    public bool?     NeedIdentified       { get; set; }
    public string?   PurchaseTimeframe    { get; set; }
    public string?   Rating               { get; set; }
    public string?   Priority             { get; set; }
    public decimal?  EstimatedRevenue     { get; set; }
    public DateTime? EstimatedCloseDate   { get; set; }
    public string?   CurrencyCode         { get; set; }
    /// <summary>Create only (changes go through assign): default the caller; someone else needs CRM_ASSIGN_LEAD.</summary>
    public int?      AssignedToUserId     { get; set; }
    /// <summary>Create only (changes go through assign).</summary>
    public Guid?     SalesTeamUuid        { get; set; }
}

public sealed class LeadListFilter
{
    public string?   Search           { get; set; }
    /// <summary>One status or several, comma separated.</summary>
    public string?   Status           { get; set; }
    public Guid?     LeadSourceUuid   { get; set; }
    public Guid?     CampaignUuid     { get; set; }
    public Guid?     SalesTeamUuid    { get; set; }
    public string?   Rating           { get; set; }
    public int?      AssignedToUserId { get; set; }
    /// <summary>Only leads assigned to the caller.</summary>
    public bool      Mine             { get; set; }
    public bool      Unassigned       { get; set; }
    public DateTime? From             { get; set; }
    public DateTime? To               { get; set; }
    public string?   SortField        { get; set; }
    public string?   SortOrder        { get; set; }
    public int       Page             { get; set; } = 1;
    public int       PageSize         { get; set; } = 25;
}

public sealed class LeadKanbanFilter
{
    public string?   Search           { get; set; }
    public Guid?     SalesTeamUuid    { get; set; }
    public int?      AssignedToUserId { get; set; }
    public bool      Mine             { get; set; }
    public string?   Rating           { get; set; }
    public DateTime? From             { get; set; }
    public DateTime? To               { get; set; }
}

public sealed class KanbanColumn<T>
{
    public Guid     StageUuid      { get; set; }
    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public int      Sequence       { get; set; }
    public int?     WinProbability { get; set; }
    public bool     IsWon          { get; set; }
    public bool     IsLost         { get; set; }
    public int      Count          { get; set; }
    public decimal  TotalRevenue   { get; set; }
    public List<T>  Items          { get; set; } = [];
}

public sealed class KanbanBoard<T>
{
    public List<KanbanColumn<T>> Columns { get; set; } = [];
    /// <summary>Open records whose stage is missing or inactive.</summary>
    public List<T> Unstaged { get; set; } = [];
    /// <summary>Several currencies among the cards — column totals then add amounts as they are (no conversion).</summary>
    public bool MixedCurrencies { get; set; }
}

public sealed class LeadAssignRequest
{
    /// <summary>Null = unassigned.</summary>
    public int?  UserId        { get; set; }
    public Guid? SalesTeamUuid { get; set; }
}

public sealed class LeadStatusRequest
{
    public string? Status { get; set; }
    public string? Reason { get; set; }
}

public sealed class ReasonRequest
{
    public string? Reason { get; set; }
}

public sealed class StageMoveRequest
{
    public Guid StageUuid { get; set; }
}

public sealed class LeadDuplicateQuery
{
    public string? Email       { get; set; }
    public string? Phone       { get; set; }
    public string? CompanyName { get; set; }
    /// <summary>Leave this lead out (its own duplicate check).</summary>
    public Guid?   ExcludeUuid { get; set; }
}

public sealed class LeadDuplicate
{
    public LeadListItem Lead      { get; set; } = new();
    /// <summary>EMAIL, PHONE, COMPANY.</summary>
    public List<string> MatchedOn { get; set; } = [];
}

public sealed class LeadDuplicateResult
{
    public List<LeadDuplicate>    Leads    { get; set; } = [];
    public List<CrmAccountModel>  Accounts { get; set; } = [];
}

public sealed class CrossReferenceItem
{
    public Guid     Uuid       { get; set; }
    public string   LeadNumber { get; set; } = string.Empty;
    public string   Topic      { get; set; } = string.Empty;
    public string   Status     { get; set; } = string.Empty;
    public DateTime CreatedAt  { get; set; }
}

public sealed class CrossReferenceResult
{
    public CrmAccountModel?          Account         { get; set; }
    public CrmContactModel?          ContactPerson   { get; set; }
    public List<CrossReferenceItem>  AccountLeads    { get; set; } = [];
    public List<CrossReferenceItem>  ContactLeads    { get; set; } = [];
}

public sealed class LeadConvertRequest
{
    public Guid?     ExistingAccountUuid { get; set; }
    public int?      ExistingContactId   { get; set; }
    /// <summary>Optional overrides of what the opportunity carries from the lead.</summary>
    public string?   Topic               { get; set; }
    public decimal?  EstimatedRevenue    { get; set; }
    public DateTime? EstimatedCloseDate  { get; set; }
    public int?      AssignedToUserId    { get; set; }
}

public sealed class LeadConversionResult
{
    public Guid   OpportunityUuid   { get; set; }
    public string OpportunityNumber { get; set; } = string.Empty;
    public Guid   AccountUuid       { get; set; }
    public int    ContactId         { get; set; }
    public bool   IsNewAccount      { get; set; }
    public bool   IsNewContact      { get; set; }
}

/// <summary>§14.6 — the public lead API's body (snake_case on the wire: the controller binds it with its own options).</summary>
public sealed class PublicLeadRequest
{
    public string? FirstName            { get; set; }
    public string? LastName             { get; set; }
    public string? CompanyName          { get; set; }
    public string? Email                { get; set; }
    public string? Phone                { get; set; }
    public string? Topic                { get; set; }
    public string? Description          { get; set; }
    public string? SourceCode           { get; set; }
    public string? CampaignCode         { get; set; }
    public string? SourceMedium         { get; set; }
    public string? SourceReference      { get; set; }
    public string? ExternalSourceSystem { get; set; }
    public string? ExternalSourceId     { get; set; }
}

public sealed class PublicLeadResult
{
    public Guid   LeadId     { get; set; }
    public string LeadNumber { get; set; } = string.Empty;
}
