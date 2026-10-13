using SMS.Shared.Common;

namespace SMS.Modules.Crm.Domain;

// A41 (docs/crm/ADDENDUM-41-ANALYSIS.md D-2): int Id + Guid Uuid, Guid OrganizationId (tenant filter), PascalCase columns,
// strings for statuses (CrmCodes.cs), users as int ids. References inside the CRM are real int FKs; references to other
// modules are unenforced: AccountUuid → suppliers.BusinessPartners.UUID, ContactPersonId → suppliers.SupplierContacts.Id,
// Source*Uuid → demand documents' UUID. Currency = 3-letter code. Soft-deleted rows (IsDeleted) are filtered explicitly.

/// <summary>§4.2 M2 — how leads reach the organization (14 seeded per org, D-7; organizations add their own).</summary>
internal class CrmLeadSource : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public string?  Description    { get; set; }
    public bool     IsActive       { get; set; } = true;
    public int      SortOrder      { get; set; }
    /// <summary>One of the seeded sources — its code cannot be changed.</summary>
    public bool     IsSystem       { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt     { get; set; } = DateTime.UtcNow;
}

/// <summary>§4.3 M3 — a marketing initiative leads are attributed to.</summary>
internal class CrmCampaign : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                { get; set; }
    public Guid      Uuid              { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId    { get; set; }
    public string    Code              { get; set; } = string.Empty;
    public string    Name              { get; set; } = string.Empty;
    public string?   Description       { get; set; }
    public string    CampaignType      { get; set; } = "GENERAL";
    public int?      LeadSourceId      { get; set; }
    public string    Status            { get; set; } = CampaignStatuses.Draft;
    public DateTime? StartDate         { get; set; }
    public DateTime? EndDate           { get; set; }
    public decimal?  BudgetAmount      { get; set; }
    public string?   CurrencyCode      { get; set; }
    public int?      ExpectedLeads     { get; set; }
    /// <summary>Running count, incremented when a lead is created with this campaign.</summary>
    public int       ActualLeads       { get; set; }
    public string?   ExternalReference { get; set; }
    public string?   Notes             { get; set; }
    public int       CreatedBy         { get; set; }
    public DateTime  CreatedAt         { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt        { get; set; } = DateTime.UtcNow;
    public bool      IsDeleted         { get; set; }

    public CrmLeadSource? LeadSource { get; set; }
}

/// <summary>§5.2 M4 — a group of salespeople (own pipeline stages optional).</summary>
internal class CrmSalesTeam : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                { get; set; }
    public Guid     Uuid              { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId    { get; set; }
    public string   Code              { get; set; } = string.Empty;
    public string   Name              { get; set; } = string.Empty;
    public string?  Description       { get; set; }
    public int?     TeamLeaderUserId  { get; set; }
    public bool     IsActive          { get; set; } = true;
    public int?     DefaultCampaignId { get; set; }
    public DateTime CreatedAt         { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt        { get; set; } = DateTime.UtcNow;

    public CrmCampaign? DefaultCampaign { get; set; }
    public ICollection<CrmSalesTeamMember> Members { get; set; } = new List<CrmSalesTeamMember>();
}

/// <summary>§5.3 M5 — a salesperson in a team; a user may be in several teams.</summary>
internal class CrmSalesTeamMember : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public int      SalesTeamId    { get; set; }
    public int      UserId         { get; set; }
    public string   Role           { get; set; } = TeamMemberRoles.Member;
    public bool     IsActive       { get; set; } = true;
    public DateTime JoinedAt       { get; set; } = DateTime.UtcNow;

    public CrmSalesTeam SalesTeam { get; set; } = null!;
}

/// <summary>§7.2 M6 — a column of the lead or opportunity pipeline; SalesTeamId null = the organization's default pipeline.</summary>
internal class CrmPipelineStage : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public string   StageType      { get; set; } = StageTypes.Lead;
    public int?     SalesTeamId    { get; set; }
    public int      Sequence       { get; set; }
    public int?     WinProbability { get; set; }
    public bool     IsWon          { get; set; }
    public bool     IsLost         { get; set; }
    public string?  Description    { get; set; }
    public bool     IsActive       { get; set; } = true;
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt     { get; set; } = DateTime.UtcNow;

    public CrmSalesTeam? SalesTeam { get; set; }
}

