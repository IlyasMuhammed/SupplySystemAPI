namespace SMS.Modules.Hrm.Models;

// API-CONTRACT §11, §12 (LIFE).

public sealed class InstallmentModel { public Guid Uuid { get; set; } public int Sequence { get; set; } public DateOnly DueMonth { get; set; } public decimal Amount { get; set; } public string Status { get; set; } = ""; public string? RunNumber { get; set; } }

public sealed class LoanModel
{
    public Guid Uuid { get; set; } public string LoanNumber { get; set; } = ""; public Guid EmployeeUuid { get; set; } public string EmployeeName { get; set; } = "";
    public string Type { get; set; } = ""; public decimal Principal { get; set; } public int InstallmentCount { get; set; } public decimal MonthlyAmount { get; set; }
    public DateOnly StartMonth { get; set; } public string Status { get; set; } = ""; public string? Reason { get; set; } public string? ApprovedByName { get; set; } public DateTime? ApprovedAt { get; set; }
    public DateOnly? DisbursedOn { get; set; } public string PaidFromAccount { get; set; } = ""; public string? PaymentReference { get; set; }
    public string PostingState { get; set; } = ""; public string? PostingNote { get; set; } public decimal Outstanding { get; set; }
    public List<InstallmentModel> Installments { get; set; } = [];
}

public sealed class LoanUpsert { public Guid EmployeeUuid { get; set; } public string? Type { get; set; } public decimal Principal { get; set; } public int InstallmentCount { get; set; } public DateOnly StartMonth { get; set; } public string? Reason { get; set; } }
public sealed class LifeNoteRequest { public string? Note { get; set; } }
public sealed class LoanDisburseRequest { public DateOnly DisbursedOn { get; set; } public string? PaidFromAccount { get; set; } public string? Reference { get; set; } }

public sealed class SettlementLineModel { public string Code { get; set; } = ""; public string Description { get; set; } = ""; public string Type { get; set; } = ""; public decimal Amount { get; set; } public decimal? Basis { get; set; } public string? PostingRole { get; set; } }

public sealed class SettlementModel
{
    public Guid Uuid { get; set; } public string SettlementNumber { get; set; } = ""; public Guid EmployeeUuid { get; set; } public string EmployeeName { get; set; } = "";
    public DateOnly LastWorkingDay { get; set; } public decimal ServiceYears { get; set; } public decimal LastDrawnSalary { get; set; } public decimal TotalEarnings { get; set; } public decimal TotalDeductions { get; set; } public decimal Net { get; set; }
    public string Status { get; set; } = ""; public string? Notes { get; set; } public DateOnly? PaidOn { get; set; } public string PaidFromAccount { get; set; } = ""; public string? PaymentReference { get; set; }
    public string PostingState { get; set; } = ""; public string? PostingNote { get; set; } public List<SettlementLineModel> Lines { get; set; } = [];
}

public sealed class SettlementComputeRequest { public Guid EmployeeUuid { get; set; } public DateOnly LastWorkingDay { get; set; } }
public sealed class SettlementUpdate { public string? Notes { get; set; } public List<SettlementLineModel> Lines { get; set; } = []; }
public sealed class LifePayRequest { public DateOnly PaidOn { get; set; } public string? PaidFromAccount { get; set; } public string? Reference { get; set; } }

public sealed class IncrementModel
{
    public Guid Uuid { get; set; } public Guid EmployeeUuid { get; set; } public string EmployeeName { get; set; } = ""; public decimal? CurrentBasic { get; set; } public decimal? NewBasic { get; set; } public decimal? Percent { get; set; }
    public DateOnly EffectiveDate { get; set; } public Guid? ReviewUuid { get; set; } public string Status { get; set; } = ""; public string? Reason { get; set; } public string? DecidedByName { get; set; } public Guid? AppliedAssignmentUuid { get; set; }
}

public sealed class IncrementCreate { public Guid EmployeeUuid { get; set; } public decimal? NewBasic { get; set; } public decimal? Percent { get; set; } public DateOnly EffectiveDate { get; set; } public Guid? ReviewUuid { get; set; } public string? Reason { get; set; } }
public sealed class IncrementBulkRequest { public Guid ReviewCycleUuid { get; set; } public Dictionary<string, decimal> PercentByRating { get; set; } = []; public DateOnly? EffectiveDate { get; set; } }

