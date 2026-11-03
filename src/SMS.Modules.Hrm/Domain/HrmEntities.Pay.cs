namespace SMS.Modules.Hrm.Domain;

// ── H3 — Salary (owner of behaviour: PAY) ───────────────────────────────────────────────────────────────────────────

/// <summary>§5.9 / §6.1. PostingRole = a DEFAULT_* accounting key (A42 mechanism, G-4).</summary>
internal class SalaryComponent : HrmVersionedEntity
{
    public string   Code        { get; set; } = string.Empty;
    public string   Name        { get; set; } = string.Empty;
    public string   Type        { get; set; } = SalaryComponentTypes.Earning;
    public bool     IsTaxable   { get; set; }
    public bool     InGross     { get; set; }
    public string   CalcMethod  { get; set; } = SalaryCalcMethods.Fixed;
    /// <summary>Percent method: the rate. Fixed: the default amount (usually from the assignment).</summary>
    public decimal? DefaultValue { get; set; }
    /// <summary>Percent method: the component code it is a percent of.</summary>
    public string?  PercentOf   { get; set; }
    /// <summary>Formula method: the safe expression (ISalaryFormulaEngine — no code execution).</summary>
    public string?  Formula     { get; set; }
    public string?  PostingRole { get; set; }
    /// <summary>Employer side of a contribution: the expense role (EOBI_ER: payable in PostingRole, expense here).</summary>
    public string?  ExpenseRole { get; set; }
    public bool     IsSystem    { get; set; }
    public bool     IsActive    { get; set; } = true;
    public int      SortOrder   { get; set; }
}

/// <summary>§5.9.</summary>
internal class SalaryStructure : HrmVersionedEntity
{
    public string  Code        { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool    IsDefault   { get; set; }
    public bool    IsActive    { get; set; } = true;

    public ICollection<SalaryStructureLine> Lines { get; set; } = new List<SalaryStructureLine>();
}

internal class SalaryStructureLine : HrmEntity
{
    public int      StructureId       { get; set; }
    public int      ComponentId       { get; set; }
    public int      Sequence          { get; set; }
    /// <summary>Line-level formula/value overriding the component's.</summary>
    public string?  FormulaOverride   { get; set; }
    public decimal? ValueOverride     { get; set; }

    public SalaryStructure? Structure { get; set; }
    public SalaryComponent? Component { get; set; }
}

/// <summary>§5.9 — S2, effective-dated: every change is a new row. Approved rows only reach payroll.</summary>
internal class SalaryAssignment : HrmVersionedEntity
{
    public int       EmployeeId     { get; set; }
    public int       StructureId    { get; set; }
    public DateOnly  EffectiveFrom  { get; set; }
    public DateOnly? EffectiveTo    { get; set; }
    public string    ApprovalStatus { get; set; } = SalaryAssignmentStatuses.Draft;
    public string?   ChangeReason   { get; set; }
    public Guid?     ApprovalUuid   { get; set; }
    public int?      ApprovedBy     { get; set; }
    public string?   ApprovedByName { get; set; }
    public DateTime? ApprovedAt     { get; set; }
    /// <summary>The increment proposal that created it (H6-C).</summary>
    public int?      IncrementProposalId { get; set; }

    public Employee?        Employee  { get; set; }
    public SalaryStructure? Structure { get; set; }
    public ICollection<SalaryAssignmentLine> Lines { get; set; } = new List<SalaryAssignmentLine>();
}

internal class SalaryAssignmentLine : HrmEntity
{
    public int     AssignmentId { get; set; }
    public int     ComponentId  { get; set; }
    public decimal Amount       { get; set; }

    public SalaryAssignment? Assignment { get; set; }
    public SalaryComponent?  Component  { get; set; }
}

/// <summary>§5.9 — a progressive tax table (G-21: data, not code; Unverified until reviewed, and then Finalize is blocked).</summary>
internal class TaxSlabTable : HrmVersionedEntity
{
    public string    Code          { get; set; } = string.Empty;
    public string    Name          { get; set; } = string.Empty;
    public string    CountryCode   { get; set; } = "PK";
    public DateOnly  EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo   { get; set; }
    public bool      IsVerified    { get; set; }
    public int?      VerifiedBy    { get; set; }
    public string?   VerifiedByName { get; set; }
    public DateTime? VerifiedAt    { get; set; }
    public string?   SourceNote    { get; set; }

