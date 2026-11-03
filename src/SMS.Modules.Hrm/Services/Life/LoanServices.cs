using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Pagination;

namespace SMS.Modules.Hrm.Services.Life;

public interface ILoanService
{
    Task<PaginatedResponse<LoanModel>> ListAsync(HrmCaller caller, string? status, Guid? employeeUuid, int page, int pageSize, CancellationToken ct = default);
    Task<LoanModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<LoanModel>> MineAsync(HrmCaller caller, CancellationToken ct = default);
    Task<LoanModel> CreateAsync(HrmCaller caller, LoanUpsert request, CancellationToken ct = default);
    Task<LoanModel> UpdateAsync(HrmCaller caller, Guid uuid, LoanUpsert request, CancellationToken ct = default);
    Task<LoanModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LoanModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
    Task<LoanModel> DisburseAsync(HrmCaller caller, Guid uuid, LoanDisburseRequest request, CancellationToken ct = default);
    Task<LoanModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LoanModel> WaiveInstallmentAsync(HrmCaller caller, Guid installmentUuid, string? note, CancellationToken ct = default);
}

/// <summary>
/// The state of installments, written with one conditional UPDATE so that two runs (or a run and a settlement) cannot both take the
/// same installment: LoanInstallments has no RowVersion, and a read-then-save would let both win. The in-memory branch is for unit tests.
/// </summary>
internal sealed class LoanLedger
{
    /// <summary>A run in one of these still holds what it reserved. Any later state with the installment still Pending means its line was left out.</summary>
    private static readonly string[] LiveRun = [PayrollRunStatuses.Draft, PayrollRunStatuses.Calculated, PayrollRunStatuses.UnderReview, PayrollRunStatuses.Approved];

    private readonly HrmDbContext _db;
    public LoanLedger(HrmDbContext db) => _db = db;

    private IQueryable<LoanInstallment> Open(Guid org, int employeeId) => _db.LoanInstallments.IgnoreQueryFilters()
        .Where(i => i.OrganizationId == org && i.Status == LoanInstallmentStatuses.Pending && i.Loan!.EmployeeId == employeeId && i.Loan.Status == LoanStatuses.Disbursed);

    /// <summary>Reserves everything due by <paramref name="to"/> (arrears included) that no other live run holds, and returns it.</summary>
    public async Task<IReadOnlyList<PayrollInput>> ReserveAsync(Guid org, int runId, int employeeId, DateOnly to, CancellationToken ct)
    {
        var free = Open(org, employeeId).Where(i => i.DueMonth <= to && (i.RunId == null || i.RunId == runId || !LiveRun.Contains(i.Run!.Status)));
        if (_db.Database.IsRelational())
            await free.ExecuteUpdateAsync(s => s.SetProperty(i => i.RunId, (int?)runId), ct);
        else
        {
            foreach (var row in await free.ToListAsync(ct)) row.RunId = runId;
            await _db.SaveChangesAsync(ct);
        }

        var held = await Open(org, employeeId).AsNoTracking().Where(i => i.RunId == runId && i.DueMonth <= to)
            .Select(i => new { i.Loan!.LoanNumber, i.Loan.Type, i.Amount }).ToListAsync(ct);
        return held.GroupBy(i => new { i.LoanNumber, i.Type }).OrderBy(g => g.Key.LoanNumber)
            .Select(g => new PayrollInput(g.Key.Type == LoanTypes.Advance ? "ADV" : "LOAN", g.Sum(i => i.Amount), $"Recovery of {g.Key.LoanNumber}")).ToList();
    }

