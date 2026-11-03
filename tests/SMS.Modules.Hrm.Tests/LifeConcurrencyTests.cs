using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Life;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;

namespace SMS.Modules.Hrm.Tests;

/// <summary>What only a real database proves: the conditional UPDATEs and RowVersion under true parallel callers, on a throwaway LocalDB.</summary>
[Trait("Category", "SqlServer")]
public sealed class LifeConcurrencyTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A49Life_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();
    private int _employeeId;
    private int _runA, _runB;
    private Guid _claim;

    private HrmDbContext NewContext() => new(
        new DbContextOptionsBuilder<HrmDbContext>().UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3)).Options,
        new StaticTenantContext { OrganizationId = _org });

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var branch = new Branch { OrganizationId = _org, Code = "B", Name = "B" };
        var employee = new Employee { OrganizationId = _org, EmployeeNumber = "E-1", FirstName = "A", LastName = "B", Status = EmployeeStatuses.Active };
        db.Branches.Add(branch);
        db.Employees.Add(employee);
        var a = new PayrollRun { OrganizationId = _org, RunNumber = "A", PeriodStart = new(2026, 3, 1), PeriodEnd = new(2026, 3, 31), PayDate = new(2026, 4, 1), PayrollGroup = "A", CurrencyCode = "PKR", Status = PayrollRunStatuses.Calculated };
        var b = new PayrollRun { OrganizationId = _org, RunNumber = "B", PeriodStart = new(2026, 3, 1), PeriodEnd = new(2026, 3, 31), PayDate = new(2026, 4, 1), PayrollGroup = "B", CurrencyCode = "PKR", Status = PayrollRunStatuses.Calculated };
        db.PayrollRuns.AddRange(a, b);
        await db.SaveChangesAsync();
        _employeeId = employee.Id; _runA = a.Id; _runB = b.Id;

        var loan = new Loan { OrganizationId = _org, LoanNumber = "LN-1", EmployeeId = employee.Id, Type = LoanTypes.Personal, Principal = 300m, InstallmentCount = 3, MonthlyAmount = 100m, StartMonth = new(2026, 3, 1), Status = LoanStatuses.Disbursed, DisbursedOn = new(2026, 3, 1) };
        loan.Installments = LoanService.BuildSchedule(_org, 300m, 3, loan.StartMonth);
        db.Loans.Add(loan);
        var category = new ExpenseCategory { OrganizationId = _org, Code = "C", Name = "C", AccountCode = "6000" };
        db.ExpenseCategories.Add(category);
        var claim = new ExpenseClaim { OrganizationId = _org, ClaimNumber = "C-1", EmployeeId = employee.Id, ClaimDate = new(2026, 3, 1), Status = ExpenseClaimStatuses.Approved, CurrencyCode = "PKR", TotalAmount = 500m, PostingState = HrmPostingStates.Posted, SettlementMethod = ExpenseSettlementMethods.SeparatePayment };
        claim.Lines.Add(new ExpenseLine { OrganizationId = _org, Category = category, ExpenseDate = new(2026, 3, 1), Amount = 500m });
        db.ExpenseClaims.Add(claim);
        await db.SaveChangesAsync();
        _claim = claim.Uuid;
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Two_runs_racing_for_one_installment_deduct_it_once()
    {
        for (var round = 0; round < 10; round++)
        {
            await Task.Delay(1);
            var results = await Task.WhenAll(Reserve(_runA), Reserve(_runB));
            results.Count(r => r.Count > 0).Should().Be(1, "exactly one run gets the installment");
            results.Sum(r => r.Sum(i => i.Amount)).Should().Be(100m);
            await using var db = NewContext();
            await new LoanLedger(db).ReleaseAsync(_org, _runA, default);
            await new LoanLedger(db).ReleaseAsync(_org, _runB, default);
        }
    }

    private async Task<IReadOnlyList<PayrollInput>> Reserve(int run)
    {
        await using var db = NewContext();
        return await new LoanLedger(db).ReserveAsync(_org, run, _employeeId, new DateOnly(2026, 3, 31), default);
    }

    [Fact]
    public async Task A_settlement_never_takes_what_a_live_run_holds_and_rolls_back_whole()
    {
        await Reserve(_runA);
        await using var db = NewContext();
        await FluentActions.Awaiting(() => db.InTransactionAsync(() => new LoanLedger(db).ConsumeAllForSettlementAsync(_org, _employeeId, "FS-1", default)))
            .Should().ThrowAsync<ConflictException>();
        await using var check = NewContext();
        (await check.LoanInstallments.CountAsync(i => i.Status == LoanInstallmentStatuses.Deducted)).Should().Be(0);
    }

    [Fact]
    public async Task Consume_and_release_work_on_the_real_provider_and_close_the_loan_conditionally()
    {
        await using var db = NewContext();
        var ledger = new LoanLedger(db);
        await ledger.ReserveAsync(_org, _runA, _employeeId, new DateOnly(2026, 5, 31), default);
        await ledger.ConsumeAsync(_org, _runA, _employeeId, default);
        (await db.Loans.AsNoTracking().SingleAsync()).Status.Should().Be(LoanStatuses.Closed);
        await ledger.ReleaseAsync(_org, _runA, default);
        (await db.Loans.AsNoTracking().SingleAsync()).Status.Should().Be(LoanStatuses.Disbursed);
        (await db.LoanInstallments.AsNoTracking().CountAsync(i => i.Status == LoanInstallmentStatuses.Pending && i.RunId == null)).Should().Be(3);
    }

    [Fact]
    public async Task Two_concurrent_payments_of_one_claim_post_once()
    {
        var gateway = new LifeGateway();
        async Task<bool> Pay()
        {
            await using var db = NewContext();
            var scope = new Security.HrmScopeService(db);
            var seeder = new HrmSeeder(db);
            var directory = new HrmEmployeeDirectory(db, null!, seeder);
            var svc = new ExpenseClaimService(db, scope, directory, new LifeApprovals(), null!, new ExpenseClaimDecisions(db, directory, new HrmPostingHelper(gateway)), new HrmPostingHelper(gateway));
            try
            {
                await svc.PayAsync(new Security.HrmCaller(_org, 900, SMS.Shared.Authorization.PermissionCodes.HrmCodes, "A"), _claim, new LifePayRequest { PaidOn = new(2026, 3, 2) });
                return true;
            }
            catch (ConflictException) { return false; }
        }
        // The real service, several callers at once: RowVersion or the status check lets exactly one through, whichever way they interleave.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Pay()));
        results.Count(r => r).Should().Be(1);
        gateway.Posted.Should().ContainSingle("no second payment posting");
        gateway.Posted[0].DocumentType.Should().Be(FinDocType.ExpensePayment);
    }

    [Fact]
    public async Task Concurrent_disbursements_of_one_loan_post_once()
    {
        var gateway = new LifeGateway();
        var uuid = Guid.NewGuid();
        await using (var db = NewContext())
        {
            var loan = new Loan { Uuid = uuid, OrganizationId = _org, LoanNumber = "LN-RACE", EmployeeId = _employeeId, Type = LoanTypes.Advance, Principal = 300m, InstallmentCount = 3, MonthlyAmount = 100m, StartMonth = new(2026, 3, 1), Status = LoanStatuses.Approved };
            loan.Installments = LoanService.BuildSchedule(_org, 300m, 3, loan.StartMonth);
            db.Loans.Add(loan);
            await db.SaveChangesAsync();
        }
        async Task<bool> Disburse()
        {
            await using var db = NewContext();
            var svc = new LoanService(db, new Security.HrmScopeService(db), new LoanLedger(db), new HrmPostingHelper(gateway));
            try
            {
                await svc.DisburseAsync(new Security.HrmCaller(_org, 900, SMS.Shared.Authorization.PermissionCodes.HrmCodes, "A"), uuid, new LoanDisburseRequest { DisbursedOn = new(2026, 3, 1) });
                return true;
            }
            catch (ConflictException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Disburse()));
        results.Count(r => r).Should().Be(1);
        gateway.Posted.Should().ContainSingle("the loser's RowVersion conflict comes before the posting");
        gateway.Posted[0].DocumentType.Should().Be(FinDocType.LoanDisbursement);
        gateway.Posted[0].Lines.Sum(l => l.Debit).Should().Be(300m);
    }

    [Fact]
    public async Task Numbers_stay_unique_when_loans_are_created_in_parallel()
    {
        // Sp_getapplock path and the unique index, through the real service.
        var employees = new List<Guid>();
        await using (var db = NewContext())
        {
            for (var i = 0; i < 6; i++)
            {
                var e = new Employee { OrganizationId = _org, EmployeeNumber = $"P-{i}", FirstName = "P", LastName = i.ToString(), Status = EmployeeStatuses.Active };
                db.Employees.Add(e);
                employees.Add(e.Uuid);
            }
            await db.SaveChangesAsync();
        }
        async Task<string> Create(Guid employee)
        {
            await using var db = NewContext();
            var scope = new Security.HrmScopeService(db);
            var svc = new LoanService(db, scope, new LoanLedger(db), new HrmPostingHelper(new LifeGateway()));
            var loan = await svc.CreateAsync(new Security.HrmCaller(_org, 900, SMS.Shared.Authorization.PermissionCodes.HrmCodes, "A"),
                new LoanUpsert { EmployeeUuid = employee, Type = "Personal", Principal = 100m, InstallmentCount = 1, StartMonth = new(2026, 3, 1) });
            return loan.LoanNumber;
        }
        var numbers = await Task.WhenAll(employees.Select(Create));
        numbers.Distinct().Should().HaveCount(6);
    }

    [Fact]
    public async Task The_same_employee_cannot_open_two_loans_in_parallel()
    {
        await using (var db = NewContext())
        {
            var loan = await db.Loans.SingleAsync();
            loan.Status = LoanStatuses.Closed;
            await db.SaveChangesAsync();
        }
        var uuid = await EmployeeUuidAsync();
        async Task<bool> Create()
        {
            await using var db = NewContext();
            var svc = new LoanService(db, new Security.HrmScopeService(db), new LoanLedger(db), new HrmPostingHelper(new LifeGateway()));
            try
            {
                await svc.CreateAsync(new Security.HrmCaller(_org, 900, SMS.Shared.Authorization.PermissionCodes.HrmCodes, "A"), new LoanUpsert { EmployeeUuid = uuid, Type = "Personal", Principal = 100m, InstallmentCount = 1, StartMonth = new(2026, 3, 1) });
                return true;
            }
            catch (ConflictException) { return false; }
        }
        var results = await Task.WhenAll(Create(), Create(), Create());
        results.Count(r => r).Should().Be(1, "the app lock serializes the check");
    }

    private async Task<Guid> EmployeeUuidAsync()
    {
        await using var db = NewContext();
        return await db.Employees.Where(e => e.Id == _employeeId).Select(e => e.Uuid).FirstAsync();
    }
}


