namespace SMS.Modules.Crm.Models;

// A41 API-CONTRACT §4–§6 — opportunities, activities & documents, timeline.

public class OpportunityListItem
{
    public Guid      Uuid               { get; set; }
    public string    OpportunityNumber  { get; set; } = string.Empty;
    public string    Topic              { get; set; } = string.Empty;
    public Guid      AccountUuid        { get; set; }
    public string?   AccountName        { get; set; }
    public int?      ContactPersonId    { get; set; }
    public string?   ContactPersonName  { get; set; }
    public string    Status             { get; set; } = string.Empty;
    public Guid?     PipelineStageUuid  { get; set; }
    public string?   PipelineStageName  { get; set; }
    public int?      WinProbability     { get; set; }
    public decimal?  EstimatedRevenue   { get; set; }
    public decimal?  ActualRevenue      { get; set; }
    public string?   CurrencyCode       { get; set; }
    public DateTime? EstimatedCloseDate { get; set; }
    public DateTime? ActualCloseDate    { get; set; }
    public int?      AssignedToUserId   { get; set; }
    public string?   AssignedToName     { get; set; }
    public Guid?     SalesTeamUuid      { get; set; }
    public string?   SalesTeamName      { get; set; }
    public Guid      SourceLeadUuid     { get; set; }
    public string    SourceLeadNumber   { get; set; } = string.Empty;
    public DateTime  CreatedAt          { get; set; }
}

public sealed class LinkedSalesDocumentModel
{
    public string   Kind         { get; set; } = string.Empty;
    public Guid     Uuid         { get; set; }
    public string   Number       { get; set; } = string.Empty;
    public string   Status       { get; set; } = string.Empty;
    public DateTime CreatedAt    { get; set; }
    public decimal? GrandTotal   { get; set; }
    public string?  CurrencyCode { get; set; }
}

public sealed class OpportunityDetail : OpportunityListItem
{
    public string?   Description     { get; set; }
    public CrmAccountModel? Account  { get; set; }
    public CrmContactModel? ContactPerson { get; set; }
    public Guid?     CampaignUuid    { get; set; }
    public string?   CampaignName    { get; set; }
    public string?   WinLossReason   { get; set; }
    public string?   CompetitorName  { get; set; }
    public bool      IsWonStage      { get; set; }
    public bool      IsLostStage     { get; set; }
    public List<LinkedSalesDocumentModel> SalesDocuments { get; set; } = [];
    /// <summary>The opportunity's pipeline (OPPORTUNITY stages, in order) for the progress bar.</summary>
    public List<PipelineStageModel> Pipeline { get; set; } = [];
    public List<string> AllowedActions { get; set; } = [];
    public bool      CanEdit         { get; set; }
    public int       CreatedByUserId { get; set; }
    public string?   CreatedByName   { get; set; }
    public DateTime  ModifiedAt      { get; set; }
}

public sealed class OpportunityUpdate
{
    public string?   Topic              { get; set; }
    public string?   Description        { get; set; }
    public int?      ContactPersonId    { get; set; }
    public decimal?  EstimatedRevenue   { get; set; }
    public DateTime? EstimatedCloseDate { get; set; }
    public string?   CurrencyCode       { get; set; }
    /// <summary>A different owner needs CRM_ASSIGN_LEAD.</summary>
    public int?      AssignedToUserId   { get; set; }
    public Guid?     SalesTeamUuid      { get; set; }
}

public sealed class OpportunityListFilter
{
    public string?   Search           { get; set; }
    public string?   Status           { get; set; }
    public Guid?     PipelineStageUuid { get; set; }
    public Guid?     SalesTeamUuid    { get; set; }
    public int?      AssignedToUserId { get; set; }
    public bool      Mine             { get; set; }
    public Guid?     AccountUuid      { get; set; }
    public DateTime? CloseFrom        { get; set; }
    public DateTime? CloseTo          { get; set; }
    public string?   SortField        { get; set; }
    public string?   SortOrder        { get; set; }
    public int       Page             { get; set; } = 1;
    public int       PageSize         { get; set; } = 25;
}