    public ICollection<TaxSlab> Slabs { get; set; } = new List<TaxSlab>();
}

/// <summary>One bracket: annual income in [FromAmount, ToAmount): tax = FixedAmount + Rate% × (income − FromAmount). ToAmount null = no ceiling.</summary>
internal class TaxSlab : HrmEntity
{
    public int      TableId     { get; set; }
    public decimal  FromAmount  { get; set; }
    public decimal? ToAmount    { get; set; }
    public decimal  FixedAmount { get; set; }
    public decimal  RatePercent { get; set; }

    public TaxSlabTable? Table { get; set; }
}

/// <summary>§5.9 — EOBI / PF / SS / gratuity rates, effective-dated, Unverified until reviewed.</summary>
internal class StatutoryRate : HrmVersionedEntity
{
    public string    RateType         { get; set; } = StatutoryRateTypes.Eobi;
    public string    Name             { get; set; } = string.Empty;
    public string    CountryCode      { get; set; } = "PK";
    public decimal   EmployeePercent  { get; set; }
    public decimal   EmployerPercent  { get; set; }
    /// <summary>The wage the percent applies to is capped at this (null = the component's own basis).</summary>
    public decimal?  WageCeiling      { get; set; }
    /// <summary>A fixed basis instead of the employee's wage (EOBI: the notified minimum wage).</summary>
    public decimal?  FixedBasis       { get; set; }
    public DateOnly  EffectiveFrom    { get; set; }
    public DateOnly? EffectiveTo      { get; set; }
    public bool      IsVerified       { get; set; }
    public int?      VerifiedBy       { get; set; }
    public string?   VerifiedByName   { get; set; }
    public DateTime? VerifiedAt       { get; set; }
    public string?   SourceNote       { get; set; }
}

// ── H5 — Payroll (owner of behaviour: PAY; posting: POST) ───────────────────────────────────────────────────────────

/// <summary>§5.10 — unique non-cancelled run per period + group (filtered unique index).</summary>
internal class PayrollRun : HrmVersionedEntity
{
    /// <summary>e.g. PAY-2026-10; the SourceDocumentNumber / QuickBooks DocNumber.</summary>
    public string    RunNumber        { get; set; } = string.Empty;
    public DateOnly  PeriodStart      { get; set; }
    public DateOnly  PeriodEnd        { get; set; }
    public DateOnly  PayDate          { get; set; }
    /// <summary>"" = the default group, so the unique index works.</summary>
    public string    PayrollGroup     { get; set; } = string.Empty;
    public string    Status           { get; set; } = PayrollRunStatuses.Draft;
    public string    CurrencyCode     { get; set; } = string.Empty;
    public int       TotalEmployees   { get; set; }
    public int       BlockedEmployees { get; set; }
    public decimal   TotalGross       { get; set; }
    public decimal   TotalDeductions  { get; set; }
    public decimal   TotalNet         { get; set; }
    public decimal   TotalEmployerCost { get; set; }
    public DateTime? CalculatedAt     { get; set; }
    public int?      CalculatedBy     { get; set; }
    public DateTime? ReviewedAt       { get; set; }
    public int?      ReviewedBy       { get; set; }
    public DateTime? ApprovedAt       { get; set; }
    public int?      ApprovedBy       { get; set; }
    public DateTime? FinalizedAt      { get; set; }
    public int?      FinalizedBy      { get; set; }
    public DateTime? PostedAt         { get; set; }
    public DateTime? PaidAt           { get; set; }
    public int?      PaidBy           { get; set; }
    public string?   PaymentReference { get; set; }
    public string    PaidFromAccount  { get; set; } = HrmPaymentAccounts.Bank;
    /// <summary>The date on the posted journal (G-10) and the warning when it fell back to period end.</summary>
    public DateOnly? PostingDate      { get; set; }
    public string?   PostingNote      { get; set; }
    /// <summary>§11A.2: the mode that applied to this run; never re-classified afterwards.</summary>
    public string    CostCenterMode   { get; set; } = PayrollCostCenterModes.Central;
    public int?      ReversalOfRunId  { get; set; }
    public string?   ReversalReason   { get; set; }

