using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Domain;

// A49 (docs/hrm/ADDENDUM-49-ANALYSIS.md D-2): int Id + Guid Uuid (routes and JSON only ever see the uuid), Guid OrganizationId
// (tenant filter), PascalCase columns, strings + CHECK for statuses (HrmCodes.cs), DateOnly dates, money decimal(18,2), rates
// decimal(9,4), users as int ids + name snapshots. FKs only inside schema hrm (task 073); references to other modules are bare
// values: WarehouseUuid, UserId, AttachmentUuid (workflow DocumentAttachments), AccountCode. CORE owns this file.

/// <summary>Every hrm table's common columns.</summary>
internal abstract class HrmEntity : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy      { get; set; }
    public string?   CreatedByName  { get; set; }
    public DateTime  ModifiedAt     { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy     { get; set; }
    public string?   ModifiedByName { get; set; }
}

/// <summary>Rows edited concurrently: a stale save throws DbUpdateConcurrencyException, answered 409.</summary>
internal abstract class HrmVersionedEntity : HrmEntity
{
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>§11A.2 / G-19 — one row per organization. Created unconfigured by the seeder (nothing is enabled by it).</summary>
internal class HrmSettings : HrmVersionedEntity
{
    /// <summary>Required before MODULE_HRM can be enabled (FD-9: 422 PAYROLL_COST_CENTER_REQUIRED).</summary>
    public int?      PayrollCostCenterId   { get; set; }
    public string    PayrollCostCenterMode { get; set; } = PayrollCostCenterModes.Central;
    public string    PostDatePolicy        { get; set; } = PayrollPostDatePolicies.PayDateWithFallback;
    public string    EmployeeNumberPrefix  { get; set; } = "EMP";
    /// <summary>The next number to give out; only ever goes up, so a number is never reused.</summary>
    public int       NextEmployeeNumber    { get; set; } = 1;
    public string    CountryCode           { get; set; } = "PK";
    /// <summary>The basis of pro-rata pay: calendar days of the month when null, else this fixed divisor (e.g. 26, 30).</summary>
    public int?      WorkingDaysDivisor    { get; set; }
    /// <summary>The bank row of a net-pay file; a line with no primary account blocks unless paid by cash/cheque.</summary>
    public string    DefaultExpenseSettlement { get; set; } = ExpenseSettlementMethods.WithPayroll;
    public decimal?  DefaultReceiptThreshold  { get; set; }
    public int       SeedVersion           { get; set; }
    public DateTime? EnablementCompletedAt { get; set; }
    public int?      EnablementCompletedBy { get; set; }

    public CostCenter? PayrollCostCenter { get; set; }
}

/// <summary>§11A.2 — no cost-center entity existed anywhere (only a free-text dimension on journal lines). A normal org-structure object.</summary>
internal class CostCenter : HrmVersionedEntity
{
    /// <summary>The posting dimension: FinancialTransactionLine.CostCenter and JournalEntryLines.CostCenter carry this code.</summary>
    public string  Code       { get; set; } = string.Empty;
    public string  Name       { get; set; } = string.Empty;
    public int?    ParentId   { get; set; }
    public int?    BranchId   { get; set; }
    public bool    IsActive   { get; set; } = true;

    public CostCenter? Parent { get; set; }
    public Branch?     Branch { get; set; }
}

/// <summary>§5.1.</summary>
internal class Branch : HrmVersionedEntity
{
    public string  Code          { get; set; } = string.Empty;
    public string  Name          { get; set; } = string.Empty;
    public string? Address       { get; set; }
    public string? City          { get; set; }
    /// <summary>Bare uuid of a warehouse when the branch is also a store (no cross-schema FK).</summary>
    public Guid?   WarehouseUuid { get; set; }
    public bool    IsActive      { get; set; } = true;
}

/// <summary>§5.2.</summary>
internal class Department : HrmVersionedEntity
{
    public string  Code           { get; set; } = string.Empty;
    public string  Name           { get; set; } = string.Empty;
    public int?    ParentId       { get; set; }
    public int?    HeadEmployeeId { get; set; }
    public int?    BranchId       { get; set; }
    /// <summary>§11A.2 — real link to a cost center; <see cref="CostCenterCode"/> is its denormalized code for the posting line.</summary>
    public int?    CostCenterId   { get; set; }
    public string? CostCenterCode { get; set; }
    public bool    IsActive       { get; set; } = true;

