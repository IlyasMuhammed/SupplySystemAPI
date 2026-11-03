using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Hrm.Domain;

namespace SMS.Modules.Hrm.Data.Maps;

// A49 — money decimal(18,2), rates decimal(9,4), dates date; CHECK constraints list HrmCodes vocabularies. FKs stay inside
// schema hrm (task 073) and are Restrict except children that cannot outlive their parent. Index names follow the register.

internal static class HrmColumn
{
    public const string Money = "decimal(18,2)", Rate = "decimal(9,4)", Days = "decimal(9,2)", Date = "date";

    public static string In(string column, IEnumerable<string> values) =>
        $"[{column}] IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";

    /// <summary>Key, uuid and the audit columns shared by every table.</summary>
    public static void Base<T>(EntityTypeBuilder<T> b) where T : HrmEntity
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.Property(x => x.ModifiedByName).HasMaxLength(200);
    }

    public static void Versioned<T>(EntityTypeBuilder<T> b) where T : HrmVersionedEntity
    {
        Base(b);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal sealed class HrmSettingsMap : IEntityTypeConfiguration<HrmSettings>
{
    public void Configure(EntityTypeBuilder<HrmSettings> b)
    {
        b.ToTable("HrmSettings", t =>
        {
            t.HasCheckConstraint("CK_HrmSettings_Mode", HrmColumn.In(nameof(HrmSettings.PayrollCostCenterMode), PayrollCostCenterModes.All));
            t.HasCheckConstraint("CK_HrmSettings_PostDate", HrmColumn.In(nameof(HrmSettings.PostDatePolicy), PayrollPostDatePolicies.All));
            t.HasCheckConstraint("CK_HrmSettings_Settlement", HrmColumn.In(nameof(HrmSettings.DefaultExpenseSettlement), ExpenseSettlementMethods.All));
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.PayrollCostCenterMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.PostDatePolicy).HasMaxLength(30).IsRequired();
        b.Property(x => x.EmployeeNumberPrefix).HasMaxLength(10).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.DefaultExpenseSettlement).HasMaxLength(20).IsRequired();
        b.Property(x => x.DefaultReceiptThreshold).HasColumnType(HrmColumn.Money);
        b.HasIndex(x => x.OrganizationId).IsUnique().HasDatabaseName("UQ_HrmSettings_Org");
        b.HasOne(x => x.PayrollCostCenter).WithMany().HasForeignKey(x => x.PayrollCostCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CostCenterMap : IEntityTypeConfiguration<CostCenter>
{
    public void Configure(EntityTypeBuilder<CostCenter> b)
    {
        b.ToTable("CostCenters");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_CostCenter_OrgCode");
        b.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BranchMap : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b)
    {
        b.ToTable("Branches");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.City).HasMaxLength(100);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_Branch_OrgCode");
    }
}

internal sealed class DepartmentMap : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("Departments", t => t.HasCheckConstraint("CK_Departments_NotOwnParent", "[ParentId] IS NULL OR [ParentId] <> [Id]"));
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.CostCenterCode).HasMaxLength(20);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_Dept_OrgCode");
        b.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.HeadEmployee).WithMany().HasForeignKey(x => x.HeadEmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CostCenter).WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DesignationMap : IEntityTypeConfiguration<Designation>
{
    public void Configure(EntityTypeBuilder<Designation> b)
    {
        b.ToTable("Designations");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_Designation_OrgCode");
    }
}

internal sealed class GradeMap : IEntityTypeConfiguration<Grade>
{
    public void Configure(EntityTypeBuilder<Grade> b)
    {
        b.ToTable("Grades", t => t.HasCheckConstraint("CK_Grades_MinMax", "[MinSalary] IS NULL OR [MaxSalary] IS NULL OR [MinSalary] <= [MaxSalary]"));
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.MinSalary).HasColumnType(HrmColumn.Money);
        b.Property(x => x.MaxSalary).HasColumnType(HrmColumn.Money);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_Grade_OrgCode");
    }
}

