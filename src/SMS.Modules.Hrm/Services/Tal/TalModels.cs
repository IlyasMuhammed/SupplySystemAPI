namespace SMS.Modules.Hrm.Models;

// API-CONTRACT §8 (TAL).

// ── Recruitment ─────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class VacancyModel
{
    public Guid      Uuid            { get; set; }
    public string    VacancyNumber   { get; set; } = "";
    public string    Title           { get; set; } = "";
    public Guid      DesignationUuid { get; set; }
    public string    DesignationName { get; set; } = "";
    public Guid      DepartmentUuid  { get; set; }
    public string    DepartmentName  { get; set; } = "";
    public Guid?     BranchUuid      { get; set; }
    public string?   BranchName      { get; set; }
    public int       Positions       { get; set; }
    public string    Status          { get; set; } = "";
    public string?   Description     { get; set; }
    public DateOnly? ClosingDate     { get; set; }
    public Dictionary<string, int> ApplicationCounts { get; set; } = [];
}

public sealed class VacancyUpsert
{
    public string?   Title           { get; set; }
    public Guid?     DesignationUuid { get; set; }
    public Guid?     DepartmentUuid  { get; set; }
    public Guid?     BranchUuid      { get; set; }
    public int?      Positions       { get; set; }
    public string?   Description     { get; set; }
    public DateOnly? ClosingDate     { get; set; }
}

public sealed class TalStatusChange
{
    public string? Status { get; set; }
}

public sealed class CandidateModel
{
    public Guid    Uuid      { get; set; }
    public string  FirstName { get; set; } = "";
    public string  LastName  { get; set; } = "";
    public string? Email     { get; set; }
    public string? Phone     { get; set; }
    public string? Source    { get; set; }
    public Guid?   ResumeAttachmentUuid { get; set; }
    public string? Notes     { get; set; }
    public List<ApplicationModel> Applications { get; set; } = [];
}

public sealed class CandidateUpsert
{
    public string? FirstName { get; set; }
    public string? LastName  { get; set; }
    public string? Email     { get; set; }
    public string? Phone     { get; set; }
    public string? Source    { get; set; }
    public Guid?   ResumeAttachmentUuid { get; set; }
    public string? Notes     { get; set; }
    /// <summary>Save although the email or phone matches another candidate.</summary>
    public bool    Force     { get; set; }
}

/// <summary>A candidate with the same email or phone. Name is null when that candidate is outside the caller's HR scope.</summary>
public sealed class CandidateDuplicateMatch
{
    public Guid    Uuid { get; set; }
    public string? Name { get; set; }
    public List<string> MatchedOn { get; set; } = [];
}

public sealed class ApplicationModel
{
    public Guid    Uuid          { get; set; }
    public Guid    CandidateUuid { get; set; }
    public string  CandidateName { get; set; } = "";
    public Guid    VacancyUuid   { get; set; }
    public string  VacancyTitle  { get; set; } = "";
    public string  Stage         { get; set; } = "";
    public int?    Rating        { get; set; }
    public string? RejectionReason   { get; set; }
    public Guid?   HiredEmployeeUuid { get; set; }
}

public sealed class BoardColumn
{
    public string Stage { get; set; } = "";
    public List<ApplicationModel> Applications { get; set; } = [];
}

public sealed class ApplicationCreate
{
    public Guid? CandidateUuid { get; set; }
    public Guid? VacancyUuid   { get; set; }
}

public sealed class ApplicationStageChange
{
    public string? Stage           { get; set; }
    public string? RejectionReason { get; set; }
}

public sealed class ApplicationRatingChange
{
    public int? Rating { get; set; }
}

public sealed class ConvertToEmployeeRequest
{
    public DateOnly? JoinDate        { get; set; }
    public Guid?     DesignationUuid { get; set; }
    public Guid?     DepartmentUuid  { get; set; }
    public Guid?     BranchUuid      { get; set; }
    public Guid?     ManagerUuid     { get; set; }
    public string?   EmploymentType  { get; set; }
}

public sealed class InterviewModel
{
    public Guid     Uuid            { get; set; }
    public Guid     ApplicationUuid { get; set; }
    public Guid?    InterviewerUuid { get; set; }
    public string?  InterviewerName { get; set; }
    public DateTime ScheduledAt     { get; set; }
    public string?  Feedback        { get; set; }
    public decimal? Score           { get; set; }
    public string   Result          { get; set; } = "";
}

/// <summary>GET interviews/me: what an interviewer needs to give feedback. Deliberately no contact details, resume, rating, offer or salary.</summary>
public sealed class MyInterviewModel
{
    public Guid     Uuid             { get; set; }
    public DateTime ScheduledAt      { get; set; }
    public string?  Feedback         { get; set; }
    public decimal? Score            { get; set; }
    public string   Result           { get; set; } = "";
    public Guid     ApplicationUuid  { get; set; }
    public string   ApplicationStage { get; set; } = "";
    public string   CandidateName    { get; set; } = "";
    public string   VacancyTitle     { get; set; } = "";
    public string   DesignationName  { get; set; } = "";
    public string   DepartmentName   { get; set; } = "";
}

public sealed class InterviewUpsert
{
    public Guid?     InterviewerUuid { get; set; }
    public DateTime? ScheduledAt     { get; set; }
    public string?   Feedback        { get; set; }
    public decimal?  Score           { get; set; }
    public string?   Result          { get; set; }
}

