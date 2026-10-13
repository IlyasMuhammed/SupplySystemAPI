namespace SMS.Modules.Crm.Domain;

// A41 — the closed vocabularies of the CRM (spec §4–§11, API-CONTRACT.md §0). Persisted as these strings.

internal static class LeadStatuses
{
    public const string New = "NEW", Contacted = "CONTACTED", Qualified = "QUALIFIED", Disqualified = "DISQUALIFIED", Converted = "CONVERTED";
    public static readonly IReadOnlyList<string> All = [New, Contacted, Qualified, Disqualified, Converted];
    /// <summary>Statuses a lead is still being worked in (kanban, assignment).</summary>
    public static readonly IReadOnlyList<string> Open = [New, Contacted, Qualified];
    public static bool IsOpen(string status) => status is New or Contacted or Qualified;
}

internal static class LeadRatings
{
    public const string Hot = "HOT", Warm = "WARM", Cold = "COLD";
    public static readonly IReadOnlyList<string> All = [Hot, Warm, Cold];
}

internal static class LeadPriorities
{
    public const string High = "HIGH", Normal = "NORMAL", Low = "LOW";
    public static readonly IReadOnlyList<string> All = [High, Normal, Low];
}

internal static class PurchaseTimeframes
{
    public static readonly IReadOnlyList<string> All = ["IMMEDIATE", "THIS_QUARTER", "THIS_YEAR", "NEXT_YEAR", "UNKNOWN"];
}

internal static class ExternalSourceSystems
{
    public static readonly IReadOnlyList<string> All = ["ODOO", "SAP", "DYNAMICS", "HUBSPOT", "OTHER"];
}

internal static class OpportunityStatuses
{
    public const string Open = "OPEN", Won = "WON", Lost = "LOST";
    public static readonly IReadOnlyList<string> All = [Open, Won, Lost];
}

internal static class StageTypes
{
    public const string Lead = "LEAD", Opportunity = "OPPORTUNITY";
    public static readonly IReadOnlyList<string> All = [Lead, Opportunity];
}

internal static class CampaignTypes
{
    public static readonly IReadOnlyList<string> All =
        ["GENERAL", "ADVERTISEMENT", "PROMOTION", "EVENT", "EMAIL_BLAST", "SOCIAL_MEDIA", "REFERRAL_PROGRAM"];
}

internal static class CampaignStatuses
{
    public const string Draft = "DRAFT", Active = "ACTIVE", Paused = "PAUSED", Completed = "COMPLETED", Cancelled = "CANCELLED";
    public static readonly IReadOnlyList<string> All = [Draft, Active, Paused, Completed, Cancelled];

    /// <summary>Register 1B — DRAFT → ACTIVE → PAUSED ⇄ ACTIVE → COMPLETED / CANCELLED; DRAFT may be cancelled.</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        _ when from == to => true,
        (Draft, Active) or (Draft, Cancelled) => true,
        (Active, Paused) or (Active, Completed) or (Active, Cancelled) => true,
        (Paused, Active) or (Paused, Completed) or (Paused, Cancelled) => true,
        _ => false
    };
}

internal static class TeamMemberRoles
{
    public const string Leader = "LEADER", Member = "MEMBER";
    public static readonly IReadOnlyList<string> All = [Leader, Member];
}

internal static class ActivityTypes
{
    public const string PhoneCall = "PHONE_CALL", Meeting = "MEETING", EmailSent = "EMAIL_SENT", EmailReceived = "EMAIL_RECEIVED",
        Note = "NOTE", Task = "TASK", FollowUp = "FOLLOW_UP", SiteVisit = "SITE_VISIT", Demo = "DEMO", Presentation = "PRESENTATION",
        StatusChange = "STATUS_CHANGE", DocumentShared = "DOCUMENT_SHARED", Other = "OTHER";

    public static readonly IReadOnlyList<string> All =
        [PhoneCall, Meeting, EmailSent, EmailReceived, Note, Task, FollowUp, SiteVisit, Demo, Presentation, StatusChange, DocumentShared, Other];

    /// <summary>D-10 — the types that are a conversation with the prospect (BR-08: they make a NEW lead CONTACTED).</summary>
    public static readonly IReadOnlySet<string> Contact =
        new HashSet<string> { PhoneCall, Meeting, EmailSent, EmailReceived, SiteVisit, Demo, Presentation, FollowUp };
}

internal static class ActivityOutcomes
{
    public static readonly IReadOnlyList<string> All =
        ["SUCCESSFUL", "UNSUCCESSFUL", "NO_RESPONSE", "FOLLOW_UP_NEEDED", "REFERRED", "COMPLETED", "CANCELLED"];
}

internal static class ParticipantTypes
{
    public const string InternalUser = "INTERNAL_USER", CustomerContact = "CUSTOMER_CONTACT", External = "EXTERNAL";
    public static readonly IReadOnlyList<string> All = [InternalUser, CustomerContact, External];
}

internal static class ParticipantRoles
{
    public static readonly IReadOnlyList<string> All = ["ORGANIZER", "REQUIRED", "OPTIONAL", "ATTENDEE"];
}

internal static class DocumentTypes
{
    public static readonly IReadOnlyList<string> All =
        ["BROCHURE", "PRICE_LIST", "PROPOSAL", "SPECIFICATION", "CONTRACT", "CUSTOMER_PO", "QUOTATION_PDF", "PRESENTATION", "IMAGE",
         "MEETING_MINUTES", "NDA", "OTHER"];
}

internal static class DocumentDirections
{
    public const string SentToCustomer = "SENT_TO_CUSTOMER", ReceivedFromCustomer = "RECEIVED_FROM_CUSTOMER", Internal = "INTERNAL";
    public static readonly IReadOnlyList<string> All = [SentToCustomer, ReceivedFromCustomer, Internal];
}

/// <summary>D-11 — what a <see cref="CrmRecordHistory"/> row records.</summary>
internal static class HistoryKinds
{
    public const string Created = "CREATED", Status = "STATUS", Stage = "STAGE", Assignment = "ASSIGNMENT", Converted = "CONVERTED";
}

internal static class HistoryEntities
{
    public const string Lead = "LEAD", Opportunity = "OPPORTUNITY";
}
