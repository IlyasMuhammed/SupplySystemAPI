namespace SMS.Modules.Hrm.Models;

// API-CONTRACT §2 — organization structure and settings. uuid only; camelCase JSON.

public sealed class BranchModel
{
    public Guid    Uuid          { get; set; }
    public string  Code          { get; set; } = "";
    public string  Name          { get; set; } = "";
    public string? Address       { get; set; }
    public string? City          { get; set; }
    public Guid?   WarehouseUuid { get; set; }
    public bool    IsActive      { get; set; }
}

public sealed class BranchUpsert
{
    public string? Code          { get; set; }
    public string? Name          { get; set; }
    public string? Address       { get; set; }
    public string? City          { get; set; }
    public Guid?   WarehouseUuid { get; set; }
}

public sealed class SetActiveRequest { public bool IsActive { get; set; } }

public sealed class CostCenterModel
{
    public Guid    Uuid       { get; set; }
    public string  Code       { get; set; } = "";
    public string  Name       { get; set; } = "";
    public Guid?   ParentUuid { get; set; }
    public Guid?   BranchUuid { get; set; }
    public bool    IsActive   { get; set; }
    /// <summary>True for the organization's payroll cost center (it cannot be deactivated).</summary>
    public bool    IsPayrollCostCenter { get; set; }
}

public sealed class CostCenterUpsert
{
    public string? Code       { get; set; }
    public string? Name       { get; set; }
    public Guid?   ParentUuid { get; set; }
    public Guid?   BranchUuid { get; set; }
}

public class DepartmentModel
{
    public Guid    Uuid           { get; set; }
    public string  Code           { get; set; } = "";
    public string  Name           { get; set; } = "";
    public Guid?   ParentUuid     { get; set; }
    public Guid?   HeadEmployeeUuid { get; set; }
    public string? HeadEmployeeName { get; set; }
    public Guid?   BranchUuid     { get; set; }
    public string? BranchName     { get; set; }
    public Guid?   CostCenterUuid { get; set; }
    public string? CostCenterCode { get; set; }
    public bool    IsActive       { get; set; }
    public int     EmployeeCount  { get; set; }
}

public sealed class DepartmentTreeNode : DepartmentModel
{
    public List<DepartmentTreeNode> Children { get; set; } = [];
}

public sealed class DepartmentUpsert
{
    public string? Code             { get; set; }
    public string? Name             { get; set; }
    public Guid?   ParentUuid       { get; set; }
    public Guid?   HeadEmployeeUuid { get; set; }
    public Guid?   BranchUuid       { get; set; }
    public Guid?   CostCenterUuid   { get; set; }
}

public sealed class DesignationModel
{
    public Guid   Uuid     { get; set; }
    public string Code     { get; set; } = "";
    public string Name     { get; set; } = "";
    public int    Level    { get; set; }
    public bool   IsActive { get; set; }
}

public sealed class DesignationUpsert
{
    public string? Code  { get; set; }
    public string? Name  { get; set; }
    public int     Level { get; set; }
}

public sealed class GradeModel
{
    public Guid     Uuid      { get; set; }
    public string   Code      { get; set; } = "";
    public string   Name      { get; set; } = "";
    /// <summary>S2: null unless the caller holds HRM_SALARY_VIEW; <see cref="SalaryMasked"/> says which.</summary>
    public decimal? MinSalary { get; set; }
    public decimal? MaxSalary { get; set; }
    public bool     SalaryMasked { get; set; }
    public bool     IsActive  { get; set; }
}

public sealed class GradeUpsert
{
    public string?  Code      { get; set; }
    public string?  Name      { get; set; }
    public decimal? MinSalary { get; set; }
    public decimal? MaxSalary { get; set; }
}

public sealed class HrmSettingsModel
{
    public Guid?    PayrollCostCenterUuid { get; set; }
    public string?  PayrollCostCenterCode { get; set; }
    public string?  PayrollCostCenterName { get; set; }
    public string   PayrollCostCenterMode { get; set; } = "";
    public string   PostDatePolicy        { get; set; } = "";
    public string   EmployeeNumberPrefix  { get; set; } = "";
    public string   CountryCode           { get; set; } = "";
    public int?     WorkingDaysDivisor    { get; set; }
    public string   DefaultExpenseSettlement { get; set; } = "";
    public decimal? DefaultReceiptThreshold  { get; set; }
    public DateTime? EnablementCompletedAt { get; set; }
}

public sealed class HrmSettingsUpdate
{
    public Guid?    PayrollCostCenterUuid { get; set; }
    public string?  PayrollCostCenterMode { get; set; }
    public string?  PostDatePolicy        { get; set; }
    public string?  EmployeeNumberPrefix  { get; set; }
    public int?     WorkingDaysDivisor    { get; set; }
    public string?  DefaultExpenseSettlement { get; set; }
    public decimal? DefaultReceiptThreshold  { get; set; }
}

public sealed class HrUserScopeModel
{
    public Guid    Uuid           { get; set; }
    public int     UserId         { get; set; }
    public string  ScopeType      { get; set; } = "";
    public Guid?   BranchUuid     { get; set; }
    public string? BranchName     { get; set; }
    public Guid?   DepartmentUuid { get; set; }
    public string? DepartmentName { get; set; }
}

/// <summary>FE-4 — the HR scope screen's user picker.</summary>
public sealed class HrScopeUserModel
{
    public int    UserId { get; set; }
    public string Name   { get; set; } = "";
    public string Email  { get; set; } = "";
}

public sealed class HrUserScopeCreate
{
    public int   UserId         { get; set; }
    public Guid? BranchUuid     { get; set; }
    public Guid? DepartmentUuid { get; set; }
}
