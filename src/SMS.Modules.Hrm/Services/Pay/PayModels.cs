namespace SMS.Modules.Hrm.Models;

// API-CONTRACT §7, §9 (PAY).

public sealed class SalaryComponentModel
{
    public Guid Uuid { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsTaxable { get; set; }
    public bool InGross { get; set; }
    public string CalcMethod { get; set; } = "";
    public decimal? DefaultValue { get; set; }
    public string? PercentOf { get; set; }
    public string? Formula { get; set; }
    public string? PostingRole { get; set; }
    public string? ExpenseRole { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}

public sealed class SalaryComponentUpsert
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool IsTaxable { get; set; }
    public bool InGross { get; set; }
    public string? CalcMethod { get; set; }
    public decimal? DefaultValue { get; set; }
    public string? PercentOf { get; set; }
    public string? Formula { get; set; }
    public string? PostingRole { get; set; }
    public string? ExpenseRole { get; set; }
    public int? SortOrder { get; set; }
}

public sealed class PayActiveRequest { public bool IsActive { get; set; } }

public sealed class SalaryStructureLineModel
{
    public Guid Uuid { get; set; }
    public Guid ComponentUuid { get; set; }
    public string ComponentCode { get; set; } = "";
    public int Sequence { get; set; }
    public string? FormulaOverride { get; set; }
    public decimal? ValueOverride { get; set; }
}

public sealed class SalaryStructureModel
{
    public Guid Uuid { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public List<SalaryStructureLineModel> Lines { get; set; } = [];
}

public sealed class SalaryStructureLineUpsert
{
    public Guid ComponentUuid { get; set; }
    public int Sequence { get; set; }
    public string? FormulaOverride { get; set; }
    public decimal? ValueOverride { get; set; }
}

public sealed class SalaryStructureUpsert
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public List<SalaryStructureLineUpsert> Lines { get; set; } = [];
}

public sealed class FormulaTestRequest
{
    public string? Formula { get; set; }
    public Dictionary<string, decimal>? Variables { get; set; }
}

public sealed class FormulaTestResult
{
    public bool Valid { get; set; }
    public decimal? Result { get; set; }
    public string? Error { get; set; }
}

public sealed class SalaryAssignmentLineModel
{
    public Guid ComponentUuid { get; set; }
    public string ComponentCode { get; set; } = "";
    public decimal Amount { get; set; }
}

public sealed class SalaryAssignmentModel
{
    public Guid Uuid { get; set; }
    public Guid EmployeeUuid { get; set; }
    public string EmployeeName { get; set; } = "";
    public Guid StructureUuid { get; set; }
    public string StructureName { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string ApprovalStatus { get; set; } = "";
    public string? ChangeReason { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public List<SalaryAssignmentLineModel> Lines { get; set; } = [];
    public decimal Gross { get; set; }
}

public sealed class SalaryAssignmentLineUpsert
{
    public Guid ComponentUuid { get; set; }
    public decimal Amount { get; set; }
}

public sealed class SalaryAssignmentCreate
{
    public Guid StructureUuid { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public string? ChangeReason { get; set; }
    public List<SalaryAssignmentLineUpsert> Lines { get; set; } = [];
}

public sealed class SalaryDecisionRequest { public string? Note { get; set; } }

public sealed class BulkRevisionRequest
{
    public decimal? Percent { get; set; }
    public decimal? Amount { get; set; }
    public string? ComponentCode { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public List<Guid> EmployeeUuids { get; set; } = [];
    public string? Reason { get; set; }
}

public sealed class TaxSlabModel
{
    public decimal FromAmount { get; set; }
    public decimal? ToAmount { get; set; }
    public decimal FixedAmount { get; set; }
    public decimal RatePercent { get; set; }
}

public sealed class TaxTableModel
{
    public Guid Uuid { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool IsVerified { get; set; }
    public string? VerifiedByName { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? SourceNote { get; set; }
    public List<TaxSlabModel> Slabs { get; set; } = [];
}

public sealed class TaxTableUpsert
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? CountryCode { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string? SourceNote { get; set; }
    public List<TaxSlabModel> Slabs { get; set; } = [];
}

public sealed class StatutoryRateModel
{
    public Guid Uuid { get; set; }
    public string RateType { get; set; } = "";
    public string Name { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public decimal EmployeePercent { get; set; }
    public decimal EmployerPercent { get; set; }
    public decimal? WageCeiling { get; set; }
    public decimal? FixedBasis { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool IsVerified { get; set; }
    public string? VerifiedByName { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? SourceNote { get; set; }
}

public sealed class StatutoryRateUpsert
{
    public string? RateType { get; set; }
    public string? Name { get; set; }
    public string? CountryCode { get; set; }
    public decimal EmployeePercent { get; set; }
    public decimal EmployerPercent { get; set; }
    public decimal? WageCeiling { get; set; }
    public decimal? FixedBasis { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string? SourceNote { get; set; }
}

public sealed class VerifyRequest { public string? Note { get; set; } }

// ── §9 ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class PayrollPostingModel
{
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public DateOnly? PostingDate { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? TargetResultsJson { get; set; }
}

public sealed class PayrollRunModel
{
    public Guid Uuid { get; set; }
    public string RunNumber { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly PayDate { get; set; }
    public string PayrollGroup { get; set; } = "";
    public string Status { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public int TotalEmployees { get; set; }
    public int BlockedEmployees { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNet { get; set; }
    public decimal TotalEmployerCost { get; set; }
    public DateTime? CalculatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? FinalizedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PaymentReference { get; set; }
    public string PaidFromAccount { get; set; } = "";
    public DateOnly? PostingDate { get; set; }
    public string? PostingNote { get; set; }
    public string CostCenterMode { get; set; } = "";
    public Guid? ReversalOfRunUuid { get; set; }
    public string? ReversalReason { get; set; }
    /// <summary>Set on the answer to reverse: the new Draft that takes the place of this run.</summary>
    public Guid? CorrectionRunUuid { get; set; }
    /// <summary>The calculation went to a background job (more than 200 employees): poll progress. The controller answers 202.</summary>
    public bool CalculationQueued { get; set; }
    /// <summary>D-ATT-3: payable lines paid in full with no attendance on file.</summary>
    public int UntrackedAttendanceEmployees { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<PayrollPostingModel> Postings { get; set; } = [];
    public List<string> AllowedActions { get; set; } = [];
}

public sealed class PayrollLineComponentModel
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? Basis { get; set; }
}

public sealed class PayrollRunLineModel
{
    public Guid Uuid { get; set; }
    public Guid EmployeeUuid { get; set; }
    public string EmployeeNumber { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string? DepartmentName { get; set; }
    public string? DesignationName { get; set; }
    public string? CostCenterCode { get; set; }
    public decimal WorkingDays { get; set; }
    public decimal PaidDays { get; set; }
    public decimal WorkedDays { get; set; }
    public decimal Gross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal Net { get; set; }
    public decimal TotalEmployerCost { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
    public string PaymentMethod { get; set; } = "";
    /// <summary>D-ATT-3: paid in full because attendance has no record of this person in the period. Highlight it.</summary>
    public bool AttendanceUntracked { get; set; }
    public List<PayrollLineComponentModel> Components { get; set; } = [];
}

public sealed class PayrollRunCreate
{
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly PayDate { get; set; }
    public string? PayrollGroup { get; set; }
}

public sealed class PayrollApproveRequest { public bool ExcludeBlocked { get; set; } }

public sealed class PayrollPayRequest
{
    public DateOnly PaidOn { get; set; }
    public string? Reference { get; set; }
    public string? PaidFromAccount { get; set; }
}

public sealed class PayrollReverseRequest { public string? Reason { get; set; } }

public sealed class PayrollProgressModel
{
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public string? Error { get; set; }
}

public sealed class PayrollJournalLineModel
{
    public string AccountRole { get; set; } = "";
    public string? AccountCode { get; set; }
    public string? CostCenter { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
}

public sealed class PayrollJournalModel
{
    public string RunNumber { get; set; } = "";
    public DateOnly? PostingDate { get; set; }
    public string CurrencyCode { get; set; } = "";
    public string? Description { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public List<PayrollJournalLineModel> Lines { get; set; } = [];
}

public sealed class PayrollReconciliationModel
{
    public string RunNumber { get; set; } = "";
    public decimal RunGross { get; set; }
    public decimal RunDeductions { get; set; }
    public decimal RunNet { get; set; }
    public decimal RunEmployerCost { get; set; }
    public decimal JournalDebit { get; set; }
    public decimal JournalCredit { get; set; }
    public bool Balanced { get; set; }
    public List<PayrollPostingModel> Postings { get; set; } = [];
}

public sealed class PayrollAdjustmentModel
{
    public Guid Uuid { get; set; }
    public Guid EmployeeUuid { get; set; }
    public string EmployeeName { get; set; } = "";
    public Guid? RunUuid { get; set; }
    public string ComponentCode { get; set; } = "";
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public Guid? AppliedInRunUuid { get; set; }
}

public sealed class PayrollAdjustmentCreate
{
    public Guid EmployeeUuid { get; set; }
    public Guid? RunUuid { get; set; }
    public string? ComponentCode { get; set; }
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
}

public sealed class PayslipSummaryModel
{
    public string RunNumber { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal Net { get; set; }
    public Guid PayslipUuid { get; set; }
}

public sealed class PayslipBulkResult
{
    public int Generated { get; set; }
    public int AlreadyGenerated { get; set; }
}

public sealed class PayrollHistoryItem
{
    public Guid RunUuid { get; set; }
    /// <summary>With RunUuid, opens GET payroll-runs/{run}/lines/{line}/payslip, which generates a missing payslip.</summary>
    public Guid LineUuid { get; set; }
    /// <summary>FE-5: null until the payslip has been generated. Opens GET payslips/{uuid}/pdf.</summary>
    public Guid? PayslipUuid { get; set; }
    public string RunNumber { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Status { get; set; } = "";
    public decimal Gross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal Net { get; set; }
    public bool IsBlocked { get; set; }
}

public sealed class PayFile
{
    public byte[] Content { get; set; } = [];
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
}