public sealed class OpportunityKanbanFilter
{
    public string?   Search           { get; set; }
    public Guid?     SalesTeamUuid    { get; set; }
    public int?      AssignedToUserId { get; set; }
    public bool      Mine             { get; set; }
    public DateTime? CloseFrom        { get; set; }
    public DateTime? CloseTo          { get; set; }
    /// <summary>Also show won and lost opportunities in their stages.</summary>
    public bool      IncludeClosed    { get; set; }
}

public sealed class OpportunityLostRequest
{
    public string? Reason     { get; set; }
    public string? Competitor { get; set; }
}

public sealed class ActivityParticipantModel
{
    public string  ParticipantType { get; set; } = string.Empty;
    public int?    UserId          { get; set; }
    public int?    ContactPersonId { get; set; }
    public string? Name            { get; set; }
    public string? ExternalName    { get; set; }
    public string? ExternalEmail   { get; set; }
    public string? Role            { get; set; }
}

public sealed class ActivityDocumentModel
{
    public Guid     Uuid              { get; set; }
    public Guid?    ActivityUuid      { get; set; }
    public string   DocumentType      { get; set; } = string.Empty;
    public string   DocumentName      { get; set; } = string.Empty;
    public string   Direction         { get; set; } = string.Empty;
    public string?  Notes             { get; set; }
    public string   ContentType       { get; set; } = string.Empty;
    public long     SizeBytes         { get; set; }
    public int      SharedByUserId    { get; set; }
    public string?  SharedByName      { get; set; }
    public DateTime SharedAt          { get; set; }
    public string?  PipelineStageName { get; set; }
}

public sealed class ActivityModel
{
    public Guid      Uuid              { get; set; }
    public Guid?     LeadUuid          { get; set; }
    public string?   LeadNumber        { get; set; }
    public Guid?     OpportunityUuid   { get; set; }
    public string?   OpportunityNumber { get; set; }
    /// <summary>The lead's / opportunity's company, for the activity list.</summary>
    public string?   RegardingName     { get; set; }
    public string    ActivityType      { get; set; } = string.Empty;
    public string    Subject           { get; set; } = string.Empty;
    public string?   Description       { get; set; }
    public string?   Outcome           { get; set; }
    public string?   OutcomeNotes      { get; set; }
    public DateTime  ActivityDate      { get; set; }
    public int?      DurationMinutes   { get; set; }
    public DateTime? ScheduledDate     { get; set; }
    public DateTime? DueDate           { get; set; }
    public DateTime? CompletedDate     { get; set; }
    public bool      IsCompleted       { get; set; }
    public int       PerformedByUserId { get; set; }
    public string?   PerformedByName   { get; set; }
    public int?      ContactPersonId   { get; set; }
    public string?   ContactPersonName { get; set; }
    public DateTime? FollowUpDate      { get; set; }
    public string?   FollowUpType      { get; set; }
    public string?   FollowUpNotes     { get; set; }
    public string?   PipelineStageName { get; set; }
    public List<ActivityParticipantModel> Participants { get; set; } = [];
    public List<ActivityDocumentModel>    Documents    { get; set; } = [];
    public DateTime  CreatedAt         { get; set; }
    public bool      CanEdit           { get; set; }
}

public sealed class ActivityParticipantRequest
{
    public string? ParticipantType { get; set; }
    public int?    UserId          { get; set; }
    public int?    ContactPersonId { get; set; }
    public string? ExternalName    { get; set; }
    public string? ExternalEmail   { get; set; }
    public string? Role            { get; set; }
}

public sealed class ActivityUpsert
{
    /// <summary>Create only — the parent never moves.</summary>
    public Guid?     LeadUuid          { get; set; }
    public Guid?     OpportunityUuid   { get; set; }
    public string?   ActivityType      { get; set; }
    public string?   Subject           { get; set; }
    public string?   Description       { get; set; }
    public string?   Outcome           { get; set; }
    public string?   OutcomeNotes      { get; set; }
    public DateTime? ActivityDate      { get; set; }
    public int?      DurationMinutes   { get; set; }
    public DateTime? ScheduledDate     { get; set; }
    public DateTime? DueDate           { get; set; }
    public bool?     IsCompleted       { get; set; }
    /// <summary>Default the caller.</summary>
    public int?      PerformedByUserId { get; set; }
    public int?      ContactPersonId   { get; set; }
    public DateTime? FollowUpDate      { get; set; }
    public string?   FollowUpType      { get; set; }
    public string?   FollowUpNotes     { get; set; }
    /// <summary>Replaces the participant list (null = leave as it is on update).</summary>
    public List<ActivityParticipantRequest>? Participants { get; set; }
}