/// <summary>§6.2 M7 — an unqualified prospect; exists without a business partner until conversion.</summary>
internal class CrmLead : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                    { get; set; }
    public Guid      Uuid                  { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId        { get; set; }
    /// <summary>LD-YYYY-NNNNN, unique per organization (D-8).</summary>
    public string    LeadNumber            { get; set; } = string.Empty;

    public string    Topic                 { get; set; } = string.Empty;
    public string?   Description           { get; set; }

    public string?   Salutation            { get; set; }
    public string    FirstName             { get; set; } = string.Empty;
    public string    LastName              { get; set; } = string.Empty;
    /// <summary>FirstName + ' ' + LastName — stored and kept in step by <see cref="SetName"/> (a SQL computed column
    /// would not exist in the in-memory test store, D-2).</summary>
    public string    FullName              { get; set; } = string.Empty;
    public string?   JobTitle              { get; set; }
    public string?   Email                 { get; set; }
    public string?   Phone                 { get; set; }
    public string?   MobilePhone           { get; set; }
    /// <summary>For an individual (B2C) the person's full name (BR-03).</summary>
    public string    CompanyName           { get; set; } = string.Empty;
    public string?   Website               { get; set; }
    public string?   AddressLine1          { get; set; }
    public string?   AddressLine2          { get; set; }
    public string?   City                  { get; set; }
    public string?   StateProvince         { get; set; }
    public string?   PostalCode            { get; set; }
    public string?   Country               { get; set; }

    public Guid?     AccountUuid           { get; set; }
    public int?      ContactPersonId       { get; set; }

    public int?      LeadSourceId          { get; set; }
    public int?      CampaignId            { get; set; }
    public string?   SourceMedium          { get; set; }
    public string?   SourceReference       { get; set; }
    public string?   ExternalSourceSystem  { get; set; }
    public string?   ExternalSourceId      { get; set; }

    public decimal?  BudgetAmount          { get; set; }
    public string?   BudgetCurrencyCode    { get; set; }
    public bool      BudgetConfirmed       { get; set; }
    public bool      AuthorityConfirmed    { get; set; }
    public bool      NeedIdentified        { get; set; }
    public string?   PurchaseTimeframe     { get; set; }

    public string    Rating                { get; set; } = LeadRatings.Warm;
    public string    Priority              { get; set; } = LeadPriorities.Normal;

    public int?      AssignedToUserId      { get; set; }
    public DateTime? AssignedAt            { get; set; }
    public int?      SalesTeamId           { get; set; }
    public int?      PreviousOwnerUserId   { get; set; }

    public int?      PipelineStageId       { get; set; }

    public string    Status                { get; set; } = LeadStatuses.New;
    public string?   StatusReason          { get; set; }
    public DateTime? QualifiedAt           { get; set; }
    public DateTime? DisqualifiedAt        { get; set; }
    public DateTime? ConvertedAt           { get; set; }

    public Guid?     ConvertedAccountUuid  { get; set; }
    public int?      ConvertedContactId    { get; set; }
    public int?      ConvertedOpportunityId { get; set; }

    public decimal?  EstimatedRevenue      { get; set; }
    public DateTime? EstimatedCloseDate    { get; set; }
    public string?   CurrencyCode          { get; set; }

    /// <summary>0 = created through the public lead API (CreatedByName holds the key's name).</summary>
    public int       CreatedByUserId       { get; set; }
    public string?   CreatedByName         { get; set; }
    public DateTime  CreatedAt             { get; set; } = DateTime.UtcNow;
    public int       UpdatedByUserId       { get; set; }
    public DateTime  ModifiedAt            { get; set; } = DateTime.UtcNow;
    public bool      IsDeleted             { get; set; }
    public Guid      TraceId               { get; set; } = Guid.NewGuid();

    public CrmLeadSource?    LeadSource    { get; set; }
    public CrmCampaign?      Campaign      { get; set; }
    public CrmSalesTeam?     SalesTeam     { get; set; }
    public CrmPipelineStage? PipelineStage { get; set; }

    public void SetName(string firstName, string lastName)
    {
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        FullName = $"{FirstName} {LastName}".Trim();
    }
}

