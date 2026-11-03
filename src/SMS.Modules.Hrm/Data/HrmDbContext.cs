using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Data;

/// <summary>
/// A49 — schema <c>hrm</c>, all phases H1..H6E. The tenant filter applies to every entity, but it is OFF for super admins and
/// jobs without a captured organization, so services always filter on the organization explicitly too (§3.3). CORE alone
/// owns this context, the entities, the maps and the migrations; other agents file docs/hrm/SCHEMA-REQUESTS.md.
/// </summary>
internal sealed class HrmDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "hrm";

    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public HrmDbContext(DbContextOptions<HrmDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    // H1
    internal DbSet<HrmSettings>         Settings            => Set<HrmSettings>();
    internal DbSet<CostCenter>          CostCenters         => Set<CostCenter>();
    internal DbSet<Branch>              Branches            => Set<Branch>();
    internal DbSet<Department>          Departments         => Set<Department>();
    internal DbSet<Designation>         Designations        => Set<Designation>();
    internal DbSet<Grade>               Grades              => Set<Grade>();
    internal DbSet<Employee>            Employees           => Set<Employee>();
    internal DbSet<EmploymentRecord>    EmploymentRecords   => Set<EmploymentRecord>();
    internal DbSet<EmploymentCostSplit> EmploymentCostSplits => Set<EmploymentCostSplit>();
    internal DbSet<EmployeeBankAccount> EmployeeBankAccounts => Set<EmployeeBankAccount>();
    internal DbSet<EmployeeDocument>    EmployeeDocuments   => Set<EmployeeDocument>();
    internal DbSet<HrUserScope>         UserScopes          => Set<HrUserScope>();
    internal DbSet<SensitiveAccessLog>  SensitiveAccessLogs => Set<SensitiveAccessLog>();
    // H2
    internal DbSet<Shift>               Shifts              => Set<Shift>();
    internal DbSet<Holiday>             Holidays            => Set<Holiday>();
    internal DbSet<AttendanceEvent>     AttendanceEvents    => Set<AttendanceEvent>();
    internal DbSet<AttendanceDay>       AttendanceDays      => Set<AttendanceDay>();
    internal DbSet<OvertimeRecord>      OvertimeRecords     => Set<OvertimeRecord>();
    internal DbSet<LeaveType>           LeaveTypes          => Set<LeaveType>();
    internal DbSet<LeavePolicy>         LeavePolicies       => Set<LeavePolicy>();
    internal DbSet<LeaveLedgerEntry>    LeaveLedgerEntries  => Set<LeaveLedgerEntry>();
    internal DbSet<LeaveRequest>        LeaveRequests       => Set<LeaveRequest>();
    // H3
    internal DbSet<SalaryComponent>     SalaryComponents    => Set<SalaryComponent>();
    internal DbSet<SalaryStructure>     SalaryStructures    => Set<SalaryStructure>();
    internal DbSet<SalaryStructureLine> SalaryStructureLines => Set<SalaryStructureLine>();
    internal DbSet<SalaryAssignment>    SalaryAssignments   => Set<SalaryAssignment>();
    internal DbSet<SalaryAssignmentLine> SalaryAssignmentLines => Set<SalaryAssignmentLine>();
    internal DbSet<TaxSlabTable>        TaxSlabTables       => Set<TaxSlabTable>();
    internal DbSet<TaxSlab>             TaxSlabs            => Set<TaxSlab>();
    internal DbSet<StatutoryRate>       StatutoryRates      => Set<StatutoryRate>();
    // H4
    internal DbSet<Vacancy>             Vacancies           => Set<Vacancy>();
    internal DbSet<Candidate>           Candidates          => Set<Candidate>();
    internal DbSet<JobApplication>      JobApplications     => Set<JobApplication>();
    internal DbSet<Interview>           Interviews          => Set<Interview>();
    internal DbSet<JobOffer>            JobOffers           => Set<JobOffer>();
    internal DbSet<ReviewCycle>         ReviewCycles        => Set<ReviewCycle>();
    internal DbSet<PerformanceReview>   PerformanceReviews  => Set<PerformanceReview>();
    internal DbSet<Goal>                Goals               => Set<Goal>();
    internal DbSet<Course>              Courses             => Set<Course>();
    internal DbSet<CourseEnrollment>    CourseEnrollments   => Set<CourseEnrollment>();
    // H5
    internal DbSet<PayrollRun>          PayrollRuns         => Set<PayrollRun>();
    internal DbSet<PayrollRunLine>      PayrollRunLines     => Set<PayrollRunLine>();
    internal DbSet<PayrollRunLineComponent> PayrollRunLineComponents => Set<PayrollRunLineComponent>();
    internal DbSet<Payslip>             Payslips            => Set<Payslip>();
    internal DbSet<PayrollAdjustment>   PayrollAdjustments  => Set<PayrollAdjustment>();
    internal DbSet<PayrollPosting>      PayrollPostings     => Set<PayrollPosting>();
    // H6
    internal DbSet<Loan>                Loans               => Set<Loan>();
    internal DbSet<LoanInstallment>     LoanInstallments    => Set<LoanInstallment>();
    internal DbSet<FinalSettlement>     FinalSettlements    => Set<FinalSettlement>();
    internal DbSet<FinalSettlementLine> FinalSettlementLines => Set<FinalSettlementLine>();
    internal DbSet<IncrementProposal>   IncrementProposals  => Set<IncrementProposal>();
    internal DbSet<LetterTemplate>      LetterTemplates     => Set<LetterTemplate>();
    internal DbSet<IssuedLetter>        IssuedLetters       => Set<IssuedLetter>();
    internal DbSet<TaxCertificate>      TaxCertificates     => Set<TaxCertificate>();
    // H6E
    internal DbSet<ExpenseCategory>     ExpenseCategories   => Set<ExpenseCategory>();
    internal DbSet<ExpenseClaim>        ExpenseClaims       => Set<ExpenseClaim>();
    internal DbSet<ExpenseLine>         ExpenseLines        => Set<ExpenseLine>();
    internal DbSet<StatutoryRemittance> StatutoryRemittances => Set<StatutoryRemittance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HrmDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        modelBuilder.ApplyTenantIndexes();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        this.StampTenantScopedEntities(_tenantContext);
        this.StampModifiedAt();
        Refuse<SensitiveAccessLog>("The sensitive-data access log is append-only.", EntityState.Modified, EntityState.Deleted);
        Refuse<AttendanceEvent>("A raw attendance event is immutable.", EntityState.Modified, EntityState.Deleted);
        Refuse<LeaveLedgerEntry>("The leave ledger is append-only: reverse an entry with a new one.", EntityState.Modified, EntityState.Deleted);
        Refuse<Payslip>("A payslip is immutable.", EntityState.Modified, EntityState.Deleted);
        // A recalculation may delete and rewrite the snapshot of a run that is not finalized; a row is never edited.
        Refuse<PayrollRunLineComponent>("A payroll component snapshot is immutable.", EntityState.Modified);
    }

    private void Refuse<T>(string message, params EntityState[] states) where T : class
    {
        if (ChangeTracker.Entries<T>().Any(e => states.Contains(e.State))) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction inside the retrying execution strategy (a user-started transaction outside
    /// it throws under EnableRetryOnFailure — A48 D-CORE-14). The work may run again after a transient failure: it must not
    /// assume a first attempt. In-memory tests have no transactions, so the work simply runs. Joins the caller's transaction.
    /// </summary>
    public async Task InTransactionAsync(Func<Task> work, CancellationToken ct = default)
    {
        if (!Database.IsRelational() || Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }
        var strategy = Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Database.BeginTransactionAsync(ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }
}