    public PayrollRun? ReversalOfRun { get; set; }
    public ICollection<PayrollRunLine>  Lines      { get; set; } = new List<PayrollRunLine>();
    public ICollection<PayrollPosting>  Postings   { get; set; } = new List<PayrollPosting>();
}

/// <summary>§5.10 — S2. Unique (run, employee).</summary>
internal class PayrollRunLine : HrmEntity
{
    public int      RunId             { get; set; }
    public int      EmployeeId        { get; set; }
    public decimal  WorkingDays       { get; set; }
    public decimal  PaidDays          { get; set; }
    public decimal  WorkedDays        { get; set; }
    public decimal  Gross             { get; set; }
    public decimal  TotalDeductions   { get; set; }
    public decimal  Net               { get; set; }
    public decimal  TotalEmployerCost { get; set; }
    public bool     IsBlocked         { get; set; }
    public string?  BlockReason       { get; set; }
    /// <summary>Snapshots at calculation time.</summary>
    public string?  DepartmentName    { get; set; }
    public string?  DesignationName   { get; set; }
    public string?  CostCenterCode    { get; set; }
    public string   PaymentMethod     { get; set; } = BankPaymentMethods.Bank;

    public PayrollRun? Run      { get; set; }
    public Employee?   Employee { get; set; }
    public ICollection<PayrollRunLineComponent> Components { get; set; } = new List<PayrollRunLineComponent>();
    public Payslip? Payslip { get; set; }
}

/// <summary>§5.10 — S2, the immutable snapshot: a recalculation deletes and rewrites; a row is never edited.</summary>
internal class PayrollRunLineComponent : HrmEntity
{
    public int     LineId      { get; set; }
    public string  Code        { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string  Type        { get; set; } = SalaryComponentTypes.Earning;
    public decimal Amount      { get; set; }
    public decimal? Basis      { get; set; }
    public string? PostingRole { get; set; }
    public string? ExpenseRole { get; set; }
    public bool    IsTaxable   { get; set; }
    public bool    InGross     { get; set; }
    public int     Sequence    { get; set; }
    /// <summary>Allocation for §11A.2 percentage splits; null = the line's cost center.</summary>
    public string? CostCenterCode { get; set; }

    public PayrollRunLine? Line { get; set; }
}

/// <summary>§5.10 — immutable PDF, kept as a generated workflow attachment (AttachmentUuid is the spec's FileKey).</summary>
internal class Payslip : HrmEntity
{
    public int      RunLineId      { get; set; }
    public Guid     AttachmentUuid { get; set; }
    public DateTime GeneratedAt    { get; set; } = DateTime.UtcNow;
    public string   FileName       { get; set; } = string.Empty;

    public PayrollRunLine? RunLine { get; set; }
}

/// <summary>§5.10 — one-off item (Odoo hr.payslip.input). RunId null = "next run for this employee".</summary>
internal class PayrollAdjustment : HrmVersionedEntity
{
    public int      EmployeeId    { get; set; }
    public int?     RunId         { get; set; }
    public string   ComponentCode { get; set; } = string.Empty;
    public decimal  Amount        { get; set; }
    public string   Reason        { get; set; } = string.Empty;
    /// <summary>Set once a run consumed it.</summary>
    public int?     AppliedInRunId { get; set; }
    /// <summary>EXPREIMB from an expense claim settled with payroll.</summary>
    public int?     ExpenseClaimId { get; set; }

    public Employee?     Employee      { get; set; }
    public PayrollRun?   Run           { get; set; }
    public PayrollRun?   AppliedInRun  { get; set; }
    public ExpenseClaim? ExpenseClaim  { get; set; }
}

/// <summary>§5.10 / §7.7 — the run's posting attempts, per kind, as HRM sees them (target detail is in finance.FinancialPostings).</summary>
internal class PayrollPosting : HrmEntity
{
    public int       RunId           { get; set; }
    public string    Kind            { get; set; } = PayrollPostingKinds.PayrollRun;
    public string    IdempotencyKey  { get; set; } = string.Empty;
    public string    Status          { get; set; } = PayrollPostingStatuses.Pending;
    public DateOnly? PostingDate     { get; set; }
    public decimal   TotalDebit      { get; set; }
    public decimal   TotalCredit     { get; set; }
    /// <summary>JSON: target code → status / internal id / external id / error (no per-employee data).</summary>
    public string?   TargetResultsJson { get; set; }
    public string?   LastError       { get; set; }
    public DateTime? LastAttemptAt   { get; set; }
    public int       Attempts        { get; set; }