/// <summary>§9.2 M8 — a qualified deal; only ever created by lead conversion (BR-11).</summary>
internal class CrmOpportunity : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                   { get; set; }
    public Guid      Uuid                 { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId       { get; set; }
    /// <summary>OP-YYYY-NNNNN, unique per organization (D-8).</summary>
    public string    OpportunityNumber    { get; set; } = string.Empty;
    public string    Topic                { get; set; } = string.Empty;
    public string?   Description          { get; set; }
    public Guid      AccountUuid          { get; set; }
    public int?      ContactPersonId      { get; set; }
    public int       SourceLeadId         { get; set; }
    public int?      CampaignId           { get; set; }
    public int?      PipelineStageId      { get; set; }
    public string    Status               { get; set; } = OpportunityStatuses.Open;
    public decimal?  EstimatedRevenue     { get; set; }
    public decimal?  ActualRevenue        { get; set; }
    public string?   CurrencyCode         { get; set; }
    public DateTime? EstimatedCloseDate   { get; set; }
    public DateTime? ActualCloseDate      { get; set; }
    public string?   WinLossReason        { get; set; }
    public string?   CompetitorName       { get; set; }
    public int?      AssignedToUserId     { get; set; }
    public int?      SalesTeamId          { get; set; }
    /// <summary>D-13 — latest linked Demand documents (Demand owns the link; refreshed from it).</summary>
    public Guid?     SourceInquiryUuid    { get; set; }
    public Guid?     SourceQuotationUuid  { get; set; }
    public Guid?     SourceOrderUuid      { get; set; }
    public int       CreatedByUserId      { get; set; }
    public DateTime  CreatedAt            { get; set; } = DateTime.UtcNow;
    public int       UpdatedByUserId      { get; set; }
    public DateTime  ModifiedAt           { get; set; } = DateTime.UtcNow;
    public bool      IsDeleted            { get; set; }
    public Guid      TraceId              { get; set; } = Guid.NewGuid();

    public CrmLead           SourceLead    { get; set; } = null!;
    public CrmCampaign?      Campaign      { get; set; }
    public CrmPipelineStage? PipelineStage { get; set; }
    public CrmSalesTeam?     SalesTeam     { get; set; }
}

/// <summary>§10.2 M9 — one interaction or task, on a lead and/or an opportunity (BR-17).</summary>
internal class CrmActivity : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                { get; set; }
    public Guid      Uuid              { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId    { get; set; }
    public int?      LeadId            { get; set; }
    public int?      OpportunityId     { get; set; }
    public string    ActivityType      { get; set; } = ActivityTypes.Note;
    public string    Subject           { get; set; } = string.Empty;
    /// <summary>Sanitized HTML (D-21, BR-19).</summary>
    public string?   Description       { get; set; }
    public string?   Outcome           { get; set; }
    public string?   OutcomeNotes      { get; set; }
    public DateTime  ActivityDate      { get; set; } = DateTime.UtcNow;
    public int?      DurationMinutes   { get; set; }
    public DateTime? ScheduledDate     { get; set; }
    public DateTime? DueDate           { get; set; }
    public DateTime? CompletedDate     { get; set; }
    public bool      IsCompleted       { get; set; }
    public int       PerformedByUserId { get; set; }
    public int?      ContactPersonId   { get; set; }
    public DateTime? FollowUpDate      { get; set; }
    public string?   FollowUpType      { get; set; }
    public string?   FollowUpNotes     { get; set; }
    public int?      PipelineStageId   { get; set; }
    public int       CreatedByUserId   { get; set; }
    public DateTime  CreatedAt         { get; set; } = DateTime.UtcNow;
    public int       UpdatedByUserId   { get; set; }
    public DateTime  ModifiedAt        { get; set; } = DateTime.UtcNow;
    public bool      IsDeleted         { get; set; }

    public CrmLead?          Lead          { get; set; }
    public CrmOpportunity?   Opportunity   { get; set; }
    public CrmPipelineStage? PipelineStage { get; set; }
    public ICollection<CrmActivityParticipant> Participants { get; set; } = new List<CrmActivityParticipant>();
}