    public async Task ConsumeAsync(Guid org, int runId, int employeeId, CancellationToken ct)
    {
        var held = Open(org, employeeId).Where(i => i.RunId == runId);
        if (_db.Database.IsRelational())
            await held.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LoanInstallmentStatuses.Deducted), ct);
        else
        {
            foreach (var row in await held.ToListAsync(ct)) row.Status = LoanInstallmentStatuses.Deducted;
            await _db.SaveChangesAsync(ct);
        }
        await CloseFinishedAsync(org, employeeId, ct);
    }

    public async Task ReleaseAsync(Guid org, int runId, CancellationToken ct)
    {
        var rows = _db.LoanInstallments.IgnoreQueryFilters().Where(i => i.OrganizationId == org && i.RunId == runId && i.Status != LoanInstallmentStatuses.Waived);
        var reopened = _db.Loans.IgnoreQueryFilters().Where(l => l.OrganizationId == org && l.Status == LoanStatuses.Closed && l.Installments.Any(i => i.Status == LoanInstallmentStatuses.Pending));
        if (_db.Database.IsRelational())
        {
            await rows.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LoanInstallmentStatuses.Pending).SetProperty(i => i.RunId, (int?)null), ct);
            await reopened.ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, LoanStatuses.Disbursed), ct);
            return;
        }
        foreach (var row in await rows.ToListAsync(ct)) { row.Status = LoanInstallmentStatuses.Pending; row.RunId = null; }
        await _db.SaveChangesAsync(ct);
        foreach (var loan in await reopened.ToListAsync(ct)) loan.Status = LoanStatuses.Disbursed;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>What the employee still owes per loan type (reserved installments included: they are not deducted yet).</summary>
    public async Task<(decimal Loans, decimal Advances)> OutstandingAsync(Guid org, int employeeId, CancellationToken ct)
    {
        var rows = await Open(org, employeeId).AsNoTracking().Select(i => new { i.Loan!.Type, i.Amount }).ToListAsync(ct);
        return (rows.Where(r => r.Type != LoanTypes.Advance).Sum(r => r.Amount), rows.Where(r => r.Type == LoanTypes.Advance).Sum(r => r.Amount));
    }

    /// <summary>A final settlement recovers the whole balance. A live run holding an installment means someone must finish or cancel that run first.</summary>
    public async Task ConsumeAllForSettlementAsync(Guid org, int employeeId, string settlementNumber, CancellationToken ct)
    {
        var all = await Open(org, employeeId).CountAsync(ct);
        var free = Open(org, employeeId).Where(i => i.RunId == null || !LiveRun.Contains(i.Run!.Status));
        var note = "Recovered in " + settlementNumber;
        int done;
        if (_db.Database.IsRelational())
            done = await free.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LoanInstallmentStatuses.Deducted).SetProperty(i => i.RunId, (int?)null).SetProperty(i => i.Note, note), ct);
        else
        {
            var rows = await free.ToListAsync(ct);
            foreach (var row in rows) { row.Status = LoanInstallmentStatuses.Deducted; row.RunId = null; row.Note = note; }
            await _db.SaveChangesAsync(ct);
            done = rows.Count;
        }
        if (done != all)
            throw HrmErrors.Conflict("SETTLEMENT_LOAN_IN_RUN", "A payroll run still in progress holds an installment of this employee's loan. Finalize or cancel that run first.");
        await CloseFinishedAsync(org, employeeId, ct);
    }

    public async Task CloseFinishedAsync(Guid org, int employeeId, CancellationToken ct)
    {
        var finished = _db.Loans.IgnoreQueryFilters().Where(l => l.OrganizationId == org && l.EmployeeId == employeeId && l.Status == LoanStatuses.Disbursed
            && !l.Installments.Any(i => i.Status == LoanInstallmentStatuses.Pending));
        if (_db.Database.IsRelational())
        {
            await finished.ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, LoanStatuses.Closed), ct);
            return;
        }
        foreach (var loan in await finished.ToListAsync(ct)) loan.Status = LoanStatuses.Closed;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>One atomic move Pending and unheld → Waived. A run's reservation refuses it: the deduction is already on that run's line.</summary>
    public async Task<bool> TryWaiveAsync(int installmentId, Guid org, string note, CancellationToken ct)
    {
        var row = _db.LoanInstallments.IgnoreQueryFilters().Where(i => i.Id == installmentId && i.OrganizationId == org && i.Status == LoanInstallmentStatuses.Pending
            && (i.RunId == null || !LiveRun.Contains(i.Run!.Status)));
        if (_db.Database.IsRelational())
            return await row.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LoanInstallmentStatuses.Waived).SetProperty(i => i.RunId, (int?)null).SetProperty(i => i.Note, note), ct) == 1;
        var found = await row.FirstOrDefaultAsync(ct);
        if (found is null) return false;
        found.Status = LoanInstallmentStatuses.Waived; found.RunId = null; found.Note = note;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Task 351: what a run deducts. PAY's run owns the deduction and its posting (the LOAN/ADV components credit the receivable, task 352); this owns the installment state.</summary>