    public PayrollRun? Run { get; set; }
}

// ── H6 — Loans, settlement, increments, letters (owner of behaviour: LIFE) ──────────────────────────────────────────

/// <summary>§5.11.</summary>
internal class Loan : HrmVersionedEntity
{
    public string    LoanNumber       { get; set; } = string.Empty;
    public int       EmployeeId       { get; set; }
    public string    Type             { get; set; } = LoanTypes.Personal;
    public decimal   Principal        { get; set; }
    public int       InstallmentCount { get; set; }
    public decimal   MonthlyAmount    { get; set; }
    /// <summary>First day of the first deduction month.</summary>
    public DateOnly  StartMonth       { get; set; }
    public string    Status           { get; set; } = LoanStatuses.Draft;
    public string?   Reason           { get; set; }
    public int?      ApprovedBy       { get; set; }
    public string?   ApprovedByName   { get; set; }
    public DateTime? ApprovedAt       { get; set; }
    public DateOnly? DisbursedOn      { get; set; }
    public int?      DisbursedBy      { get; set; }
    public string    PaidFromAccount  { get; set; } = HrmPaymentAccounts.Bank;
    public string?   PaymentReference { get; set; }
    public string    PostingState     { get; set; } = HrmPostingStates.None;
    public string?   PostingNote      { get; set; }

    public Employee? Employee { get; set; }
    public ICollection<LoanInstallment> Installments { get; set; } = new List<LoanInstallment>();
}

/// <summary>§5.11.</summary>
internal class LoanInstallment : HrmEntity
{
    public int      LoanId   { get; set; }
    public int      Sequence { get; set; }
    public DateOnly DueMonth { get; set; }
    public decimal  Amount   { get; set; }
    public string   Status   { get; set; } = LoanInstallmentStatuses.Pending;
    public int?     RunId    { get; set; }
    public string?  Note     { get; set; }

    public Loan?       Loan { get; set; }
    public PayrollRun? Run  { get; set; }
}

/// <summary>§5.11.</summary>
internal class FinalSettlement : HrmVersionedEntity
{
    public string    SettlementNumber { get; set; } = string.Empty;
    public int       EmployeeId       { get; set; }
    public DateOnly  LastWorkingDay   { get; set; }
    public decimal   ServiceYears     { get; set; }
    public decimal   LastDrawnSalary  { get; set; }
    public decimal   TotalEarnings    { get; set; }
    public decimal   TotalDeductions  { get; set; }
    public decimal   Net              { get; set; }
    public string    Status           { get; set; } = SettlementStatuses.Draft;
    public string?   Notes            { get; set; }
    public int?      ApprovedBy       { get; set; }
    public DateTime? ApprovedAt       { get; set; }
    public DateOnly? PaidOn           { get; set; }
    public string    PaidFromAccount  { get; set; } = HrmPaymentAccounts.Bank;
    public string?   PaymentReference { get; set; }
    public string    PostingState     { get; set; } = HrmPostingStates.None;
    public string?   PostingNote      { get; set; }

    public Employee? Employee { get; set; }
    public ICollection<FinalSettlementLine> Lines { get; set; } = new List<FinalSettlementLine>();
}

/// <summary>§5.11 — replaces the register's Components JSON: lines are queryable and post by role.</summary>
internal class FinalSettlementLine : HrmEntity
{
    public int     SettlementId { get; set; }
    public string  Code         { get; set; } = string.Empty;
    public string  Description  { get; set; } = string.Empty;
    public string  Type         { get; set; } = SalaryComponentTypes.Earning;
    public decimal Amount       { get; set; }
    public decimal? Basis       { get; set; }
    public string? PostingRole  { get; set; }
    public int     Sequence     { get; set; }

