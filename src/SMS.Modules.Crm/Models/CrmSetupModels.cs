namespace SMS.Modules.Crm.Models;

// A41 API-CONTRACT §2 — configuration: lead sources, campaigns, sales teams, pipeline stages, public API keys.

public sealed class LeadSourceModel
{
    public Guid    Uuid        { get; set; }
    public string  Code        { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool    IsActive    { get; set; }
    public int     SortOrder   { get; set; }
    public bool    IsSystem    { get; set; }
}

public sealed class LeadSourceUpsert
{
    public string? Code        { get; set; }
    public string? Name        { get; set; }
    public string? Description { get; set; }
    public bool?   IsActive    { get; set; }
    public int?    SortOrder   { get; set; }
}

public sealed class CampaignModel
{
    public Guid      Uuid              { get; set; }
    public string    Code              { get; set; } = string.Empty;
    public string    Name              { get; set; } = string.Empty;
    public string?   Description       { get; set; }
    public string    CampaignType      { get; set; } = string.Empty;
    public Guid?     LeadSourceUuid    { get; set; }
    public string?   LeadSourceName    { get; set; }
    public string    Status            { get; set; } = string.Empty;
    public DateTime? StartDate         { get; set; }
    public DateTime? EndDate           { get; set; }
    public decimal?  BudgetAmount      { get; set; }
    public string?   CurrencyCode      { get; set; }
    public int?      ExpectedLeads     { get; set; }
    public int       ActualLeads       { get; set; }
    public string?   ExternalReference { get; set; }
    public string?   Notes             { get; set; }
    public int       CreatedBy         { get; set; }
    public string?   CreatedByName     { get; set; }
    public DateTime  CreatedAt         { get; set; }
    /// <summary>Detail only: leads attributed to the campaign now (not deleted), by status.</summary>
    public Dictionary<string, int>? LeadsByStatus { get; set; }
    public int?      OpportunityCount  { get; set; }
}

public sealed class CampaignUpsert
{
    public string?   Code              { get; set; }
    public string?   Name              { get; set; }
    public string?   Description       { get; set; }
    public string?   CampaignType      { get; set; }
    public Guid?     LeadSourceUuid    { get; set; }
    public string?   Status            { get; set; }
    public DateTime? StartDate         { get; set; }
    public DateTime? EndDate           { get; set; }
    public decimal?  BudgetAmount      { get; set; }
    public string?   CurrencyCode      { get; set; }
    public int?      ExpectedLeads     { get; set; }
    public string?   ExternalReference { get; set; }
    public string?   Notes             { get; set; }
}

public sealed class CampaignListFilter
{
    public string? Search   { get; set; }
    public string? Status   { get; set; }
    public string? Type     { get; set; }
    public int     Page     { get; set; } = 1;
    public int     PageSize { get; set; } = 25;
}

public sealed class SalesTeamMemberModel
{
    public int      UserId   { get; set; }
    public string   Name     { get; set; } = string.Empty;
    public string   Role     { get; set; } = string.Empty;
    public bool     IsActive { get; set; }
    public DateTime JoinedAt { get; set; }
}

public sealed class SalesTeamModel
{
    public Guid    Uuid                { get; set; }
    public string  Code                { get; set; } = string.Empty;
    public string  Name                { get; set; } = string.Empty;
    public string? Description         { get; set; }
    public int?    TeamLeaderUserId    { get; set; }
    public string? TeamLeaderName      { get; set; }
    public bool    IsActive            { get; set; }
    public Guid?   DefaultCampaignUuid { get; set; }
    public string? DefaultCampaignName { get; set; }
    /// <summary>True when the team has pipeline stages of its own (otherwise it uses the organization's).</summary>
    public bool    HasOwnStages        { get; set; }
    public List<SalesTeamMemberModel> Members { get; set; } = [];
}

public sealed class SalesTeamUpsert
{
    public string? Code                { get; set; }
    public string? Name                { get; set; }
    public string? Description         { get; set; }
    public int?    TeamLeaderUserId    { get; set; }
    public bool?   IsActive            { get; set; }
    public Guid?   DefaultCampaignUuid { get; set; }
}

public sealed class SalesTeamMemberRequest
{
    public int     UserId { get; set; }
    public string? Role   { get; set; }
}

public sealed class PipelineStageModel
{
    public Guid    Uuid           { get; set; }
    public string  Code           { get; set; } = string.Empty;
    public string  Name           { get; set; } = string.Empty;
    public string  StageType      { get; set; } = string.Empty;
    public Guid?   SalesTeamUuid  { get; set; }
    public string? SalesTeamName  { get; set; }
    public int     Sequence       { get; set; }
    public int?    WinProbability { get; set; }
    public bool    IsWon          { get; set; }
    public bool    IsLost         { get; set; }
    public string? Description    { get; set; }
    public bool    IsActive       { get; set; }
}

public sealed class PipelineStageUpsert
{
    public string? Code           { get; set; }
    public string? Name           { get; set; }
    public string? StageType      { get; set; }
    public Guid?   SalesTeamUuid  { get; set; }
    public int?    Sequence       { get; set; }
    public int?    WinProbability { get; set; }
    public bool?   IsWon          { get; set; }
    public bool?   IsLost         { get; set; }
    public string? Description    { get; set; }
}

public sealed class PipelineStageFilter
{
    public string? StageType       { get; set; }
    public Guid?   SalesTeamUuid   { get; set; }
    public bool    IncludeInactive { get; set; }
}

public sealed class LeadApiKeyModel
{
    public Guid      Uuid              { get; set; }
    public string    Name              { get; set; } = string.Empty;
    public string    KeyPrefix         { get; set; } = string.Empty;
    public int       RequestsPerMinute { get; set; }
    public bool      IsActive          { get; set; }
    public DateTime? LastUsedAt        { get; set; }
    public DateTime  CreatedAt         { get; set; }
}

/// <summary>The only time the key is ever shown.</summary>
public sealed class LeadApiKeyCreated
{
    public string          Key    { get; set; } = string.Empty;
    public LeadApiKeyModel ApiKey { get; set; } = new();
}

public sealed class LeadApiKeyUpsert
{
    public string? Name              { get; set; }
    public int?    RequestsPerMinute { get; set; }
    public bool?   IsActive          { get; set; }
}

/// <summary>A user for the assignment pickers.</summary>
public sealed class CrmUserModel
{
    public int    UserId   { get; set; }
    public string Name     { get; set; } = string.Empty;
    public string Email    { get; set; } = string.Empty;
    public bool   IsActive { get; set; }
}

/// <summary>GET api/crm/meta — the vocabularies the screens need (CrmCodes.cs), one call.</summary>
public sealed class CrmMetaModel
{
    public IReadOnlyList<string> LeadStatuses        { get; set; } = [];
    public IReadOnlyList<string> Ratings             { get; set; } = [];
    public IReadOnlyList<string> Priorities          { get; set; } = [];
    public IReadOnlyList<string> PurchaseTimeframes  { get; set; } = [];
    public IReadOnlyList<string> ExternalSystems     { get; set; } = [];
    public IReadOnlyList<string> OpportunityStatuses { get; set; } = [];
    public IReadOnlyList<string> CampaignTypes       { get; set; } = [];
    public IReadOnlyList<string> CampaignStatuses    { get; set; } = [];
    public IReadOnlyList<string> ActivityTypes       { get; set; } = [];
    public IReadOnlyList<string> ContactActivityTypes { get; set; } = [];
    public IReadOnlyList<string> ActivityOutcomes    { get; set; } = [];
    public IReadOnlyList<string> ParticipantTypes    { get; set; } = [];
    public IReadOnlyList<string> ParticipantRoles    { get; set; } = [];
    public IReadOnlyList<string> DocumentTypes       { get; set; } = [];
    public IReadOnlyList<string> DocumentDirections  { get; set; } = [];
    public bool CampaignTrackingEnabled { get; set; }
}