    public Department? Parent       { get; set; }
    public Employee?   HeadEmployee { get; set; }
    public Branch?     Branch       { get; set; }
    public CostCenter? CostCenter   { get; set; }
}

/// <summary>§5.3.</summary>
internal class Designation : HrmVersionedEntity
{
    public string  Code     { get; set; } = string.Empty;
    public string  Name     { get; set; } = string.Empty;
    public int     Level    { get; set; }
    public bool    IsActive { get; set; } = true;
}

/// <summary>§5.3 — MinSalary / MaxSalary are S2.</summary>
internal class Grade : HrmVersionedEntity
{
    public string   Code      { get; set; } = string.Empty;
    public string   Name      { get; set; } = string.Empty;
    public decimal? MinSalary { get; set; }
    public decimal? MaxSalary { get; set; }
    public bool     IsActive  { get; set; } = true;
}

/// <summary>§5.4. S3 fields are AES (IEncryptionService) + keyed hash for exact search + the mask tail; never map the ciphertext to a DTO.</summary>
internal class Employee : HrmVersionedEntity
{
    public string    EmployeeNumber      { get; set; } = string.Empty;
    public string    FirstName           { get; set; } = string.Empty;
    public string    LastName            { get; set; } = string.Empty;
    public string?   FatherOrHusbandName { get; set; }
    public DateOnly? DateOfBirth         { get; set; }
    public string?   Gender              { get; set; }
    public string?   MaritalStatus       { get; set; }
    public string?   Nationality         { get; set; }
    public string?   CnicEncrypted       { get; set; }
    public string?   CnicHash            { get; set; }
    public string?   CnicLastDigit       { get; set; }
    public string?   NtnEncrypted        { get; set; }
    public string?   NtnHash             { get; set; }
    public string?   NtnLast4            { get; set; }
    public string?   PersonalEmail       { get; set; }
    public string?   WorkEmail           { get; set; }
    public string?   Phone               { get; set; }
    public string?   Mobile              { get; set; }
    /// <summary>JSON: EmployeeAddress.</summary>
    public string?   CurrentAddressJson   { get; set; }
    public string?   PermanentAddressJson { get; set; }
    /// <summary>JSON array: EmergencyContact.</summary>
    public string?   EmergencyContactsJson { get; set; }
    public Guid?     PhotoAttachmentUuid { get; set; }
    public string    Status              { get; set; } = EmployeeStatuses.Active;
    /// <summary>The SMS user; one employee per user per organization.</summary>
    public int?      UserId              { get; set; }
    public Guid?     SourceCandidateUuid { get; set; }
    public DateOnly? JoinDate            { get; set; }
    public DateOnly? LeaveDate           { get; set; }
    public string?   DepartureReason     { get; set; }
    public string?   Notes               { get; set; }
    public bool      IsDeleted           { get; set; }

    public ICollection<EmploymentRecord>    EmploymentRecords { get; set; } = new List<EmploymentRecord>();
    public ICollection<EmployeeBankAccount> BankAccounts      { get; set; } = new List<EmployeeBankAccount>();
    public ICollection<EmployeeDocument>    Documents         { get; set; } = new List<EmployeeDocument>();

    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>§5.5 — effective-dated, Odoo hr.contract style. EffectiveTo null = open. No overlap per employee (service + unique start).</summary>
internal class EmploymentRecord : HrmVersionedEntity
{
    public int       EmployeeId        { get; set; }
    public DateOnly  EffectiveFrom     { get; set; }
    public DateOnly? EffectiveTo       { get; set; }
    public string    EmploymentType    { get; set; } = EmploymentTypes.Permanent;
    public int       DepartmentId      { get; set; }
    public int       DesignationId     { get; set; }
    public int       BranchId          { get; set; }
    public int?      GradeId           { get; set; }
    public int?      ManagerEmployeeId { get; set; }
    public int?      ShiftId           { get; set; }
    public DateOnly? ProbationEndDate  { get; set; }
    public DateOnly? ConfirmationDate  { get; set; }
    public DateOnly? ContractEndDate   { get; set; }
    public int       NoticePeriodDays  { get; set; }
    /// <summary>Overrides the department's cost center (§11A.2 By-department mode). Code denormalized for posting.</summary>
    public int?      CostCenterId      { get; set; }
    public string?   CostCenterCode    { get; set; }
    public string?   PayrollGroup      { get; set; }
    public string    ChangeReason      { get; set; } = EmploymentChangeReasons.Hire;
    public string?   Notes             { get; set; }

    public Employee?    Employee        { get; set; }
    public Department?  Department      { get; set; }
    public Designation? Designation     { get; set; }
    public Branch?      Branch          { get; set; }
    public Grade?       Grade           { get; set; }
    public Employee?    ManagerEmployee { get; set; }
    public Shift?       Shift           { get; set; }
    public CostCenter?  CostCenter      { get; set; }
    public ICollection<EmploymentCostSplit> CostSplits { get; set; } = new List<EmploymentCostSplit>();
}

/// <summary>§11A.2 — optional percentage split of one employment record across cost centers (Σ = 100, checked by the service).</summary>
internal class EmploymentCostSplit : HrmEntity
{
    public int     EmploymentRecordId { get; set; }
    public int     CostCenterId       { get; set; }
    public decimal Percent            { get; set; }