public sealed class ActivityListFilter
{
    public Guid?     LeadUuid          { get; set; }
    public Guid?     OpportunityUuid   { get; set; }
    public int?      PerformedByUserId { get; set; }
    public bool      Mine              { get; set; }
    public string?   ActivityType      { get; set; }
    public DateTime? From              { get; set; }
    public DateTime? To                { get; set; }
    /// <summary>OPEN = not completed, DONE = completed.</summary>
    public string?   State             { get; set; }
    public string?   Search            { get; set; }
    public int       Page              { get; set; } = 1;
    public int       PageSize          { get; set; } = 25;
}

public sealed class PendingActivityFilter
{
    public int?  UserId { get; set; }
    /// <summary>Default true: the caller's own; false with no user = everybody's (managers).</summary>
    public bool  Mine   { get; set; } = true;
    /// <summary>Only items due on or before this day (follow-up / due / scheduled date).</summary>
    public DateTime? DueBy { get; set; }
}

public sealed class PendingActivityItem
{
    public ActivityModel Activity { get; set; } = new();
    /// <summary>FOLLOW_UP (follow-up date), TASK (due date) or SCHEDULED (scheduled date).</summary>
    public string    Reason   { get; set; } = string.Empty;
    public DateTime  DueOn    { get; set; }
    public bool      Overdue  { get; set; }
}

public sealed class AttachDocumentRequest
{
    public string? DocumentType { get; set; }
    public string? Direction    { get; set; }
    public string? Notes        { get; set; }
    public string? DocumentName { get; set; }
}

public sealed class TimelineUser
{
    public int    UserId { get; set; }
    public string Name   { get; set; } = string.Empty;
}

public sealed class TimelineEntry
{
    /// <summary>ACTIVITY, DOCUMENT, STATUS_CHANGE, STAGE_CHANGE, ASSIGNMENT, CREATED, CONVERTED, SCM_DOCUMENT.</summary>
    public string    EntryType         { get; set; } = string.Empty;
    public DateTime  Date              { get; set; }
    /// <summary>Set on entries of the source lead shown on an opportunity's timeline (BR-18).</summary>
    public bool      FromLead          { get; set; }
    public string?   LeadNumber        { get; set; }

    public Guid?     ActivityUuid      { get; set; }
    public string?   ActivityType      { get; set; }
    public string?   Subject           { get; set; }
    public string?   Description       { get; set; }
    public string?   Outcome           { get; set; }
    public string?   OutcomeNotes      { get; set; }
    public bool?     IsCompleted       { get; set; }
    public TimelineUser? PerformedBy   { get; set; }
    public CrmContactModel? ContactPerson { get; set; }
    public int?      DurationMinutes   { get; set; }
    public string?   PipelineStage     { get; set; }
    public DateTime? FollowUpDate      { get; set; }
    public string?   FollowUpType      { get; set; }
    public string?   FollowUpNotes     { get; set; }
    public List<ActivityDocumentModel> Documents { get; set; } = [];

    public Guid?     DocumentUuid      { get; set; }
    public string?   DocumentType      { get; set; }
    public string?   DocumentName      { get; set; }
    public string?   Direction         { get; set; }

    public string?   FromValue         { get; set; }
    public string?   ToValue           { get; set; }
    public string?   Reason            { get; set; }
    public TimelineUser? ChangedBy     { get; set; }

    public string?   ScmKind           { get; set; }
    public Guid?     ScmUuid           { get; set; }
    public string?   ScmNumber         { get; set; }
    public string?   ScmStatus         { get; set; }
    public decimal?  GrandTotal        { get; set; }
    public string?   CurrencyCode      { get; set; }
}

public sealed class TimelineResponse
{
    public List<TimelineEntry> Timeline  { get; set; } = [];
    public int                 TotalCount { get; set; }
    public int                 Page      { get; set; }
    public int                 PageSize  { get; set; }
}
