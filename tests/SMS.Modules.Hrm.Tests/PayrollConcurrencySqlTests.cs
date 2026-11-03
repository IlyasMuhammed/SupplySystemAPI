using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Life;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>
/// What in-memory tests cannot show: RowVersion, the org lock and the retrying strategy on a throwaway LocalDB database, with one DbContext per request as in production.
/// Concurrent finalize, calculate and adjust must post once and deduct once.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class PayrollConcurrencySqlTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A49Pay_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();
    private readonly PayGateway _gateway = new();
    private readonly HrmCaller _admin;

    public PayrollConcurrencySqlTests() => _admin = new HrmCaller(_org, 900, P.HrmCodes, "HR Admin");

    private HrmDbContext NewContext() => new(
        new DbContextOptionsBuilder<HrmDbContext>().UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3)).Options,
        new StaticTenantContext { OrganizationId = _org });

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    /// <summary>One request: its own DbContext and services.</summary>
    private sealed class Scope : IAsyncDisposable
    {
        public HrmDbContext Db { get; }
        public IPayrollRunService Runs { get; }
        public IPayrollAdjustmentService Adjustments { get; }

        public Scope(PayrollConcurrencySqlTests t)
        {
            Db = t.NewContext();
            var encryption = new AesEncryptionService(Options.Create(new AppSettings { AesEncryptionKey = HrmHarness.AesKey }));
            var sensitive = new HrmSensitiveData(encryption, Options.Create(new AppSettings { AesEncryptionKey = HrmHarness.AesKey }));
            var seeder = new HrmSeeder(Db);
            var scope = new HrmScopeService(Db);
            var directory = new HrmEmployeeDirectory(Db, sensitive, seeder);
            var providers = new List<IPayrollInputProvider> { new PayrollAdjustmentInputProvider(Db), new LoanPayrollInputProvider(new LoanLedger(Db)), new ExpenseReimbursementInputProvider(Db) };
            var attendance = new NullAttendancePayInput();
            var engine = new PayrollCalculationEngine(Db, directory, new SalaryFormulaEngine(), attendance, providers, new PayrollProgressTracker());
            Runs = new PayrollRunService(Db, scope, directory, engine, new PayrollPostingBuilder(Db), t._gateway, new PayReadiness(), providers, attendance, new PayrollProgressTracker(), seeder);
            Adjustments = new PayrollAdjustmentService(Db, scope);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<(PayrollRunModel Run, Guid Employee)> ApprovedRunAsync()
    {
        await using var db = NewContext();
        var seeder = new HrmSeeder(db);
        await seeder.EnsureAsync(_org);
        var center = new CostCenter { OrganizationId = _org, Code = "OPS", Name = "Ops" };
        var branch = new Branch { OrganizationId = _org, Code = "KHI", Name = "Karachi" };
        var dept = new Department { OrganizationId = _org, Code = "OPS", Name = "Ops" };
        var designation = new Designation { OrganizationId = _org, Code = "CLK", Name = "Clerk", Level = 1 };
        db.AddRange(center, branch, dept, designation);
        await db.SaveChangesAsync();
        var settings = await db.Settings.IgnoreQueryFilters().FirstAsync(s => s.OrganizationId == _org);
        settings.PayrollCostCenterId = center.Id;
        var table = new TaxSlabTable { OrganizationId = _org, Code = "T", Name = "T", EffectiveFrom = new DateOnly(2026, 7, 1), EffectiveTo = new DateOnly(2027, 6, 30), IsVerified = true };
        table.Slabs.Add(new TaxSlab { OrganizationId = _org, FromAmount = 0m, RatePercent = 5m });
        db.TaxSlabTables.Add(table);
        foreach (var rate in await db.StatutoryRates.IgnoreQueryFilters().Where(r => r.OrganizationId == _org).ToListAsync()) rate.IsVerified = true;
        var employee = new Employee { OrganizationId = _org, EmployeeNumber = "E-1", FirstName = "Ali", LastName = "Khan", Status = EmployeeStatuses.Active, JoinDate = new DateOnly(2026, 1, 1) };
        employee.EmploymentRecords.Add(new EmploymentRecord { OrganizationId = _org, EffectiveFrom = new DateOnly(2026, 1, 1), DepartmentId = dept.Id, DesignationId = designation.Id, BranchId = branch.Id, EmploymentType = EmploymentTypes.Permanent, ChangeReason = EmploymentChangeReasons.Hire });
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeBankAccounts.Add(new EmployeeBankAccount { OrganizationId = _org, EmployeeId = employee.Id, BankName = "HBL", AccountNumberEncrypted = "x", IsPrimary = true });
        var structure = await db.SalaryStructures.IgnoreQueryFilters().Include(s => s.Lines).ThenInclude(l => l.Component).FirstAsync(s => s.OrganizationId == _org);
        var assignment = new SalaryAssignment { OrganizationId = _org, EmployeeId = employee.Id, StructureId = structure.Id, EffectiveFrom = new DateOnly(2026, 1, 1), ApprovalStatus = SalaryAssignmentStatuses.Approved };
        assignment.Lines.Add(new SalaryAssignmentLine { OrganizationId = _org, ComponentId = structure.Lines.First(l => l.Component!.Code == "BASIC").ComponentId, Amount = 100_000m });
        db.SalaryAssignments.Add(assignment);
        await db.SaveChangesAsync();

        await using var s = new Scope(this);
        var run = await s.Runs.CreateAsync(_admin, new PayrollRunCreate { PeriodStart = new DateOnly(2026, 10, 1), PeriodEnd = new DateOnly(2026, 10, 31), PayDate = new DateOnly(2026, 11, 1) });
        await s.Runs.CalculateAsync(_admin, run.Uuid);
        await s.Runs.ReviewAsync(_admin, run.Uuid);
        return (await s.Runs.ApproveAsync(_admin, run.Uuid, false), employee.Uuid);
    }

    private static async Task<(int Ok, List<Exception> Failed)> RaceAsync(int count, Func<int, Task> work)
    {
        var failed = new List<Exception>();
        var ok = 0;
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await start.Task;
            try
            {
                await work(i);
                Interlocked.Increment(ref ok);
            }
            catch (Exception ex)
            {
                lock (failed) failed.Add(ex);
            }
        }).ToList();
        start.SetResult();
        await Task.WhenAll(tasks);
        return (ok, failed);
    }

    [Fact]
    public async Task Concurrent_finalize_posts_once_and_consumes_once()
    {
        _gateway.PostsToGl();
        var (run, employee) = await ApprovedRunAsync();
        // An adjustment from before the run would have been in the calculation; recalculation is covered elsewhere. Here the run's own posting is the point.
        var (ok, failed) = await RaceAsync(6, async _ =>
        {
            await using var s = new Scope(this);
            await s.Runs.FinalizeAsync(_admin, run.Uuid);
        });

        ok.Should().Be(1);
        failed.Should().HaveCount(5).And.OnlyContain(e => e is ConflictException);
        _gateway.Posted.Where(t => t.DocumentType == FinDocType.PayrollRun).Should().ContainSingle();
        await using var db = NewContext();
        (await db.PayrollPostings.IgnoreQueryFilters().CountAsync(p => p.Run!.Uuid == run.Uuid)).Should().Be(1);
        (await db.PayrollRuns.IgnoreQueryFilters().SingleAsync(r => r.Uuid == run.Uuid)).Status.Should().Be("Posted");
    }

    [Fact]
    public async Task Concurrent_pay_pays_once_and_concurrent_reverse_voids_once()
    {
        _gateway.PostsToGl();
        var (run, _) = await ApprovedRunAsync();
        await using (var s = new Scope(this)) await s.Runs.FinalizeAsync(_admin, run.Uuid);

        var (paidOk, paidFailed) = await RaceAsync(4, async _ =>
        {
            await using var s = new Scope(this);
            await s.Runs.PayAsync(_admin, run.Uuid, new PayrollPayRequest { PaidOn = new DateOnly(2026, 11, 2), Reference = "BANK" });
        });
        paidOk.Should().Be(1, string.Join(" | ", paidFailed.Select(f => f.GetType().Name + ":" + f.Message)));
        paidFailed.Where(e => e is not ConflictException).Should().BeEmpty();
        _gateway.Posted.Where(t => t.DocumentType == FinDocType.PayrollPayment).Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_reverse_voids_once_and_leaves_one_correction()
    {
        _gateway.PostsToGl();
        var (run, _) = await ApprovedRunAsync();
        await using (var s = new Scope(this)) await s.Runs.FinalizeAsync(_admin, run.Uuid);
        var (ok, failed) = await RaceAsync(4, async _ =>
        {
            await using var s = new Scope(this);
            await s.Runs.ReverseAsync(_admin, run.Uuid, "Redo");
        });
        ok.Should().Be(1);
        failed.Where(e => e is not ConflictException).Should().BeEmpty();
        _gateway.Voids.Should().ContainSingle();
        await using var db = NewContext();
        (await db.PayrollRuns.IgnoreQueryFilters().CountAsync(r => r.OrganizationId == _org && r.Status == "Draft")).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_calculations_leave_one_clean_set_of_lines()
    {
        var (run, employee) = await ApprovedRunAsync();
        await using (var s = new Scope(this)) await s.Runs.ReopenAsync(_admin, run.Uuid);
        var (ok, failed) = await RaceAsync(5, async _ =>
        {
            await using var s = new Scope(this);
            await s.Runs.CalculateAsync(_admin, run.Uuid);
        });
        ok.Should().BeGreaterThan(0);
        failed.Where(e => e is not ConflictException).Should().BeEmpty(string.Join(" | ", failed.Select(f => f.GetType().Name + ":" + f.Message)));
        await using var db = NewContext();
        (await db.PayrollRunLines.IgnoreQueryFilters().CountAsync(l => l.Run!.Uuid == run.Uuid)).Should().Be(1);
        var r = await db.PayrollRuns.IgnoreQueryFilters().SingleAsync(x => x.Uuid == run.Uuid);
        r.Status.Should().Be("Calculated");
        r.TotalGross.Should().Be(100_000m);
    }

    [Fact]
    public async Task Calculate_racing_finalize_cannot_touch_a_finalized_run()
    {
        _gateway.PostsToGl();
        var (run, _) = await ApprovedRunAsync();
        var (_, failed) = await RaceAsync(4, async i =>
        {
            await using var s = new Scope(this);
            if (i == 0) await s.Runs.FinalizeAsync(_admin, run.Uuid);
            else await s.Runs.CalculateAsync(_admin, run.Uuid);
        });
        failed.Where(e => e is not ConflictException).Should().BeEmpty();
        _gateway.Posted.Should().ContainSingle();
        await using var db = NewContext();
        var lines = await db.PayrollRunLines.IgnoreQueryFilters().Where(l => l.Run!.Uuid == run.Uuid).CountAsync();
        lines.Should().Be(1);
    }

    [Fact]
    public async Task Two_runs_cannot_both_consume_one_adjustment_and_the_retrying_transaction_works()
    {
        _gateway.PostsToGl();
        var (run, employee) = await ApprovedRunAsync();

        // A second group's run on the same person is what could take the same "next run" adjustment.
        await using (var s = new Scope(this))
        {
            await s.Runs.ReopenAsync(_admin, run.Uuid);
            await s.Adjustments.CreateAsync(_admin, new PayrollAdjustmentCreate { EmployeeUuid = employee, ComponentCode = "BONUS", Amount = 1_000m, Reason = "Once" });
            await s.Runs.CalculateAsync(_admin, run.Uuid);
            await s.Runs.ReviewAsync(_admin, run.Uuid);
            await s.Runs.ApproveAsync(_admin, run.Uuid, false);
        }

        var (ok, failed) = await RaceAsync(4, async _ =>
        {
            await using var s = new Scope(this);
            await s.Runs.FinalizeAsync(_admin, run.Uuid);
        });
        ok.Should().Be(1);
        await using var db = NewContext();
        var adjustment = await db.PayrollAdjustments.IgnoreQueryFilters().SingleAsync(a => a.OrganizationId == _org);
        adjustment.AppliedInRunId.Should().NotBeNull();
    }

    /// <summary>LIFE's real providers on the real schema: an installment and a WithPayroll claim through calculate, recalculate, cancel, finalize, reverse.</summary>
    [Fact]
    public async Task Real_loan_and_claim_providers_through_recalculate_cancel_finalize_and_reverse()
    {
        _gateway.PostsToGl();
        var (first, employeeUuid) = await ApprovedRunAsync();
        int loanId, claimId, adjustmentId;
        await using (var db = NewContext())
        {
            var employeeId = await db.Employees.IgnoreQueryFilters().Where(e => e.Uuid == employeeUuid).Select(e => e.Id).FirstAsync();
            var loan = new Loan { OrganizationId = _org, LoanNumber = "L-1", EmployeeId = employeeId, Type = LoanTypes.Personal, Principal = 10_000m, InstallmentCount = 2, MonthlyAmount = 5_000m, StartMonth = new DateOnly(2026, 10, 1), Status = LoanStatuses.Disbursed, DisbursedOn = new DateOnly(2026, 9, 1) };
            loan.Installments.Add(new LoanInstallment { OrganizationId = _org, Sequence = 1, DueMonth = new DateOnly(2026, 10, 1), Amount = 5_000m });
            loan.Installments.Add(new LoanInstallment { OrganizationId = _org, Sequence = 2, DueMonth = new DateOnly(2026, 11, 1), Amount = 5_000m });
            var claim = new ExpenseClaim { OrganizationId = _org, ClaimNumber = "C-1", EmployeeId = employeeId, ClaimDate = new DateOnly(2026, 10, 5), Status = ExpenseClaimStatuses.Approved, CurrencyCode = "PKR", TotalAmount = 2_500m };
            db.AddRange(loan, claim);
            await db.SaveChangesAsync();
            var adjustment = new PayrollAdjustment { OrganizationId = _org, EmployeeId = employeeId, ComponentCode = "EXPREIMB", Amount = 2_500m, Reason = "Claim C-1", ExpenseClaimId = claim.Id };
            db.Add(adjustment);
            await db.SaveChangesAsync();
            (loanId, claimId, adjustmentId) = (loan.Id, claim.Id, adjustment.Id);
        }

        async Task<(LoanInstallment Due, LoanInstallment Later, ExpenseClaim Claim, PayrollAdjustment Adj)> State()
        {
            await using var db = NewContext();
            var rows = await db.LoanInstallments.IgnoreQueryFilters().AsNoTracking().Where(i => i.LoanId == loanId).OrderBy(i => i.Sequence).ToListAsync();
            return (rows[0], rows[1], await db.ExpenseClaims.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == claimId), await db.PayrollAdjustments.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == adjustmentId));
        }
        static async Task<PayrollRunLineModel> LineOf(Scope s, HrmCaller who, Guid run) => (await s.Runs.ListLinesAsync(who, run, null, false, 1, 5)).Data.Single();
        static void Once(PayrollRunLineModel line)
        {
            PayHarness.Amount(line, "LOAN").Should().Be(5_000m, "the October installment only");
            PayHarness.Amount(line, "EXPREIMB").Should().Be(2_500m, "the claim is paid once, not by two providers");
            line.Gross.Should().Be(100_000m);
        }

        // Calculate, and calculate again: reserved for this run, the amounts do not double.
        await using (var s = new Scope(this))
        {
            await s.Runs.ReopenAsync(_admin, first.Uuid);
            await s.Runs.CalculateAsync(_admin, first.Uuid);
            Once(await LineOf(s, _admin, first.Uuid));
        }
        await using (var s = new Scope(this))
        {
            await s.Runs.CalculateAsync(_admin, first.Uuid);
            Once(await LineOf(s, _admin, first.Uuid));
        }
        var st = await State();
        st.Due.RunId.Should().NotBeNull();
        st.Later.RunId.Should().BeNull();
        st.Adj.RunId.Should().NotBeNull();

        // Cancel: everything goes back.
        await using (var s = new Scope(this)) await s.Runs.CancelAsync(_admin, first.Uuid);
        st = await State();
        st.Due.RunId.Should().BeNull();
        st.Due.Status.Should().Be(LoanInstallmentStatuses.Pending);
        st.Adj.RunId.Should().BeNull();
        st.Adj.AppliedInRunId.Should().BeNull();
        st.Claim.Status.Should().Be(ExpenseClaimStatuses.Approved);

        // A new run takes them, finalizes, posts.
        Guid second;
        await using (var s = new Scope(this))
        {
            var run = await s.Runs.CreateAsync(_admin, new PayrollRunCreate { PeriodStart = new DateOnly(2026, 10, 1), PeriodEnd = new DateOnly(2026, 10, 31), PayDate = new DateOnly(2026, 11, 1) });
            second = run.Uuid;
            await s.Runs.CalculateAsync(_admin, second);
            var line = await LineOf(s, _admin, second);
            Once(line);
            await s.Runs.ReviewAsync(_admin, second);
            await s.Runs.ApproveAsync(_admin, second, false);
            var done = await s.Runs.FinalizeAsync(_admin, second);
            done.Status.Should().Be("Posted");

            var tx = _gateway.Posted.Single(t => t.SourceDocumentId == second);
            tx.Lines.Sum(l => l.Debit).Should().Be(tx.Lines.Sum(l => l.Credit));
            tx.Lines.Single(l => l.AccountRole == "DEFAULT_EMPLOYEE_LOAN_RECEIVABLE").Credit.Should().Be(5_000m, "task 352");
            tx.Lines.Single(l => l.AccountRole == "DEFAULT_EXPENSE_CLAIM_PAYABLE").Debit.Should().Be(2_500m, "the run clears the claim payable");
            tx.Lines.Single(l => l.AccountRole == "DEFAULT_SALARY_PAYABLE").Credit.Should().Be(line.Net);
            tx.Lines.Where(l => l.AccountRole == "DEFAULT_SALARY_EXPENSE").Sum(l => l.Debit).Should().Be(100_000m, "a reimbursement is not salary expense");
        }
        st = await State();
        st.Due.Status.Should().Be(LoanInstallmentStatuses.Deducted);
        st.Later.Status.Should().Be(LoanInstallmentStatuses.Pending);
        st.Claim.Status.Should().Be(ExpenseClaimStatuses.Paid);
        st.Adj.AppliedInRunId.Should().NotBeNull();

        // Reverse: back again, and the correction takes them once.
        Guid correction;
        await using (var s = new Scope(this)) correction = (await s.Runs.ReverseAsync(_admin, second, "Redo")).CorrectionRunUuid!.Value;
        st = await State();
        st.Due.Status.Should().Be(LoanInstallmentStatuses.Pending);
        st.Due.RunId.Should().BeNull();
        st.Claim.Status.Should().Be(ExpenseClaimStatuses.Approved);
        st.Adj.AppliedInRunId.Should().BeNull();
        await using (var s = new Scope(this))
        {
            await s.Runs.CalculateAsync(_admin, correction);
            Once(await LineOf(s, _admin, correction));
        }
    }

    [Fact]
    public async Task The_real_schema_takes_the_snapshot_and_the_unique_index_stops_a_duplicate_run()
    {
        var (run, _) = await ApprovedRunAsync();
        await using var s = new Scope(this);
        var line = (await s.Runs.ListLinesAsync(_admin, run.Uuid, null, false, 1, 5)).Data.Single();
        line.Gross.Should().Be(100_000m);
        line.Components.Should().Contain(c => c.Code == "BASIC");

        var (ok, failed) = await RaceAsync(4, async _ =>
        {
            await using var inner = new Scope(this);
            await inner.Runs.CreateAsync(_admin, new PayrollRunCreate { PeriodStart = new DateOnly(2026, 11, 1), PeriodEnd = new DateOnly(2026, 11, 30), PayDate = new DateOnly(2026, 12, 1) });
        });
        ok.Should().Be(1);
        failed.Where(e => e is not ConflictException).Should().BeEmpty();
    }
}