    public FinalSettlement? Settlement { get; set; }
}

/// <summary>§5.11.</summary>
internal class IncrementProposal : HrmVersionedEntity
{
    public int       EmployeeId     { get; set; }
    public decimal?  NewBasic       { get; set; }
    public decimal?  Percent        { get; set; }
    public DateOnly  EffectiveDate  { get; set; }
    public int?      ReviewId       { get; set; }
    public string    Status         { get; set; } = IncrementStatuses.Draft;
    public string?   Reason         { get; set; }
    public int?      DecidedBy      { get; set; }
    public string?   DecidedByName  { get; set; }
    public DateTime? DecidedAt      { get; set; }
    public int?      AppliedAssignmentId { get; set; }

    public Employee?          Employee          { get; set; }
    public PerformanceReview? Review            { get; set; }
    public SalaryAssignment?  AppliedAssignment { get; set; }
}

/// <summary>Task 375 — body with {Placeholders}; IsSystem seeded per organization, editable text.</summary>
internal class LetterTemplate : HrmVersionedEntity
{
    public string  Code     { get; set; } = string.Empty;
    public string  Name     { get; set; } = string.Empty;
    public string  Kind     { get; set; } = LetterTemplateCodes.Custom;
    public string  Subject  { get; set; } = string.Empty;
    public string  Body     { get; set; } = string.Empty;
    public bool    IsSystem { get; set; }
    public bool    IsActive { get; set; } = true;
}

/// <summary>§5.11.</summary>
internal class IssuedLetter : HrmEntity
{
    public string   LetterNumber   { get; set; } = string.Empty;
    public int      EmployeeId     { get; set; }
    public int?     TemplateId     { get; set; }
    public string   Kind           { get; set; } = LetterTemplateCodes.Custom;
    public Guid     AttachmentUuid { get; set; }
    public DateTime IssuedAt       { get; set; } = DateTime.UtcNow;
    public int?     IssuedBy       { get; set; }
    public string?  IssuedByName   { get; set; }

    public Employee?       Employee { get; set; }
    public LetterTemplate? Template { get; set; }
}

/// <summary>§5.11 — unique (employee, tax year).</summary>
internal class TaxCertificate : HrmEntity
{
    public int      EmployeeId     { get; set; }
    /// <summary>The tax year's start date year, e.g. 2026 for 2026-07-01 .. 2027-06-30.</summary>
    public int      TaxYear        { get; set; }
    public decimal  TotalIncome    { get; set; }
    public decimal  TotalTax       { get; set; }
    public Guid?    AttachmentUuid { get; set; }
    public DateTime GeneratedAt    { get; set; } = DateTime.UtcNow;

    public Employee? Employee { get; set; }
}

// ── H6E — Expense claims and remittances (owner of behaviour: LIFE) ─────────────────────────────────────────────────

/// <summary>§5.12 / G-9. AccountCode = an explicit CoA leaf code; null falls back to AccountRole. Rate fields: per-diem / mileage (§11A.4).</summary>
internal class ExpenseCategory : HrmVersionedEntity
{
    public string   Code             { get; set; } = string.Empty;
    public string   Name             { get; set; } = string.Empty;
    public string?  AccountCode      { get; set; }
    public string?  AccountRole      { get; set; }
    public string?  DefaultTaxCode   { get; set; }
    public bool     RequiresReceipt  { get; set; }
    public decimal? ReceiptThreshold { get; set; }
    public decimal? RatePerUnit      { get; set; }
    public string?  UnitName         { get; set; }
    public bool     IsActive         { get; set; } = true;
    public bool     IsSeeded         { get; set; }
}

/// <summary>§5.12.</summary>
internal class ExpenseClaim : HrmVersionedEntity
{
    public string    ClaimNumber      { get; set; } = string.Empty;
    public int       EmployeeId       { get; set; }
    public DateOnly  ClaimDate        { get; set; }
    public string    Status           { get; set; } = ExpenseClaimStatuses.Draft;
    public string    PaymentMode      { get; set; } = ExpensePaymentModes.OwnAccount;
    public string    SettlementMethod { get; set; } = ExpenseSettlementMethods.WithPayroll;
    public string    CurrencyCode     { get; set; } = string.Empty;
    public decimal   TotalAmount      { get; set; }
    public decimal   TotalTax         { get; set; }
    public string?   Description      { get; set; }
    public Guid?     ApprovalUuid     { get; set; }
    public DateTime? SubmittedAt      { get; set; }
    public int?      DecidedBy        { get; set; }
    public string?   DecidedByName    { get; set; }
    public DateTime? DecidedAt        { get; set; }
    public string?   DecisionNote     { get; set; }
    public DateOnly? PaidOn           { get; set; }
    public int?      PaidInRunId      { get; set; }
    public string    PaidFromAccount  { get; set; } = HrmPaymentAccounts.Bank;
    public string?   PaymentReference { get; set; }
    public string    PostingState     { get; set; } = HrmPostingStates.None;
    public string    PaymentPostingState { get; set; } = HrmPostingStates.None;
    public string?   PostingNote      { get; set; }