public sealed class OfferModel
{
    public Guid      Uuid             { get; set; }
    public Guid      ApplicationUuid  { get; set; }
    /// <summary>S2: null + SalaryMasked without HRM_SALARY_VIEW.</summary>
    public decimal?  OfferedSalary    { get; set; }
    public bool      SalaryMasked     { get; set; }
    public DateOnly? ProposedJoinDate { get; set; }
    public string    Status           { get; set; } = "";
    public Guid?     LetterAttachmentUuid { get; set; }
    public string?   Notes            { get; set; }
}

public sealed class OfferUpsert
{
    /// <summary>Needs HRM_SALARY_EDIT. Null keeps the stored amount.</summary>
    public decimal?  OfferedSalary    { get; set; }
    public DateOnly? ProposedJoinDate { get; set; }
    public string?   Notes            { get; set; }
}

public sealed class OfferLetterRequest
{
    /// <summary>An active OfferLetter template; null = the organization's default one.</summary>
    public Guid?   TemplateUuid     { get; set; }
    /// <summary>Used only when the organization name lookup has nothing.</summary>
    public string? OrganizationName { get; set; }
}

public sealed record OfferLetterFile(byte[] Content, string FileName, string ContentType);

// ── Performance ─────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class ReviewCompletion
{
    public int Total        { get; set; }
    /// <summary>Past Draft: Submitted and Acknowledged together.</summary>
    public int Submitted    { get; set; }
    public int Acknowledged { get; set; }
}

public sealed class ReviewCycleModel
{
    public Guid     Uuid      { get; set; }
    public string   Name      { get; set; } = "";
    public string   Period    { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate   { get; set; }
    public string   Status    { get; set; } = "";
    public ReviewCompletion Completion { get; set; } = new();
}

public sealed class ReviewCycleUpsert
{
    public string?   Name      { get; set; }
    public string?   Period    { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate   { get; set; }
}

public sealed class GoalModel
{
    public Guid     Uuid          { get; set; }
    public Guid     EmployeeUuid  { get; set; }
    public Guid     CycleUuid     { get; set; }
    public string   Title         { get; set; } = "";
    public string?  Description   { get; set; }
    public decimal  Weight        { get; set; }
    public decimal? SelfRating    { get; set; }
    /// <summary>Hidden from the employee until the review is submitted.</summary>
    public decimal? ManagerRating { get; set; }
    public string   Status        { get; set; } = "";
}

public sealed class GoalUpsert
{
    /// <summary>POST only.</summary>
    public Guid?    EmployeeUuid  { get; set; }
    /// <summary>POST only.</summary>
    public Guid?    CycleUuid     { get; set; }
    public string?  Title         { get; set; }
    public string?  Description   { get; set; }
    public decimal? Weight        { get; set; }
    public decimal? SelfRating    { get; set; }
    public decimal? ManagerRating { get; set; }
    public string?  Status        { get; set; }
}

public sealed class ReviewModel
{
    public Guid     Uuid          { get; set; }
    public Guid     EmployeeUuid  { get; set; }
    public string   EmployeeName  { get; set; } = "";
    public Guid     CycleUuid     { get; set; }
    public string   CycleName     { get; set; } = "";
    public Guid?    ReviewerUuid  { get; set; }
    public string?  ReviewerName  { get; set; }
    public decimal? OverallRating { get; set; }
    public string   Status        { get; set; } = "";
    /// <summary>Hidden from the employee until the review is submitted.</summary>
    public string?  ManagerComments  { get; set; }
    public string?  EmployeeComments { get; set; }
    public List<GoalModel> Goals { get; set; } = [];
}

public sealed class ReviewUpdate
{
    /// <summary>HRM_PERFORMANCE_MANAGE only. Null keeps the reviewer.</summary>
    public Guid?   ReviewerUuid     { get; set; }
    public string? ManagerComments  { get; set; }
    public string? EmployeeComments { get; set; }
}

public sealed class RatingBucket
{
    /// <summary>0..5: 4 holds ratings from 4.00 to 4.99; 5 holds exactly 5.</summary>
    public int Rating { get; set; }
    public int Count  { get; set; }
}

public sealed class PerformanceDashboard
{
    public ReviewCompletion Completion { get; set; } = new();
    public decimal CompletionPercent   { get; set; }
    public List<RatingBucket> RatingDistribution { get; set; } = [];
}

// ── Training ────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class CourseModel
{
    public Guid     Uuid          { get; set; }
    public string   Code          { get; set; } = "";
    public string   Name          { get; set; } = "";
    public string?  Description   { get; set; }
    public string?  Provider      { get; set; }
    public decimal? DurationHours { get; set; }
    public decimal? Cost          { get; set; }
    public bool     IsActive      { get; set; }
}

public sealed class CourseUpsert
{
    public string?  Code          { get; set; }
    public string?  Name          { get; set; }
    public string?  Description   { get; set; }
    public string?  Provider      { get; set; }
    public decimal? DurationHours { get; set; }
    public decimal? Cost          { get; set; }
    /// <summary>PUT: null keeps the current value.</summary>
    public bool?    IsActive      { get; set; }
}

public sealed class EnrollmentModel
{
    public Guid      Uuid          { get; set; }
    public Guid      EmployeeUuid  { get; set; }
    public string    EmployeeName  { get; set; } = "";
    public Guid      CourseUuid    { get; set; }
    public string    CourseName    { get; set; } = "";
    public DateOnly  EnrolledDate  { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public string    Status        { get; set; } = "";
    public Guid?     CertificateAttachmentUuid { get; set; }
}

public sealed class EnrollmentCreate
{
    public Guid?     EmployeeUuid { get; set; }
    public Guid?     CourseUuid   { get; set; }
    public DateOnly? EnrolledDate { get; set; }
}

public sealed class EnrollmentComplete
{
    public DateOnly? CompletedDate { get; set; }
    public Guid?     CertificateAttachmentUuid { get; set; }
}
