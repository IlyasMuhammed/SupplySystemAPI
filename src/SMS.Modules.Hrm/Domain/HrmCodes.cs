namespace SMS.Modules.Hrm.Domain;

// A49 — every persisted vocabulary of the hrm schema (docs/hrm/API-CONTRACT.md §0.1). Statuses are strings with CHECK
// constraints built from these lists. Owner: CORE; a new value needs a schema request.

public static class EmployeeStatuses
{
    public const string Candidate = "Candidate", Probation = "Probation", Active = "Active", OnNotice = "OnNotice",
        Suspended = "Suspended", Terminated = "Terminated", Archived = "Archived";
    public static readonly IReadOnlyList<string> All = [Candidate, Probation, Active, OnNotice, Suspended, Terminated, Archived];

    /// <summary>Everyone still on the books (payroll, leave allocation, attendance).</summary>
    public static readonly IReadOnlyList<string> Employed = [Probation, Active, OnNotice, Suspended];

    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
    {
        [Candidate]  = [Probation, Active, Archived],
        [Probation]  = [Active, OnNotice, Suspended, Terminated],
        [Active]     = [OnNotice, Suspended, Terminated],
        [OnNotice]   = [Active, Terminated],
        [Suspended]  = [Active, OnNotice, Terminated],
        [Terminated] = [Archived],
        [Archived]   = [],
    };

    public static bool CanMove(string from, string to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);
    public static IReadOnlyList<string> NextFrom(string from) => Transitions.TryGetValue(from, out var next) ? next : [];
}

public static class Genders { public const string Male = "M", Female = "F", Other = "Other"; public static readonly IReadOnlyList<string> All = [Male, Female, Other]; }

public static class MaritalStatuses
{
    public const string Single = "Single", Married = "Married", Widowed = "Widowed", Divorced = "Divorced", Other = "Other";
    public static readonly IReadOnlyList<string> All = [Single, Married, Widowed, Divorced, Other];
}

public static class DepartureReasons
{
    public const string Resignation = "Resignation", Retirement = "Retirement", Termination = "Termination", ContractEnd = "ContractEnd",
        Death = "Death", Other = "Other";
    public static readonly IReadOnlyList<string> All = [Resignation, Retirement, Termination, ContractEnd, Death, Other];
}

public static class EmploymentTypes
{
    public const string Permanent = "Permanent", Contract = "Contract", Probation = "Probation", Intern = "Intern", DailyWage = "DailyWage";
    public static readonly IReadOnlyList<string> All = [Permanent, Contract, Probation, Intern, DailyWage];
}

public static class EmploymentChangeReasons
{
    public const string Hire = "Hire", Transfer = "Transfer", Promotion = "Promotion", Confirmation = "Confirmation", Demotion = "Demotion",
        ContractRenewal = "ContractRenewal", Correction = "Correction", Other = "Other";
    public static readonly IReadOnlyList<string> All = [Hire, Transfer, Promotion, Confirmation, Demotion, ContractRenewal, Correction, Other];
}

public static class BankPaymentMethods
{
    public const string Bank = "Bank", Cash = "Cash", Cheque = "Cheque";
    public static readonly IReadOnlyList<string> All = [Bank, Cash, Cheque];
}

/// <summary>§11A.2 FD-9.</summary>
public static class PayrollCostCenterModes
{
    public const string Central = "CENTRAL", ByDepartment = "BY_DEPARTMENT";
    public static readonly IReadOnlyList<string> All = [Central, ByDepartment];
}

/// <summary>G-10 FD-7 (default <see cref="PayDateWithFallback"/>).</summary>
public static class PayrollPostDatePolicies
{
    public const string PayDate = "PayDate", PeriodEnd = "PeriodEnd", PayDateWithFallback = "PayDateWithFallback";
    public static readonly IReadOnlyList<string> All = [PayDate, PeriodEnd, PayDateWithFallback];
}

/// <summary>§3.3 — explicit assignment of an HR scope to a user (hrm.HrUserScopes).</summary>
public static class HrScopeTypes
{
    public const string Branch = "Branch", Department = "Department";
    public static readonly IReadOnlyList<string> All = [Branch, Department];
}

public static class EmployeeDocumentTypes
{
    public const string Cnic = "CNIC", Contract = "CONTRACT", Certificate = "CERTIFICATE", Resume = "RESUME", Photo = "PHOTO", Other = "OTHER";
    public static readonly IReadOnlyList<string> All = [Cnic, Contract, Certificate, Resume, Photo, Other];
}

// ── H2 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class AttendanceSources
{
    public const string Manual = "MANUAL", Device = "DEVICE", Import = "IMPORT", Web = "WEB";
    public static readonly IReadOnlyList<string> All = [Manual, Device, Import, Web];
}

public static class AttendanceEventTypes
{
    public const string In = "IN", Out = "OUT", Unknown = "UNKNOWN";
    public static readonly IReadOnlyList<string> All = [In, Out, Unknown];
}