    public Employee?   Employee    { get; set; }
    public PayrollRun? PaidInRun   { get; set; }
    public ICollection<ExpenseLine> Lines { get; set; } = new List<ExpenseLine>();
}

/// <summary>§5.12.</summary>
internal class ExpenseLine : HrmEntity
{
    public int       ClaimId        { get; set; }
    public int       CategoryId     { get; set; }
    public DateOnly  ExpenseDate    { get; set; }
    public string?   Description    { get; set; }
    public decimal   Amount         { get; set; }
    public decimal?  Quantity       { get; set; }
    public string?   TaxCode        { get; set; }
    public decimal   TaxAmount      { get; set; }
    public string?   CostCenterCode { get; set; }
    public Guid?     AttachmentUuid { get; set; }

    public ExpenseClaim?    Claim    { get; set; }
    public ExpenseCategory? Category { get; set; }
}

/// <summary>G-13 — tax / EOBI / PF paid to the authority; vendor-less, details stay in HRM.</summary>
internal class StatutoryRemittance : HrmVersionedEntity
{
    public string   RemittanceNumber { get; set; } = string.Empty;
    public string   RemittanceType   { get; set; } = RemittanceTypes.IncomeTax;
    public DateOnly PeriodStart      { get; set; }
    public DateOnly PeriodEnd        { get; set; }
    public decimal  Amount           { get; set; }
    public DateOnly PaidOn           { get; set; }
    public string   PaidFromAccount  { get; set; } = HrmPaymentAccounts.Bank;
    public string?  Reference        { get; set; }
    public string?  AuthorityName    { get; set; }
    public string?  Notes            { get; set; }
    public string   PostingState     { get; set; } = HrmPostingStates.None;
    public string?  PostingNote      { get; set; }
}

// ── H4 — Recruitment, performance, training (owner of behaviour: TAL) ───────────────────────────────────────────────

/// <summary>§5.13 / task 201.</summary>
internal class Vacancy : HrmVersionedEntity
{
    public string    VacancyNumber { get; set; } = string.Empty;
    public string    Title         { get; set; } = string.Empty;
    public int       DesignationId { get; set; }
    public int       DepartmentId  { get; set; }
    public int?      BranchId      { get; set; }
    public int       Positions     { get; set; } = 1;
    public string    Status        { get; set; } = VacancyStatuses.Open;
    public string?   Description   { get; set; }
    public DateOnly? ClosingDate   { get; set; }

    public Designation? Designation { get; set; }
    public Department?  Department  { get; set; }
    public Branch?      Branch      { get; set; }
}

/// <summary>Task 202 — S1. Duplicate detection on (email / phone): a service check with non-unique indexes.</summary>
internal class Candidate : HrmVersionedEntity
{
    public string  FirstName            { get; set; } = string.Empty;
    public string  LastName             { get; set; } = string.Empty;
    public string? Email                { get; set; }
    public string? Phone                { get; set; }
    public string? Source               { get; set; }
    public Guid?   ResumeAttachmentUuid { get; set; }
    public string? Notes                { get; set; }
    public bool    IsDeleted            { get; set; }
}

/// <summary>Task 203 — Odoo hr.applicant with stages. Unique (candidate, vacancy).</summary>
internal class JobApplication : HrmVersionedEntity
{
    public int      CandidateId { get; set; }
    public int      VacancyId   { get; set; }
    public string   Stage       { get; set; } = ApplicationStages.New;
    public int?     Rating      { get; set; }
    public string?  RejectionReason { get; set; }
    public int?     HiredEmployeeId { get; set; }