    public EmploymentRecord? EmploymentRecord { get; set; }
    public CostCenter?       CostCenter       { get; set; }
}

/// <summary>§5.6 — AccountNumber / Iban S3. One active primary per employee (filtered unique index).</summary>
internal class EmployeeBankAccount : HrmVersionedEntity
{
    public int     EmployeeId             { get; set; }
    public string  BankName               { get; set; } = string.Empty;
    public string? BranchName             { get; set; }
    public string? AccountTitle           { get; set; }
    public string? AccountNumberEncrypted { get; set; }
    public string? AccountNumberLast4     { get; set; }
    public string? IbanEncrypted          { get; set; }
    public string? IbanLast4              { get; set; }
    public bool    IsPrimary              { get; set; }
    public bool    IsActive               { get; set; } = true;
    public string  PaymentMethod          { get; set; } = BankPaymentMethods.Bank;

    public Employee? Employee { get; set; }
}

/// <summary>The Documents tab (task 080): a file on the employee, kept as a workflow attachment (interface HRM_EMPLOYEE).</summary>
internal class EmployeeDocument : HrmEntity
{
    public int       EmployeeId     { get; set; }
    public string    DocumentType   { get; set; } = EmployeeDocumentTypes.Other;
    public string    Title          { get; set; } = string.Empty;
    public Guid      AttachmentUuid { get; set; }
    public DateOnly? ExpiryDate     { get; set; }

    public Employee? Employee { get; set; }
}

/// <summary>§3.3 — an explicit HR scope assignment: user may see this branch / department (+ children).</summary>
internal class HrUserScope : HrmEntity
{
    public int    UserId       { get; set; }
    public string ScopeType    { get; set; } = HrScopeTypes.Department;
    public int?   BranchId     { get; set; }
    public int?   DepartmentId { get; set; }

    public Branch?     Branch     { get; set; }
    public Department? Department { get; set; }
}

/// <summary>§5 S3 — "audited on read". Append-only (HrmDbContext refuses changes).</summary>
internal class SensitiveAccessLog : HrmEntity
{
    public int    EmployeeId { get; set; }
    public string Field      { get; set; } = string.Empty;
    public string Purpose    { get; set; } = string.Empty;
    public int?   UserId     { get; set; }
    public string? UserName  { get; set; }
}

// ── H2 — Leave & attendance (owner of behaviour: ATT) ───────────────────────────────────────────────────────────────

/// <summary>§5.7.</summary>
internal class Shift : HrmVersionedEntity
{
    public string   Code             { get; set; } = string.Empty;
    public string   Name             { get; set; } = string.Empty;
    public TimeOnly StartTime        { get; set; }
    public TimeOnly EndTime          { get; set; }
    public int      BreakMinutes     { get; set; }
    public int      GraceInMinutes   { get; set; }
    public int      GraceOutMinutes  { get; set; }
    public int      HalfDayMinutes   { get; set; }
    /// <summary>Overtime starts this many minutes beyond the shift (task 117).</summary>
    public int      OvertimeThresholdMinutes { get; set; }
    /// <summary>Flags, Sunday = 1, Monday = 2, Tuesday = 4 … Saturday = 64.</summary>
    public int      WeeklyOffDays    { get; set; } = 1;
    public bool     IsDefault        { get; set; }
    public bool     IsActive         { get; set; } = true;
}

/// <summary>§5.7 — IsEstimated marks a moon-dependent date seeded as an estimate.</summary>
internal class Holiday : HrmVersionedEntity
{
    public DateOnly Date        { get; set; }
    public string   Name        { get; set; } = string.Empty;
    public string   CountryCode { get; set; } = "PK";
    public int?     BranchId    { get; set; }
    public bool     IsEstimated { get; set; }
    public bool     IsSeeded    { get; set; }

    public Branch? Branch { get; set; }
}

/// <summary>§5.7 — raw and immutable. Unique (org, source, external id) makes recording idempotent.</summary>
internal class AttendanceEvent : HrmEntity
{
    public int      EmployeeId      { get; set; }
    public DateTime EventTimeUtc    { get; set; }
    public string   EventType       { get; set; } = AttendanceEventTypes.Unknown;
    public string   Source          { get; set; } = AttendanceSources.Manual;
    public string?  ExternalEventId { get; set; }
    public string?  Note            { get; set; }