internal sealed class LoanPayrollInputProvider : IPayrollInputProvider
{
    private readonly LoanLedger _ledger;
    public LoanPayrollInputProvider(LoanLedger ledger) => _ledger = ledger;

    public Task<IReadOnlyList<PayrollInput>> GetInputsAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        _ledger.ReserveAsync(organizationId, runId, employeeId, to, ct);
    public Task MarkConsumedAsync(Guid organizationId, int runId, int employeeId, CancellationToken ct = default) => _ledger.ConsumeAsync(organizationId, runId, employeeId, ct);
    public Task ReleaseAsync(Guid organizationId, int runId, CancellationToken ct = default) => _ledger.ReleaseAsync(organizationId, runId, ct);
}

internal sealed class LoanService : LifeServiceBase, ILoanService
{
    private readonly IHrmScopeService _scope;
    private readonly LoanLedger _ledger;
    private readonly LifeUserNames? _names;

    public LoanService(HrmDbContext db, IHrmScopeService scope, LoanLedger ledger, IHrmPostingHelper posting, ICurrencyService? currency = null, IOrgCurrencyLookup? currencies = null, LifeUserNames? names = null)
        : base(db, posting, currency, currencies)
    {
        _scope = scope;
        _ledger = ledger;
        _names = names;
    }

    private IQueryable<Loan> Query(Guid org) => Db.Loans.IgnoreQueryFilters().Include(l => l.Employee).Include(l => l.Installments).ThenInclude(i => i.Run).Where(l => l.OrganizationId == org);

    internal static LoanModel Map(Loan l) => new()
    {
        Uuid = l.Uuid, LoanNumber = l.LoanNumber, EmployeeUuid = l.Employee!.Uuid, EmployeeName = l.Employee.FullName, Type = l.Type, Principal = l.Principal, InstallmentCount = l.InstallmentCount,
        MonthlyAmount = l.MonthlyAmount, StartMonth = l.StartMonth, Status = l.Status, Reason = l.Reason, ApprovedByName = l.ApprovedByName, ApprovedAt = l.ApprovedAt, DisbursedOn = l.DisbursedOn,
        PaidFromAccount = l.PaidFromAccount, PaymentReference = l.PaymentReference, PostingState = l.PostingState, PostingNote = l.PostingNote,
        // Task 353, plus waived: a waived installment is no longer owed. Nothing is owed before the money goes out.
        Outstanding = l.DisbursedOn is null ? 0m : l.Principal - l.Installments.Where(i => i.Status != LoanInstallmentStatuses.Pending).Sum(i => i.Amount),
        Installments = l.Installments.OrderBy(i => i.Sequence).Select(i => new InstallmentModel
        {
            Uuid = i.Uuid, Sequence = i.Sequence, DueMonth = i.DueMonth, Amount = i.Amount, Status = i.Status, RunNumber = i.Status == LoanInstallmentStatuses.Deducted ? i.Run?.RunNumber : null
        }).ToList()
    };