    public Candidate? Candidate      { get; set; }
    public Vacancy?   Vacancy        { get; set; }
    public Employee?  HiredEmployee  { get; set; }
}

/// <summary>Task 204.</summary>
internal class Interview : HrmVersionedEntity
{
    public int      ApplicationId         { get; set; }
    public int?     InterviewerEmployeeId { get; set; }
    public DateTime ScheduledAt           { get; set; }
    public string?  Feedback              { get; set; }
    public decimal? Score                 { get; set; }
    public string   Result                { get; set; } = InterviewResults.Pending;

    public JobApplication? Application { get; set; }
    public Employee?       Interviewer { get; set; }
}

/// <summary>§5.13 "Offer" (task 212): S2 offered salary; the letter is an IssuedLetter-style generated PDF.</summary>
internal class JobOffer : HrmVersionedEntity
{
    public int       ApplicationId  { get; set; }
    public decimal   OfferedSalary  { get; set; }
    public DateOnly? ProposedJoinDate { get; set; }
    public string    Status         { get; set; } = OfferStatuses.Draft;
    public Guid?     LetterAttachmentUuid { get; set; }
    public string?   Notes          { get; set; }

    public JobApplication? Application { get; set; }
}

/// <summary>Task 215.</summary>
internal class ReviewCycle : HrmVersionedEntity
{
    public string   Name      { get; set; } = string.Empty;
    public string   Period    { get; set; } = ReviewPeriods.Annual;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate   { get; set; }
    public string   Status    { get; set; } = ReviewCycleStatuses.Draft;
}

/// <summary>Task 216 — unique (employee, cycle).</summary>
internal class PerformanceReview : HrmVersionedEntity
{
    public int       EmployeeId         { get; set; }
    public int       ReviewCycleId      { get; set; }
    public int?      ReviewerEmployeeId { get; set; }
    public decimal?  OverallRating      { get; set; }
    public string    Status             { get; set; } = ReviewStatuses.Draft;
    public string?   ManagerComments    { get; set; }
    public string?   EmployeeComments   { get; set; }
    public DateTime? SubmittedAt        { get; set; }
    public DateTime? AcknowledgedAt     { get; set; }

    public Employee?    Employee { get; set; }
    public ReviewCycle? Cycle    { get; set; }
    public Employee?    Reviewer { get; set; }
}

/// <summary>Task 217.</summary>
internal class Goal : HrmVersionedEntity
{
    public int      EmployeeId    { get; set; }
    public int      ReviewCycleId { get; set; }
    public string   Title         { get; set; } = string.Empty;
    public string?  Description   { get; set; }
    public decimal  Weight        { get; set; }
    public decimal? SelfRating    { get; set; }
    public decimal? ManagerRating { get; set; }
    public string   Status        { get; set; } = GoalStatuses.Open;

    public Employee?    Employee { get; set; }
    public ReviewCycle? Cycle    { get; set; }
}

/// <summary>Task 225.</summary>
internal class Course : HrmVersionedEntity
{
    public string   Code        { get; set; } = string.Empty;
    public string   Name        { get; set; } = string.Empty;
    public string?  Description { get; set; }
    public string?  Provider    { get; set; }
    public decimal? DurationHours { get; set; }
    public decimal? Cost        { get; set; }
    public bool     IsActive    { get; set; } = true;
}

/// <summary>Task 226 — unique (employee, course, enrolled date).</summary>
internal class CourseEnrollment : HrmVersionedEntity
{
    public int       EmployeeId    { get; set; }
    public int       CourseId      { get; set; }
    public DateOnly  EnrolledDate  { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public string    Status        { get; set; } = EnrollmentStatuses.Enrolled;
    public Guid?     CertificateAttachmentUuid { get; set; }

    public Employee? Employee { get; set; }
    public Course?   Course   { get; set; }
}