internal sealed class EmployeeMap : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("Employees", t =>
        {
            t.HasCheckConstraint("CK_Employees_Status", HrmColumn.In(nameof(Employee.Status), EmployeeStatuses.All));
            t.HasCheckConstraint("CK_Employees_Gender", $"[Gender] IS NULL OR {HrmColumn.In(nameof(Employee.Gender), Genders.All)}");
            t.HasCheckConstraint("CK_Employees_Marital", $"[MaritalStatus] IS NULL OR {HrmColumn.In(nameof(Employee.MaritalStatus), MaritalStatuses.All)}");
            t.HasCheckConstraint("CK_Employees_Departure", $"[DepartureReason] IS NULL OR {HrmColumn.In(nameof(Employee.DepartureReason), DepartureReasons.All)}");
        });
        HrmColumn.Versioned(b);
        b.Ignore(x => x.FullName);
        b.Property(x => x.EmployeeNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.FirstName).HasMaxLength(60).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(60).IsRequired();
        b.Property(x => x.FatherOrHusbandName).HasMaxLength(100);
        b.Property(x => x.DateOfBirth).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Gender).HasMaxLength(10);
        b.Property(x => x.MaritalStatus).HasMaxLength(20);
        b.Property(x => x.Nationality).HasMaxLength(3);
        b.Property(x => x.CnicEncrypted).HasMaxLength(500);
        b.Property(x => x.CnicHash).HasMaxLength(64);
        b.Property(x => x.CnicLastDigit).HasMaxLength(1);
        b.Property(x => x.NtnEncrypted).HasMaxLength(500);
        b.Property(x => x.NtnHash).HasMaxLength(64);
        b.Property(x => x.NtnLast4).HasMaxLength(4);
        b.Property(x => x.PersonalEmail).HasMaxLength(200);
        b.Property(x => x.WorkEmail).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Mobile).HasMaxLength(30);
        b.Property(x => x.CurrentAddressJson).HasMaxLength(2000);
        b.Property(x => x.PermanentAddressJson).HasMaxLength(2000);
        b.Property(x => x.EmergencyContactsJson).HasMaxLength(2000);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.JoinDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.LeaveDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.DepartureReason).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeNumber }).IsUnique().HasDatabaseName("UQ_Emp_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.CnicHash }).IsUnique().HasFilter("[CnicHash] IS NOT NULL AND [IsDeleted] = 0").HasDatabaseName("UQ_Emp_OrgCnicHash");
        b.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique().HasFilter("[UserId] IS NOT NULL AND [IsDeleted] = 0").HasDatabaseName("IX_Emp_UserId");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("IX_Emp_OrgStatus");
    }
}