    private async Task<Loan> RequireAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var loan = await Query(caller.OrganizationId).FirstOrDefaultAsync(l => l.Uuid == uuid, ct) ?? throw new NotFoundException("Loan", uuid);
        if (!await _scope.CanAccessAsync(caller, loan.EmployeeId, ct)) throw new NotFoundException("Loan", uuid);
        return loan;
    }

    public async Task<PaginatedResponse<LoanModel>> ListAsync(HrmCaller caller, string? status, Guid? employeeUuid, int page, int pageSize, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var people = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var query = Query(caller.OrganizationId).AsNoTracking().Where(l => people.Contains(l.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) { var s = HrmValidate.OneOf(status, LoanStatuses.All, "status"); query = query.Where(l => l.Status == s); }
        if (employeeUuid is { } e2) query = query.Where(l => l.Employee!.Uuid == e2);
        (page, pageSize) = LifePaging.Normalize(page, pageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(l => l.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return LifePaging.Page(rows.Select(Map).ToList(), total, page, pageSize);
    }

    public async Task<LoanModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) => Map(await RequireAsync(caller, uuid, ct));

    public async Task<IReadOnlyList<LoanModel>> MineAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var me = await _scope.RequireOwnEmployeeAsync(caller, ct);
        var rows = await Query(caller.OrganizationId).AsNoTracking().Where(l => l.EmployeeId == me.Id).OrderByDescending(l => l.Id).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    /// <summary>Equal installments rounded down to 2 dp; the last absorbs the rounding, so the schedule sums to the principal exactly.</summary>
    internal static List<LoanInstallment> BuildSchedule(Guid org, decimal principal, int count, DateOnly startMonth)
    {
        var monthly = Math.Floor(principal * 100m / count) / 100m;
        return Enumerable.Range(1, count).Select(n => new LoanInstallment
        {
            OrganizationId = org, Sequence = n, DueMonth = startMonth.AddMonths(n - 1), Amount = n == count ? principal - monthly * (count - 1) : monthly
        }).ToList();
    }

    private static void Apply(Loan loan, LoanUpsert request)
    {
        loan.Type = HrmValidate.OneOf(request.Type, LoanTypes.All, "type");
        if (request.Principal <= 0 || request.Principal != LifeMoney.Round(request.Principal)) throw new BadRequestException("The principal must be positive, with at most 2 decimals.");
        if (request.InstallmentCount is < 1 or > 360) throw new BadRequestException("The number of installments must be between 1 and 360.");
        if (request.StartMonth == default) throw new BadRequestException("The start month is required.");
        if (request.Principal * 100m < request.InstallmentCount) throw new BadRequestException("The principal is too small for that many installments.");
        loan.Principal = request.Principal;
        loan.InstallmentCount = request.InstallmentCount;
        loan.StartMonth = new DateOnly(request.StartMonth.Year, request.StartMonth.Month, 1);
        loan.MonthlyAmount = Math.Floor(request.Principal * 100m / request.InstallmentCount) / 100m;
        loan.Reason = HrmValidate.Optional(request.Reason, 500, "Reason");
    }

    private async Task CheckNoActiveAsync(Guid org, int employeeId, string type, int id, CancellationToken ct)
    {
        var active = new[] { LoanStatuses.Draft, LoanStatuses.Approved, LoanStatuses.Disbursed };
        if (await Db.Loans.IgnoreQueryFilters().AnyAsync(l => l.OrganizationId == org && l.EmployeeId == employeeId && l.Type == type && l.Id != id && active.Contains(l.Status), ct))
            throw HrmErrors.Conflict("LOAN_ACTIVE_EXISTS", $"This employee already has an open {type} loan.");
    }

    public async Task<LoanModel> CreateAsync(HrmCaller caller, LoanUpsert request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid, ct);
        var org = caller.OrganizationId;
        var loan = new Loan { EmployeeId = employee.Id };
        Apply(loan, request);
        Stamp(loan, caller, isNew: true);
        loan.Installments = BuildSchedule(org, loan.Principal, loan.InstallmentCount, loan.StartMonth);
        foreach (var i in loan.Installments) Stamp(i, caller, isNew: true);
        await Db.InTransactionAsync(async () =>
        {
            await LockAsync($"hrm-loan-{org:N}-{employee.Id}", ct);
            await CheckNoActiveAsync(org, employee.Id, loan.Type, 0, ct);
            if (Db.Entry(loan).State == EntityState.Detached) Db.Loans.Add(loan);
            var prefix = $"LN-{HrmValidate.Today.Year}";
            await SaveNumberedAsync(prefix, Db.Loans.IgnoreQueryFilters().Where(l => l.OrganizationId == org).Select(l => l.LoanNumber), n => loan.LoanNumber = n, ct);
        }, ct);
        return await GetAsync(caller, loan.Uuid, ct);
    }

    public async Task<LoanModel> UpdateAsync(HrmCaller caller, Guid uuid, LoanUpsert request, CancellationToken ct = default)
    {
        var loan = await RequireAsync(caller, uuid, ct);
        if (loan.Status != LoanStatuses.Draft) throw HrmErrors.Conflict("LOAN_NOT_DRAFT", "Only a draft loan can be changed.");
        var type = loan.Type;
        Apply(loan, request);
        if (loan.Type != type) await CheckNoActiveAsync(caller.OrganizationId, loan.EmployeeId, loan.Type, loan.Id, ct);
        Db.LoanInstallments.RemoveRange(loan.Installments);
        // Sequences are unique per loan: the old rows must be gone before the new ones go in.
        await Db.InTransactionAsync(async () =>
        {
            await SaveAsync(ct, "This loan");
            loan.Installments = BuildSchedule(caller.OrganizationId, loan.Principal, loan.InstallmentCount, loan.StartMonth);
            foreach (var i in loan.Installments) Stamp(i, caller, isNew: true);
            Stamp(loan, caller);
            await SaveAsync(ct, "This loan");
        }, ct);
        return Map(loan);
    }

    /// <summary>Nobody approves money for themselves.</summary>
    private async Task RefuseOwnAsync(HrmCaller caller, int employeeId, CancellationToken ct)
    {
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == employeeId) throw new ForbiddenException("You cannot decide your own loan.");
    }

    public async Task<LoanModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var loan = await RequireAsync(caller, uuid, ct);
        await RefuseOwnAsync(caller, loan.EmployeeId, ct);
        if (loan.Status != LoanStatuses.Draft) throw HrmErrors.Conflict("LOAN_NOT_DRAFT", "Only a draft loan can be approved.");
        loan.Status = LoanStatuses.Approved;
        loan.ApprovedBy = caller.UserIdOrNull;
        loan.ApprovedByName = _names is null ? caller.UserName : await _names.NameAsync(caller.OrganizationId, caller.UserIdOrNull, caller.UserName, ct);
        loan.ApprovedAt = DateTime.UtcNow;
        Stamp(loan, caller);
        await SaveAsync(ct, "This loan");
        return Map(loan);
    }

    public async Task<LoanModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default)
    {
        var loan = await RequireAsync(caller, uuid, ct);
        await RefuseOwnAsync(caller, loan.EmployeeId, ct);
        if (loan.Status != LoanStatuses.Draft) throw HrmErrors.Conflict("LOAN_NOT_DRAFT", "Only a draft loan can be rejected.");
        loan.Status = LoanStatuses.Rejected;
        loan.PostingNote = HrmValidate.Optional(note, 1000, "Note");
        Stamp(loan, caller);
        await SaveAsync(ct, "This loan");
        return Map(loan);
    }

    internal static FinancialTransaction BuildDisbursement(Loan loan, string currency)
    {
        var receivable = loan.Type == LoanTypes.Advance ? "DEFAULT_EMPLOYEE_ADVANCE" : "DEFAULT_EMPLOYEE_LOAN_RECEIVABLE";
        return new FinancialTransaction
        {
            DocumentType = FinDocType.LoanDisbursement, SourceModule = PostingSourceModules.Hrm, SourceDocumentId = loan.Uuid, SourceDocumentNumber = loan.LoanNumber,
            TransactionDate = LifeMoney.Date(loan.DisbursedOn!.Value), CurrencyCode = currency, Description = $"Disbursement {loan.LoanNumber}",
            Lines = LifeMoney.Number(
            [
                new() { AccountRole = receivable, Debit = loan.Principal, Label = $"Disbursement {loan.LoanNumber}" },
                new() { AccountRole = LifeMoney.CashRole(loan.PaidFromAccount), Credit = loan.Principal, Label = $"Disbursement {loan.LoanNumber}" }
            ])
        };
    }

    public async Task<LoanModel> DisburseAsync(HrmCaller caller, Guid uuid, LoanDisburseRequest request, CancellationToken ct = default)
    {
        var loan = await RequireAsync(caller, uuid, ct);
        if (loan.Status != LoanStatuses.Approved) throw HrmErrors.Conflict("LOAN_NOT_APPROVED", "Only an approved loan can be disbursed.");
        if (request.DisbursedOn == default) throw new BadRequestException("The disbursement date is required.");
        loan.Status = LoanStatuses.Disbursed;
        loan.DisbursedOn = request.DisbursedOn;
        loan.DisbursedBy = caller.UserIdOrNull;
        loan.PaidFromAccount = HrmValidate.OneOf(request.PaidFromAccount ?? HrmPaymentAccounts.Bank, HrmPaymentAccounts.All, "paidFromAccount");
        loan.PaymentReference = HrmValidate.Optional(request.Reference, 100, "Reference");
        loan.PostingState = HrmPostingStates.Pending;
        // Paid out after the schedule's first month: move the schedule, so the first run does not recover months the employee never had the money.
        var first = new DateOnly(request.DisbursedOn.Year, request.DisbursedOn.Month, 1);
        if (first > loan.StartMonth)
        {
            var shift = (first.Year - loan.StartMonth.Year) * 12 + first.Month - loan.StartMonth.Month;
            foreach (var i in loan.Installments) i.DueMonth = i.DueMonth.AddMonths(shift);
            loan.StartMonth = first;
        }
        Stamp(loan, caller);
        await SaveAsync(ct, "This loan"); // RowVersion: of two concurrent disbursements one gets 409 here and posts nothing.
        await PostAsync(caller, loan, ct);
        return Map(loan);
    }

    private async Task PostAsync(HrmCaller caller, Loan loan, CancellationToken ct)
    {
        var currency = await BaseCurrencyAsync(caller.OrganizationId, ct);
        var outcome = await Posting.PostAsync(BuildDisbursement(loan, currency), ct);
        loan.PostingState = outcome.State;
        loan.PostingNote = outcome.Note;
        Stamp(loan, caller);
        await SaveAsync(ct, "This loan");
    }

    public async Task<LoanModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var loan = await RequireAsync(caller, uuid, ct);
        if (loan.DisbursedOn is null) throw HrmErrors.Conflict("LOAN_NOT_APPROVED", "This loan has not been disbursed.");
        await PostAsync(caller, loan, ct);
        return Map(loan);
    }

    public async Task<LoanModel> WaiveInstallmentAsync(HrmCaller caller, Guid installmentUuid, string? note, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var found = await Db.LoanInstallments.IgnoreQueryFilters().AsNoTracking().Where(i => i.OrganizationId == org && i.Uuid == installmentUuid)
            .Select(i => new { i.Id, LoanUuid = i.Loan!.Uuid, i.Loan.EmployeeId }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Installment", installmentUuid);
        if (!await _scope.CanAccessAsync(caller, found.EmployeeId, ct)) throw new NotFoundException("Installment", installmentUuid);
        await RefuseOwnAsync(caller, found.EmployeeId, ct);
        var reason = HrmValidate.Name(note, 500, "Note");
        if (!await _ledger.TryWaiveAsync(found.Id, org, reason, ct))
            throw HrmErrors.Conflict("LOAN_INSTALLMENT_NOT_PENDING", "This installment is already deducted or waived, or a payroll run in progress holds it.");
        await _ledger.CloseFinishedAsync(org, found.EmployeeId, ct);
        Db.ChangeTracker.Clear(); // Conditional updates went around the tracker.
        return await GetAsync(caller, found.LoanUuid, ct);
    }
}

internal static class LifePaging
{
    public static (int Page, int PageSize) Normalize(int page, int pageSize) => (Math.Max(page, 1), pageSize <= 0 ? 25 : Math.Min(pageSize, 200));
    public static PaginatedResponse<T> Page<T>(List<T> rows, int total, int page, int pageSize) => new() { Data = rows, TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling(total / (double)pageSize) };
}