/// <summary>§10.3 M10 — who took part (internal user, customer contact or an outsider).</summary>
internal class CrmActivityParticipant : ITenantScopedEntity
{
    public int     Id              { get; set; }
    public Guid    OrganizationId  { get; set; }
    public int     ActivityId      { get; set; }
    public string  ParticipantType { get; set; } = ParticipantTypes.InternalUser;
    public int?    UserId          { get; set; }
    public int?    ContactPersonId { get; set; }
    public string? ExternalName    { get; set; }
    public string? ExternalEmail   { get; set; }
    public string? Role            { get; set; }

    public CrmActivity Activity { get; set; } = null!;
}

/// <summary>§11.2 M11 — a document shared with or received from the prospect, with direction and stage context.</summary>
internal class CrmActivityDocument : ITenantScopedEntity
{
    public int      Id              { get; set; }
    public Guid     Uuid            { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId  { get; set; }
    public int?     ActivityId      { get; set; }
    public int?     LeadId          { get; set; }
    public int?     OpportunityId   { get; set; }
    public string   DocumentType    { get; set; } = "OTHER";
    public string   DocumentName    { get; set; } = string.Empty;
    public int      FileId          { get; set; }
    public string   Direction       { get; set; } = DocumentDirections.Internal;
    public int?     PipelineStageId { get; set; }
    public string?  Notes           { get; set; }
    public int      SharedByUserId  { get; set; }
    public DateTime SharedAt        { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
    public bool     IsDeleted       { get; set; }

    public CrmActivity?      Activity      { get; set; }
    public CrmActivityFile   File          { get; set; } = null!;
    public CrmPipelineStage? PipelineStage { get; set; }
}

/// <summary>D-14 — the stored bytes of a CRM document (kept in the database, never under wwwroot).</summary>
internal class CrmActivityFile : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public string   FileName       { get; set; } = string.Empty;
    public string   ContentType    { get; set; } = "application/octet-stream";
    public long     SizeBytes      { get; set; }
    public byte[]   Content        { get; set; } = [];
    public int      UploadedBy     { get; set; }
    public DateTime UploadedAt     { get; set; } = DateTime.UtcNow;
}

/// <summary>D-11 — status, stage and owner changes of leads and opportunities (the timeline's "audit trail").</summary>
internal class CrmRecordHistory : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public string   EntityType     { get; set; } = HistoryEntities.Lead;
    public int      EntityId       { get; set; }
    public string   Kind           { get; set; } = HistoryKinds.Status;
    public string?  FromValue      { get; set; }
    public string?  ToValue        { get; set; }
    public string?  Reason         { get; set; }
    public int      UserId         { get; set; }
    public string?  UserName       { get; set; }
    public DateTime At             { get; set; } = DateTime.UtcNow;
}

/// <summary>D-16 — a key for the public lead API (only its SHA-256 hash is stored).</summary>
internal class CrmLeadApiKey : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id              { get; set; }
    public Guid      Uuid            { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId  { get; set; }
    public string    Name            { get; set; } = string.Empty;
    /// <summary>The first characters of the key, shown so it can be recognised.</summary>
    public string    KeyPrefix       { get; set; } = string.Empty;
    public string    KeyHash         { get; set; } = string.Empty;
    public int       RequestsPerMinute { get; set; } = 100;
    public bool      IsActive        { get; set; } = true;
    public DateTime? LastUsedAt      { get; set; }
    public int       CreatedBy       { get; set; }
    public DateTime  CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt      { get; set; } = DateTime.UtcNow;
}