    public Employee? Employee { get; set; }
}

/// <summary>§5.7 — computed, one per employee per date.</summary>
internal class AttendanceDay : HrmEntity
{
    public int       EmployeeId      { get; set; }
    public DateOnly  Date            { get; set; }
    public int?      ShiftId         { get; set; }
    public DateTime? FirstInUtc      { get; set; }
    public DateTime? LastOutUtc      { get; set; }
    public int       WorkedMinutes   { get; set; }
    public int       LateMinutes     { get; set; }
    public int       EarlyMinutes    { get; set; }
    public int       OvertimeMinutes { get; set; }
    public string    Status          { get; set; } = AttendanceDayStatuses.Absent;
    public bool      IsManualOverride { get; set; }
    public string?   OverrideNote    { get; set; }
    public DateTime  ComputedAt      { get; set; } = DateTime.UtcNow;

    public Employee? Employee { get; set; }
    public Shift?    Shift    { get; set; }
}

/// <summary>§5.7 — approved overtime feeds payroll.</summary>
internal class OvertimeRecord : HrmVersionedEntity
{
    public int      EmployeeId     { get; set; }
    public DateOnly Date           { get; set; }
    public int      Minutes        { get; set; }
    public decimal  Multiplier     { get; set; } = 1m;
    public string   Status         { get; set; } = ApprovalStatuses.Pending;
    public string?  Reason         { get; set; }
    public int?     DecidedBy      { get; set; }
    public string?  DecidedByName  { get; set; }
    public DateTime? DecidedAt     { get; set; }
    public string?  DecisionNote   { get; set; }
    /// <summary>The payroll run that paid it (null until paid; prevents paying twice).</summary>
    public int?     PaidInRunId    { get; set; }

    public Employee?   Employee   { get; set; }
    public PayrollRun? PaidInRun  { get; set; }
}

/// <summary>§5.8.</summary>
internal class LeaveType : HrmVersionedEntity
{
    public string   Code                { get; set; } = string.Empty;
    public string   Name                { get; set; } = string.Empty;
    public bool     IsPaid              { get; set; } = true;
    public decimal? CarryForwardCap     { get; set; }
    public int?     AttachmentAfterDays { get; set; }
    public string?  GenderRestriction   { get; set; }
    public bool     AllowNegativeBalance { get; set; }
    public bool     IsActive            { get; set; } = true;
    public bool     IsSeeded            { get; set; }

    public ICollection<LeavePolicy> Policies { get; set; } = new List<LeavePolicy>();
}

/// <summary>§5.8 — effective-dated entitlement of a leave type (one open policy at a time per type; optional per grade).</summary>
internal class LeavePolicy : HrmVersionedEntity
{
    public int       LeaveTypeId   { get; set; }
    public int?      GradeId       { get; set; }
    public decimal   Entitlement   { get; set; }
    public string    AccrualMethod { get; set; } = LeaveAccrualMethods.Annual;
    public DateOnly  EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo   { get; set; }

    public LeaveType? LeaveType { get; set; }
    public Grade?     Grade     { get; set; }
}

/// <summary>§5.8 — balance = Σ Delta. Append-only.</summary>
internal class LeaveLedgerEntry : HrmEntity
{
    public int      EmployeeId  { get; set; }
    public int      LeaveTypeId { get; set; }
    public decimal  Delta       { get; set; }
    public string   Reason      { get; set; } = LeaveLedgerReasons.Allocation;
    public DateOnly Date        { get; set; }
    /// <summary>The leave year this entry belongs to.</summary>
    public int      Year        { get; set; }
    public Guid?    ReferenceUuid { get; set; }
    public string?  Note        { get; set; }

    public Employee?  Employee  { get; set; }
    public LeaveType? LeaveType { get; set; }
}

/// <summary>§5.8.</summary>
internal class LeaveRequest : HrmVersionedEntity
{
    public string    RequestNumber  { get; set; } = string.Empty;
    public int       EmployeeId     { get; set; }
    public int       LeaveTypeId    { get; set; }
    public DateOnly  FromDate       { get; set; }
    public DateOnly  ToDate         { get; set; }
    public bool      HalfDay        { get; set; }
    public decimal   Days           { get; set; }
    public string?   Reason         { get; set; }
    public string    Status         { get; set; } = LeaveRequestStatuses.Pending;
    public Guid?     AttachmentUuid { get; set; }
    public Guid?     ApprovalUuid   { get; set; }
    public int?      DecidedBy      { get; set; }
    public string?   DecidedByName  { get; set; }
    public DateTime? DecidedAt      { get; set; }
    public string?   DecisionNote   { get; set; }

    public Employee?  Employee  { get; set; }
    public LeaveType? LeaveType { get; set; }
}