public static class AttendanceDayStatuses
{
    public const string Present = "Present", Late = "Late", HalfDay = "HalfDay", Absent = "Absent", OnLeave = "OnLeave", Holiday = "Holiday",
        WeeklyOff = "WeeklyOff";
    public static readonly IReadOnlyList<string> All = [Present, Late, HalfDay, Absent, OnLeave, Holiday, WeeklyOff];
}

public static class ApprovalStatuses
{
    public const string Pending = "Pending", Approved = "Approved", Rejected = "Rejected";
    public static readonly IReadOnlyList<string> All = [Pending, Approved, Rejected];
}

public static class LeaveAccrualMethods
{
    public const string Annual = "Annual", Monthly = "Monthly", None = "None";
    public static readonly IReadOnlyList<string> All = [Annual, Monthly, None];
}

public static class LeaveRequestStatuses
{
    public const string Pending = "Pending", Approved = "Approved", Rejected = "Rejected", Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = [Pending, Approved, Rejected, Cancelled];
}

public static class LeaveLedgerReasons
{
    public const string Allocation = "Allocation", Accrual = "Accrual", CarryForward = "CarryForward", Expiry = "Expiry", Consumption = "Consumption",
        CancellationRestore = "CancellationRestore", Adjustment = "Adjustment", Opening = "Opening", Encashment = "Encashment";
    public static readonly IReadOnlyList<string> All = [Allocation, Accrual, CarryForward, Expiry, Consumption, CancellationRestore, Adjustment, Opening, Encashment];
}

// ── H3 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class SalaryComponentTypes
{
    public const string Earning = "Earning", Deduction = "Deduction", EmployerContribution = "EmployerContribution", Result = "Result";
    public static readonly IReadOnlyList<string> All = [Earning, Deduction, EmployerContribution, Result];
}

public static class SalaryCalcMethods
{
    public const string Fixed = "Fixed", Percent = "Percent", Formula = "Formula";
    public static readonly IReadOnlyList<string> All = [Fixed, Percent, Formula];
}

public static class SalaryAssignmentStatuses
{
    public const string Draft = "Draft", PendingApproval = "PendingApproval", Approved = "Approved", Rejected = "Rejected", Superseded = "Superseded";
    public static readonly IReadOnlyList<string> All = [Draft, PendingApproval, Approved, Rejected, Superseded];
}

public static class StatutoryRateTypes
{
    public const string Eobi = "EOBI", ProvidentFund = "PF", SocialSecurity = "SS", Gratuity = "GRATUITY";
    public static readonly IReadOnlyList<string> All = [Eobi, ProvidentFund, SocialSecurity, Gratuity];
}

// ── H4 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class VacancyStatuses
{
    public const string Open = "Open", OnHold = "OnHold", Closed = "Closed";
    public static readonly IReadOnlyList<string> All = [Open, OnHold, Closed];
}

public static class ApplicationStages
{
    public const string New = "New", Screening = "Screening", Interview = "Interview", Offer = "Offer", Hired = "Hired", Rejected = "Rejected";
    public static readonly IReadOnlyList<string> All = [New, Screening, Interview, Offer, Hired, Rejected];
}

public static class InterviewResults
{
    public const string Pending = "Pending", Passed = "Passed", Failed = "Failed", NoShow = "NoShow";
    public static readonly IReadOnlyList<string> All = [Pending, Passed, Failed, NoShow];
}

public static class OfferStatuses
{
    public const string Draft = "Draft", Sent = "Sent", Accepted = "Accepted", Declined = "Declined", Withdrawn = "Withdrawn";
    public static readonly IReadOnlyList<string> All = [Draft, Sent, Accepted, Declined, Withdrawn];
}

public static class ReviewPeriods
{
    public const string Annual = "Annual", Quarterly = "Quarterly";
    public static readonly IReadOnlyList<string> All = [Annual, Quarterly];
}

public static class ReviewCycleStatuses
{
    public const string Draft = "Draft", Open = "Open", Closed = "Closed";
    public static readonly IReadOnlyList<string> All = [Draft, Open, Closed];
}

public static class ReviewStatuses
{
    public const string Draft = "Draft", Submitted = "Submitted", Acknowledged = "Acknowledged";
    public static readonly IReadOnlyList<string> All = [Draft, Submitted, Acknowledged];
}

public static class GoalStatuses
{
    public const string Open = "Open", Achieved = "Achieved", Missed = "Missed", Dropped = "Dropped";
    public static readonly IReadOnlyList<string> All = [Open, Achieved, Missed, Dropped];
}

public static class EnrollmentStatuses
{
    public const string Enrolled = "Enrolled", Completed = "Completed", Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = [Enrolled, Completed, Cancelled];
}