internal sealed class EmploymentRecordMap : IEntityTypeConfiguration<EmploymentRecord>
{
    public void Configure(EntityTypeBuilder<EmploymentRecord> b)
    {
        b.ToTable("EmploymentRecords", t =>
        {
            t.HasCheckConstraint("CK_Employment_Type", HrmColumn.In(nameof(EmploymentRecord.EmploymentType), EmploymentTypes.All));
            t.HasCheckConstraint("CK_Employment_Reason", HrmColumn.In(nameof(EmploymentRecord.ChangeReason), EmploymentChangeReasons.All));
            t.HasCheckConstraint("CK_Employment_Dates", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
            t.HasCheckConstraint("CK_Employment_Notice", "[NoticePeriodDays] >= 0");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.EffectiveFrom).HasColumnType(HrmColumn.Date);
        b.Property(x => x.EffectiveTo).HasColumnType(HrmColumn.Date);
        b.Property(x => x.EmploymentType).HasMaxLength(20).IsRequired();
        b.Property(x => x.ProbationEndDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.ConfirmationDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.ContractEndDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.CostCenterCode).HasMaxLength(20);
        b.Property(x => x.PayrollGroup).HasMaxLength(30);
        b.Property(x => x.ChangeReason).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom }).IsUnique().HasDatabaseName("UQ_Employment_EmployeeFrom");
        // At most one open record per employee: the database half of "no overlap" (the service checks date ranges).
        b.HasIndex(x => x.EmployeeId).IsUnique().HasFilter("[EffectiveTo] IS NULL").HasDatabaseName("UQ_Employment_OneOpen");
        b.HasIndex(x => new { x.OrganizationId, x.DepartmentId }).HasDatabaseName("IX_Emp_Department");
        b.HasIndex(x => new { x.OrganizationId, x.BranchId }).HasDatabaseName("IX_Emp_Branch");
        b.HasIndex(x => new { x.OrganizationId, x.ManagerEmployeeId }).HasDatabaseName("IX_Emp_Manager");
        b.HasOne(x => x.Employee).WithMany(x => x.EmploymentRecords).HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Designation).WithMany().HasForeignKey(x => x.DesignationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Grade).WithMany().HasForeignKey(x => x.GradeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ManagerEmployee).WithMany().HasForeignKey(x => x.ManagerEmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CostCenter).WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmploymentCostSplitMap : IEntityTypeConfiguration<EmploymentCostSplit>
{
    public void Configure(EntityTypeBuilder<EmploymentCostSplit> b)
    {
        b.ToTable("EmploymentCostSplits", t => t.HasCheckConstraint("CK_CostSplit_Percent", "[Percent] > 0 AND [Percent] <= 100"));
        HrmColumn.Base(b);
        b.Property(x => x.Percent).HasColumnType(HrmColumn.Rate);
        b.HasIndex(x => new { x.EmploymentRecordId, x.CostCenterId }).IsUnique().HasDatabaseName("UQ_CostSplit_RecordCenter");
        b.HasOne(x => x.EmploymentRecord).WithMany(x => x.CostSplits).HasForeignKey(x => x.EmploymentRecordId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.CostCenter).WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeBankAccountMap : IEntityTypeConfiguration<EmployeeBankAccount>
{
    public void Configure(EntityTypeBuilder<EmployeeBankAccount> b)
    {
        b.ToTable("EmployeeBankAccounts", t => t.HasCheckConstraint("CK_BankAccount_Method", HrmColumn.In(nameof(EmployeeBankAccount.PaymentMethod), BankPaymentMethods.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.BankName).HasMaxLength(100).IsRequired();
        b.Property(x => x.BranchName).HasMaxLength(100);
        b.Property(x => x.AccountTitle).HasMaxLength(200);
        b.Property(x => x.AccountNumberEncrypted).HasMaxLength(500);
        b.Property(x => x.AccountNumberLast4).HasMaxLength(4);
        b.Property(x => x.IbanEncrypted).HasMaxLength(500);
        b.Property(x => x.IbanLast4).HasMaxLength(4);
        b.Property(x => x.PaymentMethod).HasMaxLength(10).IsRequired();
        b.HasIndex(x => x.EmployeeId).IsUnique().HasFilter("[IsPrimary] = 1 AND [IsActive] = 1").HasDatabaseName("UQ_BankAccount_OnePrimary");
        b.HasOne(x => x.Employee).WithMany(x => x.BankAccounts).HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeDocumentMap : IEntityTypeConfiguration<EmployeeDocument>
{
    public void Configure(EntityTypeBuilder<EmployeeDocument> b)
    {
        b.ToTable("EmployeeDocuments", t => t.HasCheckConstraint("CK_EmployeeDoc_Type", HrmColumn.In(nameof(EmployeeDocument.DocumentType), EmployeeDocumentTypes.All)));
        HrmColumn.Base(b);
        b.Property(x => x.DocumentType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.ExpiryDate).HasColumnType(HrmColumn.Date);
        b.HasOne(x => x.Employee).WithMany(x => x.Documents).HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class HrUserScopeMap : IEntityTypeConfiguration<HrUserScope>
{
    public void Configure(EntityTypeBuilder<HrUserScope> b)
    {
        b.ToTable("HrUserScopes", t => t.HasCheckConstraint("CK_HrUserScopes_Type",
            "([ScopeType] = 'Branch' AND [BranchId] IS NOT NULL AND [DepartmentId] IS NULL) OR ([ScopeType] = 'Department' AND [DepartmentId] IS NOT NULL AND [BranchId] IS NULL)"));
        HrmColumn.Base(b);
        b.Property(x => x.ScopeType).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.UserId }).HasDatabaseName("IX_HrUserScopes_User");
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SensitiveAccessLogMap : IEntityTypeConfiguration<SensitiveAccessLog>
{
    public void Configure(EntityTypeBuilder<SensitiveAccessLog> b)
    {
        b.ToTable("SensitiveAccessLogs");
        HrmColumn.Base(b);
        b.Property(x => x.Field).HasMaxLength(30).IsRequired();
        b.Property(x => x.Purpose).HasMaxLength(100).IsRequired();
        b.Property(x => x.UserName).HasMaxLength(200);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.CreatedAt }).HasDatabaseName("IX_SensitiveAccess_Employee");
    }
}

// ── H2 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class ShiftMap : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.ToTable("Shifts");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_Shift_OrgCode");
    }
}

internal sealed class HolidayMap : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> b)
    {
        b.ToTable("Holidays");
        HrmColumn.Versioned(b);
        b.Property(x => x.Date).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(3).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Date, x.Name, x.BranchId }).IsUnique().HasDatabaseName("UQ_Holiday_OrgDateName");
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttendanceEventMap : IEntityTypeConfiguration<AttendanceEvent>
{
    public void Configure(EntityTypeBuilder<AttendanceEvent> b)
    {
        b.ToTable("AttendanceEvents", t =>
        {
            t.HasCheckConstraint("CK_AttendanceEvent_Type", HrmColumn.In(nameof(AttendanceEvent.EventType), AttendanceEventTypes.All));
            t.HasCheckConstraint("CK_AttendanceEvent_Source", HrmColumn.In(nameof(AttendanceEvent.Source), AttendanceSources.All));
        });
        HrmColumn.Base(b);
        b.Property(x => x.EventType).HasMaxLength(10).IsRequired();
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.ExternalEventId).HasMaxLength(100);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.Source, x.ExternalEventId }).IsUnique().HasFilter("[ExternalEventId] IS NOT NULL").HasDatabaseName("UQ_AttendanceEvent_External");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.EventTimeUtc }).HasDatabaseName("IX_AttendanceEvent_EmployeeTime");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttendanceDayMap : IEntityTypeConfiguration<AttendanceDay>
{
    public void Configure(EntityTypeBuilder<AttendanceDay> b)
    {
        b.ToTable("AttendanceDays", t => t.HasCheckConstraint("CK_AttendanceDay_Status", HrmColumn.In(nameof(AttendanceDay.Status), AttendanceDayStatuses.All)));
        HrmColumn.Base(b);
        b.Property(x => x.Date).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.OverrideNote).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Date }).IsUnique().HasDatabaseName("UQ_AttendanceDay_EmployeeDate");
        b.HasIndex(x => new { x.OrganizationId, x.Date }).HasDatabaseName("IX_AttendanceDay_OrgDate");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OvertimeRecordMap : IEntityTypeConfiguration<OvertimeRecord>
{
    public void Configure(EntityTypeBuilder<OvertimeRecord> b)
    {
        b.ToTable("OvertimeRecords", t =>
        {
            t.HasCheckConstraint("CK_Overtime_Status", HrmColumn.In(nameof(OvertimeRecord.Status), ApprovalStatuses.All));
            t.HasCheckConstraint("CK_Overtime_Minutes", "[Minutes] > 0");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.Date).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Multiplier).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.DecidedByName).HasMaxLength(200);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Date }).IsUnique().HasDatabaseName("UQ_Overtime_EmployeeDate");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PaidInRun).WithMany().HasForeignKey(x => x.PaidInRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LeaveTypeMap : IEntityTypeConfiguration<LeaveType>
{
    public void Configure(EntityTypeBuilder<LeaveType> b)
    {
        b.ToTable("LeaveTypes");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.CarryForwardCap).HasColumnType(HrmColumn.Days);
        b.Property(x => x.GenderRestriction).HasMaxLength(10);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_LeaveType_OrgCode");
    }
}

