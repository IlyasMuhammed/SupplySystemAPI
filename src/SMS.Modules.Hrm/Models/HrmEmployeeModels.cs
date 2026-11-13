namespace SMS.Modules.Hrm.Models;

// API-CONTRACT §3–§5 — employee master. S3 fields appear masked here; the *Sensitive models are the audited reveal endpoints only.

public sealed class EmployeeAddress
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
}

public sealed class EmergencyContact
{
    public string? Name { get; set; }
    public string? Relation { get; set; }
    public string? Phone { get; set; }
}

/// <summary>GET api/hrm/employees/lookup (REV49-5): what a picker shows, nothing more.</summary>
public sealed class EmployeeLookupItem
{
    public Guid    Uuid           { get; set; }
    public string  EmployeeNumber { get; set; } = string.Empty;
    public string  FullName       { get; set; } = string.Empty;
    public string  Status         { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
}

public sealed class EmployeeListItem
{
    public Guid    Uuid            { get; set; }
    public string  EmployeeNumber  { get; set; } = "";
    public string  FullName        { get; set; } = "";
    public string  Status          { get; set; } = "";
    public string? WorkEmail       { get; set; }
    public string? Mobile          { get; set; }
    public Guid?   DepartmentUuid  { get; set; }
    public string? DepartmentName  { get; set; }
    public Guid?   DesignationUuid { get; set; }
    public string? DesignationName { get; set; }
    public Guid?   BranchUuid      { get; set; }
    public string? BranchName      { get; set; }
    public Guid?   ManagerUuid     { get; set; }
    public string? ManagerName     { get; set; }
    public DateOnly? JoinDate      { get; set; }
    public string? CnicMasked      { get; set; }
}

public sealed class EmployeeDetail
{
    public Guid      Uuid                { get; set; }
    public string    EmployeeNumber      { get; set; } = "";
    public string    FirstName           { get; set; } = "";
    public string    LastName            { get; set; } = "";
    public string    FullName            { get; set; } = "";
    public string?   FatherOrHusbandName { get; set; }
    public DateOnly? DateOfBirth         { get; set; }
    public string?   Gender              { get; set; }
    public string?   MaritalStatus       { get; set; }
    public string?   Nationality         { get; set; }
    public string?   CnicMasked          { get; set; }
    public string?   NtnMasked           { get; set; }
    public string?   PersonalEmail       { get; set; }
    public string?   WorkEmail           { get; set; }
    public string?   Phone               { get; set; }
    public string?   Mobile              { get; set; }
    public EmployeeAddress? CurrentAddress   { get; set; }
    public EmployeeAddress? PermanentAddress { get; set; }
    public List<EmergencyContact> EmergencyContacts { get; set; } = [];
    public Guid?     PhotoAttachmentUuid { get; set; }
    public string    Status              { get; set; } = "";
    public int?      UserId              { get; set; }
    public Guid?     SourceCandidateUuid { get; set; }
    public DateOnly? JoinDate            { get; set; }
    public DateOnly? LeaveDate           { get; set; }
    public string?   DepartureReason     { get; set; }
    public string?   Notes               { get; set; }
    public EmploymentRecordModel? CurrentEmployment { get; set; }
    /// <summary>The statuses this one may move to.</summary>
    public List<string> AllowedStatuses  { get; set; } = [];
    public bool      CanEdit             { get; set; }
    public bool      CanRevealSensitive  { get; set; }
}

public sealed class EmployeeUpsert
{
    public string?   FirstName           { get; set; }
    public string?   LastName            { get; set; }
    public string?   FatherOrHusbandName { get; set; }
    public DateOnly? DateOfBirth         { get; set; }
    public string?   Gender              { get; set; }
    public string?   MaritalStatus       { get; set; }
    public string?   Nationality         { get; set; }
    /// <summary>Plain text in, encrypted at rest, never returned here. On update: null = keep, "" = clear.</summary>
    public string?   Cnic                { get; set; }
    public string?   Ntn                 { get; set; }
    public string?   PersonalEmail       { get; set; }
    public string?   WorkEmail           { get; set; }
    public string?   Phone               { get; set; }
    public string?   Mobile              { get; set; }
    public EmployeeAddress? CurrentAddress   { get; set; }
    public EmployeeAddress? PermanentAddress { get; set; }
    public List<EmergencyContact>? EmergencyContacts { get; set; }
    public Guid?     PhotoAttachmentUuid { get; set; }
    public int?      UserId              { get; set; }
    public DateOnly? JoinDate            { get; set; }
    public string?   Notes               { get; set; }
    /// <summary>Create only: Candidate, Probation or Active (default).</summary>
    public string?   Status              { get; set; }
    /// <summary>Create only (optional): the hire record.</summary>
    public EmploymentRecordCreate? Employment { get; set; }
    public Guid?     SourceCandidateUuid { get; set; }
}

public sealed class EmployeeStatusChange
{
    public string?   Status          { get; set; }
    /// <summary>Required when terminating: the leave date.</summary>
    public DateOnly? EffectiveDate   { get; set; }
    /// <summary>Required when terminating: a DepartureReasons value.</summary>
    public string?   DepartureReason { get; set; }
}

public sealed class EmployeeListFilter
{
    public string? Search { get; set; }
    /// <summary>Exact CNIC (keyed hash); needs HRM_EMPLOYEE_SENSITIVE_VIEW.</summary>
    public string? Cnic { get; set; }
    public string? Status { get; set; }
    public Guid?   DepartmentUuid { get; set; }
    public Guid?   BranchUuid { get; set; }
    public Guid?   DesignationUuid { get; set; }
    public Guid?   ManagerUuid { get; set; }
    public int     Page { get; set; } = 1;
    public int     PageSize { get; set; } = 25;
}

/// <summary>GET …/sensitive — every call writes one row per field to hrm.SensitiveAccessLogs.</summary>
public sealed class EmployeeSensitiveModel
{
    public string? Cnic { get; set; }
    public string? Ntn  { get; set; }
}

public sealed class EmploymentRecordModel
{
    public Guid      Uuid             { get; set; }
    public Guid      EmployeeUuid     { get; set; }
    public DateOnly  EffectiveFrom    { get; set; }
    public DateOnly? EffectiveTo      { get; set; }
    public string    EmploymentType   { get; set; } = "";
    public Guid      DepartmentUuid   { get; set; }
    public string    DepartmentName   { get; set; } = "";
    public Guid      DesignationUuid  { get; set; }
    public string    DesignationName  { get; set; } = "";
    public Guid      BranchUuid       { get; set; }
    public string    BranchName       { get; set; } = "";
    public Guid?     GradeUuid        { get; set; }
    public string?   GradeName        { get; set; }
    public Guid?     ManagerUuid      { get; set; }
    public string?   ManagerName      { get; set; }
    public Guid?     ShiftUuid        { get; set; }
    public string?   ShiftName        { get; set; }
    public DateOnly? ProbationEndDate { get; set; }
    public DateOnly? ConfirmationDate { get; set; }
    public DateOnly? ContractEndDate  { get; set; }
    public int       NoticePeriodDays { get; set; }
    public Guid?     CostCenterUuid   { get; set; }
    public string?   CostCenterCode   { get; set; }
    public string?   PayrollGroup     { get; set; }
    public string    ChangeReason     { get; set; } = "";
    public string?   Notes            { get; set; }
    public List<CostSplitModel> CostSplits { get; set; } = [];
}

public sealed class CostSplitModel
{
    public Guid    CostCenterUuid { get; set; }
    public string? CostCenterCode { get; set; }
    public decimal Percent        { get; set; }
}

public class EmploymentRecordCreate
{
    public DateOnly? EffectiveFrom   { get; set; }
    public string?   EmploymentType  { get; set; }
    public Guid?     DepartmentUuid  { get; set; }
    public Guid?     DesignationUuid { get; set; }
    public Guid?     BranchUuid      { get; set; }
    public Guid?     GradeUuid       { get; set; }
    public Guid?     ManagerUuid     { get; set; }
    public Guid?     ShiftUuid       { get; set; }
    public DateOnly? ProbationEndDate { get; set; }
    public DateOnly? ConfirmationDate { get; set; }
    public DateOnly? ContractEndDate  { get; set; }
    public int?      NoticePeriodDays { get; set; }
    public Guid?     CostCenterUuid   { get; set; }
    public string?   PayrollGroup     { get; set; }
    public string?   ChangeReason     { get; set; }
    public string?   Notes            { get; set; }
    public List<CostSplitModel>? CostSplits { get; set; }
}

/// <summary>Transfer: only what changes; everything else carries over from the record in force.</summary>
public sealed class EmploymentTransferRequest
{
    public DateOnly? EffectiveFrom  { get; set; }
    public Guid?     DepartmentUuid { get; set; }
    public Guid?     BranchUuid     { get; set; }
    public Guid?     ManagerUuid    { get; set; }
    public Guid?     CostCenterUuid { get; set; }
    public string?   Notes          { get; set; }
}

public sealed class EmploymentPromotionRequest
{
    public DateOnly? EffectiveFrom   { get; set; }
    public Guid?     DesignationUuid { get; set; }
    public Guid?     GradeUuid       { get; set; }
    public Guid?     ManagerUuid     { get; set; }
    public string?   Notes           { get; set; }
}

public sealed class EmployeeBankAccountModel
{
    public Guid    Uuid                { get; set; }
    public Guid    EmployeeUuid        { get; set; }
    public string  BankName            { get; set; } = "";
    public string? BranchName          { get; set; }
    public string? AccountTitle        { get; set; }
    public string? AccountNumberMasked { get; set; }
    public string? IbanMasked          { get; set; }
    public bool    IsPrimary           { get; set; }
    public bool    IsActive            { get; set; }
    public string  PaymentMethod       { get; set; } = "";
}

public sealed class EmployeeBankAccountUpsert
{
    public string? BankName      { get; set; }
    public string? BranchName    { get; set; }
    public string? AccountTitle  { get; set; }
    /// <summary>On update: null = keep, "" = clear.</summary>
    public string? AccountNumber { get; set; }
    public string? Iban          { get; set; }
    public bool?   IsPrimary     { get; set; }
    public string? PaymentMethod { get; set; }
}

public sealed class EmployeeBankSensitiveModel
{
    public string? AccountNumber { get; set; }
    public string? Iban          { get; set; }
}

public sealed class EmployeeDocumentModel
{
    public Guid      Uuid           { get; set; }
    public string    DocumentType   { get; set; } = "";
    public string    Title          { get; set; } = "";
    public Guid      AttachmentUuid { get; set; }
    public DateOnly? ExpiryDate     { get; set; }
    public DateTime  CreatedAt      { get; set; }
}

public sealed class EmployeeDocumentCreate
{
    public string?   DocumentType   { get; set; }
    public string?   Title          { get; set; }
    public Guid      AttachmentUuid { get; set; }
    public DateOnly? ExpiryDate     { get; set; }
}