// ── H5 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class PayrollRunStatuses
{
    public const string Draft = "Draft", Calculated = "Calculated", UnderReview = "UnderReview", Approved = "Approved", Finalized = "Finalized",
        Posted = "Posted", Paid = "Paid", Cancelled = "Cancelled", Reversed = "Reversed";
    public static readonly IReadOnlyList<string> All = [Draft, Calculated, UnderReview, Approved, Finalized, Posted, Paid, Cancelled, Reversed];
}

/// <summary>Per-target state of a run's postings (hrm.PayrollPostings). "NoTarget" is G-19: finalized, no posting required.</summary>
public static class PayrollPostingStatuses
{
    public const string Pending = "Pending", Posted = "Posted", Failed = "Failed", Held = "Held", NoTarget = "NoTarget", Reversed = "Reversed";
    public static readonly IReadOnlyList<string> All = [Pending, Posted, Failed, Held, NoTarget, Reversed];
}

public static class PayrollPostingKinds
{
    public const string PayrollRun = "PayrollRun", PayrollPayment = "PayrollPayment", Void = "Void";
    public static readonly IReadOnlyList<string> All = [PayrollRun, PayrollPayment, Void];
}

// ── H6 / H6E ────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class LoanTypes
{
    public const string Advance = "Advance", Personal = "Personal", Housing = "Housing";
    public static readonly IReadOnlyList<string> All = [Advance, Personal, Housing];
}

public static class LoanStatuses
{
    public const string Draft = "Draft", Approved = "Approved", Disbursed = "Disbursed", Closed = "Closed", Rejected = "Rejected", Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = [Draft, Approved, Disbursed, Closed, Rejected, Cancelled];
}

public static class LoanInstallmentStatuses
{
    public const string Pending = "Pending", Deducted = "Deducted", Waived = "Waived";
    public static readonly IReadOnlyList<string> All = [Pending, Deducted, Waived];
}

public static class SettlementStatuses
{
    public const string Draft = "Draft", Approved = "Approved", Posted = "Posted", Paid = "Paid", Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = [Draft, Approved, Posted, Paid, Cancelled];
}

public static class IncrementStatuses
{
    public const string Draft = "Draft", Approved = "Approved", Applied = "Applied", Rejected = "Rejected";
    public static readonly IReadOnlyList<string> All = [Draft, Approved, Applied, Rejected];
}

public static class LetterTemplateCodes
{
    public const string OfferLetter = "OfferLetter", ExperienceCertificate = "ExperienceCertificate", SalaryCertificate = "SalaryCertificate", Custom = "Custom";
    public static readonly IReadOnlyList<string> All = [OfferLetter, ExperienceCertificate, SalaryCertificate, Custom];
}

public static class ExpenseClaimStatuses
{
    public const string Draft = "Draft", Submitted = "Submitted", Approved = "Approved", Paid = "Paid", Rejected = "Rejected";
    public static readonly IReadOnlyList<string> All = [Draft, Submitted, Approved, Paid, Rejected];
}

public static class ExpensePaymentModes
{
    public const string OwnAccount = "OwnAccount", CompanyAccount = "CompanyAccount";
    public static readonly IReadOnlyList<string> All = [OwnAccount, CompanyAccount];
}

public static class ExpenseSettlementMethods
{
    public const string WithPayroll = "WithPayroll", SeparatePayment = "SeparatePayment";
    public static readonly IReadOnlyList<string> All = [WithPayroll, SeparatePayment];
}

public static class RemittanceTypes
{
    public const string IncomeTax = "IncomeTax", Eobi = "EOBI", ProvidentFund = "PF", SocialSecurity = "SS";
    public static readonly IReadOnlyList<string> All = [IncomeTax, Eobi, ProvidentFund, SocialSecurity];
}

/// <summary>Posting state of a one-document HRM posting (loan disbursement, expense, remittance, settlement).</summary>
public static class HrmPostingStates
{
    public const string None = "None", Pending = "Pending", Posted = "Posted", Failed = "Failed", NoTarget = "NoTarget";
    public static readonly IReadOnlyList<string> All = [None, Pending, Posted, Failed, NoTarget];
}

public static class HrmPaymentAccounts
{
    public const string Bank = "Bank", Cash = "Cash";
    public static readonly IReadOnlyList<string> All = [Bank, Cash];
}

/// <summary>S3 reads are audited (§5): hrm.SensitiveAccessLogs.Field.</summary>
public static class SensitiveFields
{
    public const string Cnic = "CNIC", Ntn = "NTN", BankAccount = "BANK_ACCOUNT", Iban = "IBAN";
}

/// <summary>Interface codes for api/attachments and generated documents (AttachmentAccessPolicy).</summary>
public static class HrmAttachmentCodes
{
    public const string Employee = "HRM_EMPLOYEE", LeaveRequest = "HRM_LEAVE_REQUEST", ExpenseClaim = "HRM_EXPENSE_CLAIM", Candidate = "HRM_CANDIDATE",
        Enrollment = "HRM_ENROLLMENT", Payslip = "HRM_PAYSLIP", Letter = "HRM_LETTER", TaxCertificate = "HRM_TAX_CERTIFICATE";
}