public sealed class LetterTemplateModel { public Guid Uuid { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Kind { get; set; } = ""; public string Subject { get; set; } = ""; public string Body { get; set; } = ""; public bool IsSystem { get; set; } public bool IsActive { get; set; } }
public sealed class LetterTemplateUpsert { public string? Code { get; set; } public string? Name { get; set; } public string? Kind { get; set; } public string? Subject { get; set; } public string? Body { get; set; } public bool? IsActive { get; set; } }
public sealed class IssuedLetterModel { public Guid Uuid { get; set; } public string LetterNumber { get; set; } = ""; public Guid EmployeeUuid { get; set; } public string EmployeeName { get; set; } = ""; public string Kind { get; set; } = ""; public DateTime IssuedAt { get; set; } public string? IssuedByName { get; set; } }
/// <summary>OrganizationName: used only when the organization-name lookup gives nothing.</summary>
public sealed class LetterIssueRequest { public Guid EmployeeUuid { get; set; } public Guid TemplateUuid { get; set; } public string? OrganizationName { get; set; } }

public sealed class TaxCertificateModel { public Guid Uuid { get; set; } public Guid EmployeeUuid { get; set; } public string EmployeeName { get; set; } = ""; public int TaxYear { get; set; } public decimal TotalIncome { get; set; } public decimal TotalTax { get; set; } public DateTime GeneratedAt { get; set; } public bool IsUnverified { get; set; } }
public sealed class TaxCertificateGenerate { public Guid? EmployeeUuid { get; set; } public int TaxYear { get; set; } }

public sealed class ExpenseCategoryModel { public Guid Uuid { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string? AccountCode { get; set; } public string? AccountRole { get; set; } public string? DefaultTaxCode { get; set; } public bool RequiresReceipt { get; set; } public decimal? ReceiptThreshold { get; set; } public decimal? RatePerUnit { get; set; } public string? UnitName { get; set; } public bool IsActive { get; set; } }
public sealed class ExpenseCategoryUpsert { public string? Code { get; set; } public string? Name { get; set; } public string? AccountCode { get; set; } public string? AccountRole { get; set; } public string? DefaultTaxCode { get; set; } public bool RequiresReceipt { get; set; } public decimal? ReceiptThreshold { get; set; } public decimal? RatePerUnit { get; set; } public string? UnitName { get; set; } }

public sealed class ExpenseLineModel { public Guid Uuid { get; set; } public Guid CategoryUuid { get; set; } public string CategoryName { get; set; } = ""; public DateOnly ExpenseDate { get; set; } public string? Description { get; set; } public decimal Amount { get; set; } public decimal? Quantity { get; set; } public string? TaxCode { get; set; } public decimal TaxAmount { get; set; } public string? CostCenterCode { get; set; } public Guid? AttachmentUuid { get; set; } }

public sealed class ExpenseClaimModel
{
    public Guid Uuid { get; set; } public string ClaimNumber { get; set; } = ""; public Guid EmployeeUuid { get; set; } public string EmployeeName { get; set; } = ""; public DateOnly ClaimDate { get; set; } public string Status { get; set; } = "";
    public string PaymentMode { get; set; } = ""; public string SettlementMethod { get; set; } = ""; public string CurrencyCode { get; set; } = ""; public decimal TotalAmount { get; set; } public decimal TotalTax { get; set; } public string? Description { get; set; }
    public Guid? ApprovalUuid { get; set; } public DateTime? SubmittedAt { get; set; } public string? DecidedByName { get; set; } public DateTime? DecidedAt { get; set; } public string? DecisionNote { get; set; } public DateOnly? PaidOn { get; set; } public string? PaidInRunNumber { get; set; }
    public string PaidFromAccount { get; set; } = ""; public string? PaymentReference { get; set; } public string PostingState { get; set; } = ""; public string PaymentPostingState { get; set; } = ""; public string? PostingNote { get; set; } public List<ExpenseLineModel> Lines { get; set; } = [];
}

public sealed class ExpenseLineUpsert { public Guid CategoryUuid { get; set; } public DateOnly ExpenseDate { get; set; } public string? Description { get; set; } public decimal Amount { get; set; } public decimal? Quantity { get; set; } public string? TaxCode { get; set; } public decimal TaxAmount { get; set; } public string? CostCenterCode { get; set; } public Guid? AttachmentUuid { get; set; } }
public sealed class ExpenseClaimUpsert { public DateOnly? ClaimDate { get; set; } public string? PaymentMode { get; set; } public string? SettlementMethod { get; set; } public string? PaidFromAccount { get; set; } public string? Description { get; set; } public List<ExpenseLineUpsert> Lines { get; set; } = []; }

public sealed class RemittanceModel { public Guid Uuid { get; set; } public string RemittanceNumber { get; set; } = ""; public string RemittanceType { get; set; } = ""; public DateOnly PeriodStart { get; set; } public DateOnly PeriodEnd { get; set; } public decimal Amount { get; set; } public DateOnly PaidOn { get; set; } public string PaidFromAccount { get; set; } = ""; public string? Reference { get; set; } public string? AuthorityName { get; set; } public string? Notes { get; set; } public string PostingState { get; set; } = ""; public string? PostingNote { get; set; } }
public sealed class RemittanceCreate { public string? RemittanceType { get; set; } public DateOnly PeriodStart { get; set; } public DateOnly PeriodEnd { get; set; } public decimal Amount { get; set; } public DateOnly PaidOn { get; set; } public string? PaidFromAccount { get; set; } public string? Reference { get; set; } public string? AuthorityName { get; set; } public string? Notes { get; set; } public bool Force { get; set; } }
public sealed class RemittanceTrackerRow { public string Type { get; set; } = ""; public DateOnly PeriodStart { get; set; } public DateOnly PeriodEnd { get; set; } public decimal Calculated { get; set; } public decimal Paid { get; set; } public decimal Balance { get; set; } }
