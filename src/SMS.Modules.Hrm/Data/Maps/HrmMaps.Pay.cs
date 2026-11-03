using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Hrm.Domain;

namespace SMS.Modules.Hrm.Data.Maps;

// ── H3 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class SalaryComponentMap : IEntityTypeConfiguration<SalaryComponent>
{
    public void Configure(EntityTypeBuilder<SalaryComponent> b)
    {
        b.ToTable("SalaryComponents", t =>
        {
            t.HasCheckConstraint("CK_SalaryComponent_Type", HrmColumn.In(nameof(SalaryComponent.Type), SalaryComponentTypes.All));
            t.HasCheckConstraint("CK_SalaryComponent_Calc", HrmColumn.In(nameof(SalaryComponent.CalcMethod), SalaryCalcMethods.All));
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Type).HasMaxLength(30).IsRequired();
        b.Property(x => x.CalcMethod).HasMaxLength(10).IsRequired();
        b.Property(x => x.DefaultValue).HasColumnType(HrmColumn.Money);
        b.Property(x => x.PercentOf).HasMaxLength(20);
        b.Property(x => x.Formula).HasMaxLength(1000);
        b.Property(x => x.PostingRole).HasMaxLength(50);
        b.Property(x => x.ExpenseRole).HasMaxLength(50);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_SalaryComponent_OrgCode");
    }
}

internal sealed class SalaryStructureMap : IEntityTypeConfiguration<SalaryStructure>
{
    public void Configure(EntityTypeBuilder<SalaryStructure> b)
    {
        b.ToTable("SalaryStructures");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_SalaryStructure_OrgCode");
    }
}

internal sealed class SalaryStructureLineMap : IEntityTypeConfiguration<SalaryStructureLine>
{
    public void Configure(EntityTypeBuilder<SalaryStructureLine> b)
    {
        b.ToTable("SalaryStructureLines");
        HrmColumn.Base(b);
        b.Property(x => x.FormulaOverride).HasMaxLength(1000);
        b.Property(x => x.ValueOverride).HasColumnType(HrmColumn.Money);
        b.HasIndex(x => new { x.StructureId, x.ComponentId }).IsUnique().HasDatabaseName("UQ_StructureLine_Component");
        b.HasOne(x => x.Structure).WithMany(x => x.Lines).HasForeignKey(x => x.StructureId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Component).WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalaryAssignmentMap : IEntityTypeConfiguration<SalaryAssignment>
{
    public void Configure(EntityTypeBuilder<SalaryAssignment> b)
    {
        b.ToTable("SalaryAssignments", t =>
        {
            t.HasCheckConstraint("CK_SalaryAssignment_Status", HrmColumn.In(nameof(SalaryAssignment.ApprovalStatus), SalaryAssignmentStatuses.All));
            t.HasCheckConstraint("CK_SalaryAssignment_Dates", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.EffectiveFrom).HasColumnType(HrmColumn.Date);
        b.Property(x => x.EffectiveTo).HasColumnType(HrmColumn.Date);
        b.Property(x => x.ApprovalStatus).HasMaxLength(20).IsRequired();
        b.Property(x => x.ChangeReason).HasMaxLength(500);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.EffectiveFrom }).HasDatabaseName("IX_SalaryAssignment_EmployeeFrom");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Structure).WithMany().HasForeignKey(x => x.StructureId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalaryAssignmentLineMap : IEntityTypeConfiguration<SalaryAssignmentLine>
{
    public void Configure(EntityTypeBuilder<SalaryAssignmentLine> b)
    {
        b.ToTable("SalaryAssignmentLines");
        HrmColumn.Base(b);
        b.Property(x => x.Amount).HasColumnType(HrmColumn.Money);
        b.HasIndex(x => new { x.AssignmentId, x.ComponentId }).IsUnique().HasDatabaseName("UQ_AssignmentLine_Component");
        b.HasOne(x => x.Assignment).WithMany(x => x.Lines).HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Component).WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TaxSlabTableMap : IEntityTypeConfiguration<TaxSlabTable>
{
    public void Configure(EntityTypeBuilder<TaxSlabTable> b)
    {
        b.ToTable("TaxSlabTables");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.EffectiveFrom).HasColumnType(HrmColumn.Date);
        b.Property(x => x.EffectiveTo).HasColumnType(HrmColumn.Date);
        b.Property(x => x.VerifiedByName).HasMaxLength(200);
        b.Property(x => x.SourceNote).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_TaxSlabTable_OrgCode");
    }
}

internal sealed class TaxSlabMap : IEntityTypeConfiguration<TaxSlab>
{
    public void Configure(EntityTypeBuilder<TaxSlab> b)
    {
        b.ToTable("TaxSlabs", t => t.HasCheckConstraint("CK_TaxSlab_Range", "[ToAmount] IS NULL OR [ToAmount] > [FromAmount]"));
        HrmColumn.Base(b);
        b.Property(x => x.FromAmount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.ToAmount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.FixedAmount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.RatePercent).HasColumnType(HrmColumn.Rate);
        b.HasIndex(x => new { x.TableId, x.FromAmount }).IsUnique().HasDatabaseName("UQ_TaxSlab_TableFrom");
        b.HasOne(x => x.Table).WithMany(x => x.Slabs).HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StatutoryRateMap : IEntityTypeConfiguration<StatutoryRate>
{
    public void Configure(EntityTypeBuilder<StatutoryRate> b)
    {
        b.ToTable("StatutoryRates", t => t.HasCheckConstraint("CK_StatutoryRate_Type", HrmColumn.In(nameof(StatutoryRate.RateType), StatutoryRateTypes.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.RateType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.EmployeePercent).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.EmployerPercent).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.WageCeiling).HasColumnType(HrmColumn.Money);
        b.Property(x => x.FixedBasis).HasColumnType(HrmColumn.Money);
        b.Property(x => x.EffectiveFrom).HasColumnType(HrmColumn.Date);
        b.Property(x => x.EffectiveTo).HasColumnType(HrmColumn.Date);
        b.Property(x => x.VerifiedByName).HasMaxLength(200);
        b.Property(x => x.SourceNote).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.RateType, x.EffectiveFrom }).IsUnique().HasDatabaseName("UQ_StatutoryRate_TypeFrom");
    }
}

// ── H4 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class VacancyMap : IEntityTypeConfiguration<Vacancy>
{
    public void Configure(EntityTypeBuilder<Vacancy> b)
    {
        b.ToTable("Vacancies", t =>
        {
            t.HasCheckConstraint("CK_Vacancy_Status", HrmColumn.In(nameof(Vacancy.Status), VacancyStatuses.All));
            t.HasCheckConstraint("CK_Vacancy_Positions", "[Positions] > 0");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.VacancyNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.ClosingDate).HasColumnType(HrmColumn.Date);
        b.HasIndex(x => new { x.OrganizationId, x.VacancyNumber }).IsUnique().HasDatabaseName("UQ_Vacancy_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("IX_Vacancy_Status");
        b.HasOne(x => x.Designation).WithMany().HasForeignKey(x => x.DesignationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CandidateMap : IEntityTypeConfiguration<Candidate>
{
    public void Configure(EntityTypeBuilder<Candidate> b)
    {
        b.ToTable("Candidates");
        HrmColumn.Versioned(b);
        b.Property(x => x.FirstName).HasMaxLength(60).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(60).IsRequired();
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Source).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasIndex(x => new { x.OrganizationId, x.Email }).HasDatabaseName("IX_Candidate_Email");
        b.HasIndex(x => new { x.OrganizationId, x.Phone }).HasDatabaseName("IX_Candidate_Phone");
    }
}

internal sealed class JobApplicationMap : IEntityTypeConfiguration<JobApplication>
{
    public void Configure(EntityTypeBuilder<JobApplication> b)
    {
        b.ToTable("JobApplications", t => t.HasCheckConstraint("CK_JobApplication_Stage", HrmColumn.In(nameof(JobApplication.Stage), ApplicationStages.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.Stage).HasMaxLength(20).IsRequired();
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.HasIndex(x => new { x.CandidateId, x.VacancyId }).IsUnique().HasDatabaseName("UQ_JobApplication_CandidateVacancy");
        b.HasIndex(x => new { x.OrganizationId, x.VacancyId, x.Stage }).HasDatabaseName("IX_JobApplication_Board");
        b.HasOne(x => x.Candidate).WithMany().HasForeignKey(x => x.CandidateId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Vacancy).WithMany().HasForeignKey(x => x.VacancyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.HiredEmployee).WithMany().HasForeignKey(x => x.HiredEmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InterviewMap : IEntityTypeConfiguration<Interview>
{
    public void Configure(EntityTypeBuilder<Interview> b)
    {
        b.ToTable("Interviews", t => t.HasCheckConstraint("CK_Interview_Result", HrmColumn.In(nameof(Interview.Result), InterviewResults.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.Feedback).HasMaxLength(4000);
        b.Property(x => x.Score).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.Result).HasMaxLength(20).IsRequired();
        b.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Interviewer).WithMany().HasForeignKey(x => x.InterviewerEmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JobOfferMap : IEntityTypeConfiguration<JobOffer>
{
    public void Configure(EntityTypeBuilder<JobOffer> b)
    {
        b.ToTable("JobOffers", t => t.HasCheckConstraint("CK_JobOffer_Status", HrmColumn.In(nameof(JobOffer.Status), OfferStatuses.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.OfferedSalary).HasColumnType(HrmColumn.Money);
        b.Property(x => x.ProposedJoinDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReviewCycleMap : IEntityTypeConfiguration<ReviewCycle>
{
    public void Configure(EntityTypeBuilder<ReviewCycle> b)
    {
        b.ToTable("ReviewCycles", t =>
        {
            t.HasCheckConstraint("CK_ReviewCycle_Period", HrmColumn.In(nameof(ReviewCycle.Period), ReviewPeriods.All));
            t.HasCheckConstraint("CK_ReviewCycle_Status", HrmColumn.In(nameof(ReviewCycle.Status), ReviewCycleStatuses.All));
            t.HasCheckConstraint("CK_ReviewCycle_Dates", "[EndDate] >= [StartDate]");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Period).HasMaxLength(20).IsRequired();
        b.Property(x => x.StartDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.EndDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
    }
}

internal sealed class PerformanceReviewMap : IEntityTypeConfiguration<PerformanceReview>
{
    public void Configure(EntityTypeBuilder<PerformanceReview> b)
    {
        b.ToTable("Reviews", t => t.HasCheckConstraint("CK_Review_Status", HrmColumn.In(nameof(PerformanceReview.Status), ReviewStatuses.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.OverallRating).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ManagerComments).HasMaxLength(4000);
        b.Property(x => x.EmployeeComments).HasMaxLength(4000);
        b.HasIndex(x => new { x.EmployeeId, x.ReviewCycleId }).IsUnique().HasDatabaseName("UQ_Review_EmployeeCycle");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.ReviewCycleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewerEmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GoalMap : IEntityTypeConfiguration<Goal>
{
    public void Configure(EntityTypeBuilder<Goal> b)
    {
        b.ToTable("Goals", t =>
        {
            t.HasCheckConstraint("CK_Goal_Status", HrmColumn.In(nameof(Goal.Status), GoalStatuses.All));
            t.HasCheckConstraint("CK_Goal_Weight", "[Weight] >= 0");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Weight).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.SelfRating).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.ManagerRating).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.ReviewCycleId }).HasDatabaseName("IX_Goal_EmployeeCycle");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.ReviewCycleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CourseMap : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> b)
    {
        b.ToTable("Courses");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Provider).HasMaxLength(200);
        b.Property(x => x.DurationHours).HasColumnType(HrmColumn.Days);
        b.Property(x => x.Cost).HasColumnType(HrmColumn.Money);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_Course_OrgCode");
    }
}

internal sealed class CourseEnrollmentMap : IEntityTypeConfiguration<CourseEnrollment>
{
    public void Configure(EntityTypeBuilder<CourseEnrollment> b)
    {
        b.ToTable("CourseEnrollments", t => t.HasCheckConstraint("CK_Enrollment_Status", HrmColumn.In(nameof(CourseEnrollment.Status), EnrollmentStatuses.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.EnrolledDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.CompletedDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.EmployeeId, x.CourseId, x.EnrolledDate }).IsUnique().HasDatabaseName("UQ_Enrollment_EmployeeCourseDate");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
    }
}

// ── H5 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class PayrollRunMap : IEntityTypeConfiguration<PayrollRun>
{
    public void Configure(EntityTypeBuilder<PayrollRun> b)
    {
        b.ToTable("PayrollRuns", t =>
        {
            t.HasCheckConstraint("CK_PayrollRun_Status", HrmColumn.In(nameof(PayrollRun.Status), PayrollRunStatuses.All));
            t.HasCheckConstraint("CK_PayrollRun_Dates", "[PeriodEnd] >= [PeriodStart]");
            t.HasCheckConstraint("CK_PayrollRun_Mode", HrmColumn.In(nameof(PayrollRun.CostCenterMode), PayrollCostCenterModes.All));
            t.HasCheckConstraint("CK_PayrollRun_PaidFrom", HrmColumn.In(nameof(PayrollRun.PaidFromAccount), HrmPaymentAccounts.All));
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.RunNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.PeriodStart).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PeriodEnd).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PayDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PayrollGroup).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.TotalGross).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalDeductions).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalNet).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalEmployerCost).HasColumnType(HrmColumn.Money);
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.PaidFromAccount).HasMaxLength(10).IsRequired();
        b.Property(x => x.PostingDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PostingNote).HasMaxLength(1000);
        b.Property(x => x.CostCenterMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.ReversalReason).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.RunNumber }).IsUnique().HasDatabaseName("UQ_PayrollRun_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.PeriodStart, x.PeriodEnd, x.PayrollGroup }).IsUnique()
            .HasFilter("[Status] <> 'Cancelled' AND [Status] <> 'Reversed'").HasDatabaseName("UQ_PayrollRun_PeriodGroup");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("IX_PayrollRun_Status");
        b.HasOne(x => x.ReversalOfRun).WithMany().HasForeignKey(x => x.ReversalOfRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollRunLineMap : IEntityTypeConfiguration<PayrollRunLine>
{
    public void Configure(EntityTypeBuilder<PayrollRunLine> b)
    {
        b.ToTable("PayrollRunLines", t => t.HasCheckConstraint("CK_PayrollLine_Method", HrmColumn.In(nameof(PayrollRunLine.PaymentMethod), BankPaymentMethods.All)));
        HrmColumn.Base(b);
        b.Property(x => x.WorkingDays).HasColumnType(HrmColumn.Days);
        b.Property(x => x.PaidDays).HasColumnType(HrmColumn.Days);
        b.Property(x => x.WorkedDays).HasColumnType(HrmColumn.Days);
        b.Property(x => x.Gross).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalDeductions).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Net).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalEmployerCost).HasColumnType(HrmColumn.Money);
        b.Property(x => x.BlockReason).HasMaxLength(500);
        b.Property(x => x.DepartmentName).HasMaxLength(100);
        b.Property(x => x.DesignationName).HasMaxLength(100);
        b.Property(x => x.CostCenterCode).HasMaxLength(20);
        b.Property(x => x.PaymentMethod).HasMaxLength(10).IsRequired();
        b.HasIndex(x => new { x.RunId, x.EmployeeId }).IsUnique().HasDatabaseName("UQ_PayrollLine_RunEmployee");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId }).HasDatabaseName("IX_PayrollLine_Employee");
        b.HasOne(x => x.Run).WithMany(x => x.Lines).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollRunLineComponentMap : IEntityTypeConfiguration<PayrollRunLineComponent>
{
    public void Configure(EntityTypeBuilder<PayrollRunLineComponent> b)
    {
        b.ToTable("PayrollRunLineComponents", t => t.HasCheckConstraint("CK_PayrollComponent_Type", HrmColumn.In(nameof(PayrollRunLineComponent.Type), SalaryComponentTypes.All)));
        HrmColumn.Base(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Type).HasMaxLength(30).IsRequired();
        b.Property(x => x.Amount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Basis).HasColumnType(HrmColumn.Money);
        b.Property(x => x.PostingRole).HasMaxLength(50);
        b.Property(x => x.ExpenseRole).HasMaxLength(50);
        b.Property(x => x.CostCenterCode).HasMaxLength(20);
        b.HasIndex(x => new { x.LineId, x.Code, x.CostCenterCode }).HasDatabaseName("IX_PayrollComponent_LineCode");
        b.HasOne(x => x.Line).WithMany(x => x.Components).HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PayslipMap : IEntityTypeConfiguration<Payslip>
{
    public void Configure(EntityTypeBuilder<Payslip> b)
    {
        b.ToTable("Payslips");
        HrmColumn.Base(b);
        b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.RunLineId).IsUnique().HasDatabaseName("UQ_Payslip_RunLine");
        b.HasOne(x => x.RunLine).WithOne(x => x.Payslip).HasForeignKey<Payslip>(x => x.RunLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollAdjustmentMap : IEntityTypeConfiguration<PayrollAdjustment>
{
    public void Configure(EntityTypeBuilder<PayrollAdjustment> b)
    {
        b.ToTable("PayrollAdjustments");
        HrmColumn.Versioned(b);
        b.Property(x => x.ComponentCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.Amount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.AppliedInRunId }).HasDatabaseName("IX_PayrollAdjustment_Pending");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AppliedInRun).WithMany().HasForeignKey(x => x.AppliedInRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ExpenseClaim).WithMany().HasForeignKey(x => x.ExpenseClaimId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollPostingMap : IEntityTypeConfiguration<PayrollPosting>
{
    public void Configure(EntityTypeBuilder<PayrollPosting> b)
    {
        b.ToTable("PayrollPostings", t =>
        {
            t.HasCheckConstraint("CK_PayrollPosting_Kind", HrmColumn.In(nameof(PayrollPosting.Kind), PayrollPostingKinds.All));
            t.HasCheckConstraint("CK_PayrollPosting_Status", HrmColumn.In(nameof(PayrollPosting.Status), PayrollPostingStatuses.All));
        });
        HrmColumn.Base(b);
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.PostingDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.TotalDebit).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalCredit).HasColumnType(HrmColumn.Money);
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UQ_PayrollPosting_Key");
        b.HasOne(x => x.Run).WithMany(x => x.Postings).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
    }
}

// ── H6 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class LoanMap : IEntityTypeConfiguration<Loan>
{
    public void Configure(EntityTypeBuilder<Loan> b)
    {
        b.ToTable("Loans", t =>
        {
            t.HasCheckConstraint("CK_Loan_Type", HrmColumn.In(nameof(Loan.Type), LoanTypes.All));
            t.HasCheckConstraint("CK_Loan_Status", HrmColumn.In(nameof(Loan.Status), LoanStatuses.All));
            t.HasCheckConstraint("CK_Loan_Amounts", "[Principal] > 0 AND [InstallmentCount] > 0");
            t.HasCheckConstraint("CK_Loan_Posting", HrmColumn.In(nameof(Loan.PostingState), HrmPostingStates.All));
            t.HasCheckConstraint("CK_Loan_PaidFrom", HrmColumn.In(nameof(Loan.PaidFromAccount), HrmPaymentAccounts.All));
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.LoanNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Type).HasMaxLength(20).IsRequired();
        b.Property(x => x.Principal).HasColumnType(HrmColumn.Money);
        b.Property(x => x.MonthlyAmount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.StartMonth).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.Property(x => x.DisbursedOn).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PaidFromAccount).HasMaxLength(10).IsRequired();
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.PostingState).HasMaxLength(10).IsRequired();
        b.Property(x => x.PostingNote).HasMaxLength(1000);
        b.HasIndex(x => new { x.OrganizationId, x.LoanNumber }).IsUnique().HasDatabaseName("UQ_Loan_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Status }).HasDatabaseName("IX_Loan_EmployeeStatus");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LoanInstallmentMap : IEntityTypeConfiguration<LoanInstallment>
{
    public void Configure(EntityTypeBuilder<LoanInstallment> b)
    {
        b.ToTable("LoanInstallments", t => t.HasCheckConstraint("CK_LoanInstallment_Status", HrmColumn.In(nameof(LoanInstallment.Status), LoanInstallmentStatuses.All)));
        HrmColumn.Base(b);
        b.Property(x => x.DueMonth).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Amount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex(x => new { x.LoanId, x.Sequence }).IsUnique().HasDatabaseName("UQ_LoanInstallment_Seq");
        b.HasIndex(x => new { x.OrganizationId, x.DueMonth, x.Status }).HasDatabaseName("IX_LoanInstallment_Due");
        b.HasOne(x => x.Loan).WithMany(x => x.Installments).HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinalSettlementMap : IEntityTypeConfiguration<FinalSettlement>
{
    public void Configure(EntityTypeBuilder<FinalSettlement> b)
    {
        b.ToTable("FinalSettlements", t =>
        {
            t.HasCheckConstraint("CK_Settlement_Status", HrmColumn.In(nameof(FinalSettlement.Status), SettlementStatuses.All));
            t.HasCheckConstraint("CK_Settlement_Posting", HrmColumn.In(nameof(FinalSettlement.PostingState), HrmPostingStates.All));
            t.HasCheckConstraint("CK_Settlement_PaidFrom", HrmColumn.In(nameof(FinalSettlement.PaidFromAccount), HrmPaymentAccounts.All));
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.SettlementNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.LastWorkingDay).HasColumnType(HrmColumn.Date);
        b.Property(x => x.ServiceYears).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.LastDrawnSalary).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalEarnings).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalDeductions).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Net).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.PaidOn).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PaidFromAccount).HasMaxLength(10).IsRequired();
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.PostingState).HasMaxLength(10).IsRequired();
        b.Property(x => x.PostingNote).HasMaxLength(1000);
        b.HasIndex(x => new { x.OrganizationId, x.SettlementNumber }).IsUnique().HasDatabaseName("UQ_Settlement_OrgNumber");
        b.HasIndex(x => x.EmployeeId).IsUnique().HasFilter("[Status] <> 'Cancelled'").HasDatabaseName("UQ_Settlement_OnePerEmployee");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinalSettlementLineMap : IEntityTypeConfiguration<FinalSettlementLine>
{
    public void Configure(EntityTypeBuilder<FinalSettlementLine> b)
    {
        b.ToTable("FinalSettlementLines", t => t.HasCheckConstraint("CK_SettlementLine_Type", HrmColumn.In(nameof(FinalSettlementLine.Type), SalaryComponentTypes.All)));
        HrmColumn.Base(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200).IsRequired();
        b.Property(x => x.Type).HasMaxLength(30).IsRequired();
        b.Property(x => x.Amount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Basis).HasColumnType(HrmColumn.Money);
        b.Property(x => x.PostingRole).HasMaxLength(50);
        b.HasOne(x => x.Settlement).WithMany(x => x.Lines).HasForeignKey(x => x.SettlementId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IncrementProposalMap : IEntityTypeConfiguration<IncrementProposal>
{
    public void Configure(EntityTypeBuilder<IncrementProposal> b)
    {
        b.ToTable("IncrementProposals", t =>
        {
            t.HasCheckConstraint("CK_Increment_Status", HrmColumn.In(nameof(IncrementProposal.Status), IncrementStatuses.All));
            t.HasCheckConstraint("CK_Increment_Value", "[NewBasic] IS NOT NULL OR [Percent] IS NOT NULL");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.NewBasic).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Percent).HasColumnType(HrmColumn.Rate);
        b.Property(x => x.EffectiveDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.DecidedByName).HasMaxLength(200);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Status }).HasDatabaseName("IX_Increment_EmployeeStatus");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Review).WithMany().HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AppliedAssignment).WithMany().HasForeignKey(x => x.AppliedAssignmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LetterTemplateMap : IEntityTypeConfiguration<LetterTemplate>
{
    public void Configure(EntityTypeBuilder<LetterTemplate> b)
    {
        b.ToTable("LetterTemplates", t => t.HasCheckConstraint("CK_LetterTemplate_Kind", HrmColumn.In(nameof(LetterTemplate.Kind), LetterTemplateCodes.All)));
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(30).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_LetterTemplate_OrgCode");
    }
}

internal sealed class IssuedLetterMap : IEntityTypeConfiguration<IssuedLetter>
{
    public void Configure(EntityTypeBuilder<IssuedLetter> b)
    {
        b.ToTable("IssuedLetters", t => t.HasCheckConstraint("CK_IssuedLetter_Kind", HrmColumn.In(nameof(IssuedLetter.Kind), LetterTemplateCodes.All)));
        HrmColumn.Base(b);
        b.Property(x => x.LetterNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(30).IsRequired();
        b.Property(x => x.IssuedByName).HasMaxLength(200);
        b.HasIndex(x => new { x.OrganizationId, x.LetterNumber }).IsUnique().HasDatabaseName("UQ_IssuedLetter_OrgNumber");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TaxCertificateMap : IEntityTypeConfiguration<TaxCertificate>
{
    public void Configure(EntityTypeBuilder<TaxCertificate> b)
    {
        b.ToTable("TaxCertificates");
        HrmColumn.Base(b);
        b.Property(x => x.TotalIncome).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalTax).HasColumnType(HrmColumn.Money);
        b.HasIndex(x => new { x.EmployeeId, x.TaxYear }).IsUnique().HasDatabaseName("UQ_TaxCertificate_EmployeeYear");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

// ── H6E ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class ExpenseCategoryMap : IEntityTypeConfiguration<ExpenseCategory>
{
    public void Configure(EntityTypeBuilder<ExpenseCategory> b)
    {
        b.ToTable("ExpenseCategories");
        HrmColumn.Versioned(b);
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.AccountCode).HasMaxLength(20);
        b.Property(x => x.AccountRole).HasMaxLength(50);
        b.Property(x => x.DefaultTaxCode).HasMaxLength(20);
        b.Property(x => x.ReceiptThreshold).HasColumnType(HrmColumn.Money);
        b.Property(x => x.RatePerUnit).HasColumnType(HrmColumn.Money);
        b.Property(x => x.UnitName).HasMaxLength(20);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_EC_OrgCode");
    }
}

internal sealed class ExpenseClaimMap : IEntityTypeConfiguration<ExpenseClaim>
{
    public void Configure(EntityTypeBuilder<ExpenseClaim> b)
    {
        b.ToTable("ExpenseClaims", t =>
        {
            t.HasCheckConstraint("CK_ExpenseClaim_Status", HrmColumn.In(nameof(ExpenseClaim.Status), ExpenseClaimStatuses.All));
            t.HasCheckConstraint("CK_ExpenseClaim_Mode", HrmColumn.In(nameof(ExpenseClaim.PaymentMode), ExpensePaymentModes.All));
            t.HasCheckConstraint("CK_ExpenseClaim_Settlement", HrmColumn.In(nameof(ExpenseClaim.SettlementMethod), ExpenseSettlementMethods.All));
            t.HasCheckConstraint("CK_ExpenseClaim_Posting", HrmColumn.In(nameof(ExpenseClaim.PostingState), HrmPostingStates.All));
            t.HasCheckConstraint("CK_ExpenseClaim_PayPosting", HrmColumn.In(nameof(ExpenseClaim.PaymentPostingState), HrmPostingStates.All));
            t.HasCheckConstraint("CK_ExpenseClaim_PaidFrom", HrmColumn.In(nameof(ExpenseClaim.PaidFromAccount), HrmPaymentAccounts.All));
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.ClaimNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.ClaimDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.PaymentMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.SettlementMethod).HasMaxLength(20).IsRequired();
        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.TotalAmount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.TotalTax).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.DecidedByName).HasMaxLength(200);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.Property(x => x.PaidOn).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PaidFromAccount).HasMaxLength(10).IsRequired();
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.PostingState).HasMaxLength(10).IsRequired();
        b.Property(x => x.PaymentPostingState).HasMaxLength(10).IsRequired();
        b.Property(x => x.PostingNote).HasMaxLength(1000);
        b.HasIndex(x => new { x.OrganizationId, x.ClaimNumber }).IsUnique().HasDatabaseName("UQ_ExpenseClaim_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Status }).HasDatabaseName("IX_ExpenseClaim_EmployeeStatus");
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PaidInRun).WithMany().HasForeignKey(x => x.PaidInRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExpenseLineMap : IEntityTypeConfiguration<ExpenseLine>
{
    public void Configure(EntityTypeBuilder<ExpenseLine> b)
    {
        b.ToTable("ExpenseLines", t => t.HasCheckConstraint("CK_ExpenseLine_Amount", "[Amount] >= 0 AND [TaxAmount] >= 0"));
        HrmColumn.Base(b);
        b.Property(x => x.ExpenseDate).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Amount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.Quantity).HasColumnType(HrmColumn.Days);
        b.Property(x => x.TaxCode).HasMaxLength(20);
        b.Property(x => x.TaxAmount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.CostCenterCode).HasMaxLength(20);
        b.HasOne(x => x.Claim).WithMany(x => x.Lines).HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StatutoryRemittanceMap : IEntityTypeConfiguration<StatutoryRemittance>
{
    public void Configure(EntityTypeBuilder<StatutoryRemittance> b)
    {
        b.ToTable("StatutoryRemittances", t =>
        {
            t.HasCheckConstraint("CK_Remittance_Type", HrmColumn.In(nameof(StatutoryRemittance.RemittanceType), RemittanceTypes.All));
            t.HasCheckConstraint("CK_Remittance_Posting", HrmColumn.In(nameof(StatutoryRemittance.PostingState), HrmPostingStates.All));
            t.HasCheckConstraint("CK_Remittance_PaidFrom", HrmColumn.In(nameof(StatutoryRemittance.PaidFromAccount), HrmPaymentAccounts.All));
            t.HasCheckConstraint("CK_Remittance_Amount", "[Amount] > 0");
        });
        HrmColumn.Versioned(b);
        b.Property(x => x.RemittanceNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.RemittanceType).HasMaxLength(20).IsRequired();
        b.Property(x => x.PeriodStart).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PeriodEnd).HasColumnType(HrmColumn.Date);
        b.Property(x => x.Amount).HasColumnType(HrmColumn.Money);
        b.Property(x => x.PaidOn).HasColumnType(HrmColumn.Date);
        b.Property(x => x.PaidFromAccount).HasMaxLength(10).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.AuthorityName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.PostingState).HasMaxLength(10).IsRequired();
        b.Property(x => x.PostingNote).HasMaxLength(1000);
        b.HasIndex(x => new { x.OrganizationId, x.RemittanceNumber }).IsUnique().HasDatabaseName("UQ_Remittance_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.RemittanceType, x.PeriodStart }).HasDatabaseName("IX_Remittance_TypePeriod");
    }
}