internal sealed class LeavePolicyMap : IEntityTypeConfiguration<LeavePolicy>
{
    public void Configure(EntityTypeBuilder<LeavePolicy> b)
    {
        b.ToTable("LeavePolicies", t => t.HasCheckConstraint("CK_LeavePolicy_Accrual", HrmColumn.In(nameof(LeavePolicy.AccrualMethod), LeaveAccrualMethods.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.Entitlement).HasColumnType(HrmColumn.Days);
        b.Property(x => x.AccrualMethod).HasMaxLength(20).IsRequired();
        b.Property(x => x.EffectiveFrom).HasColumnType(HrmColumn.Date);
        b.Property(x => x.EffectiveTo).HasColumnType(HrmColumn.Date);
        b.HasOne(x => x.LeaveType).WithMany(x => x.Policies).HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Grade).WithMany().HasForeignKey(x => x.GradeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LeaveLedgerEntryMap : IEntityTypeConfiguration<LeaveLedgerEntry>
{
    public void Configure(EntityTypeBuilder<LeaveLedgerEntry> b)
    {
        b.ToTable("LeaveLedgerEntries", t => t.HasCheckConstraint("CK_LeaveLedger_Reason", HrmColumn.In(nameof(LeaveLedgerEntry.Reason), LeaveLedgerReasons.All)));
        HrmColumn.Base(b);
        b.Property(x => x.Delta).HasColumnType(HrmColumn.Days);
        b.Property(x => x.Reason).HasMaxLength(30).IsRequired();
        b.Property(x => x.Date).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.LeaveTypeId, x.Year }).HasDatabaseName("IX_LeaveLedger_Balance");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.LeaveType).WithMany().HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LeaveRequestMap : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> b)
    {
        b.ToTable("LeaveRequests", t =>
        {
            t.HasCheckConstraint("CK_LeaveRequest_Status", HrmColumn.In(nameof(LeaveRequest.Status), LeaveRequestStatuses.All));
            t.HasCheckConstraint("CK_LeaveRequest_Dates", "[ToDate] >= [FromDate]");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.RequestNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.FromDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.ToDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Days).HasColumnType(HrmColumn.Days);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.DecidedByName).HasMaxLength(200);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.RequestNumber }).IsUnique().HasDatabaseName("UQ_LeaveRequest_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.FromDate }).HasDatabaseName("IX_LeaveRequest_EmployeeFrom");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("IX_LeaveRequest_Status");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.LeaveType).WithMany().HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
    }
}
