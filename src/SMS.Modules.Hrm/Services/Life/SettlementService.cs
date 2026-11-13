using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm.Services.Life;

public interface IFinalSettlementService
{
    Task<IReadOnlyList<SettlementModel>> ListAsync(HrmCaller caller, string? status, CancellationToken ct = default);
    Task<SettlementModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SettlementModel> ComputeAsync(HrmCaller caller, SettlementComputeRequest request, CancellationToken ct = default);
    Task<SettlementModel> UpdateAsync(HrmCaller caller, Guid uuid, SettlementUpdate request, CancellationToken ct = default);
    Task<SettlementModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SettlementModel> PostAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SettlementModel> PayAsync(HrmCaller caller, Guid uuid, LifePayRequest request, CancellationToken ct = default);
    Task<SettlementModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SettlementModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class FinalSettlementService : LifeServiceBase, IFinalSettlementService
{
    internal const string SalaryCode = "REMSAL", EncashCode = "ENCASH", GratuityCode = "GRATUITY", LoanCode = "LOAN", AdvanceCode = "ADV";
    private const string PayableRole = "DEFAULT_SALARY_PAYABLE";

    /// <summary>What a hand-added line may post to: earnings to expense roles, deductions to liability or receivable roles.</summary>
    private static readonly HashSet<string> EditableRoles =
    [
        "DEFAULT_SALARY_EXPENSE", "DEFAULT_BONUS_EXPENSE", "DEFAULT_OVERTIME_EXPENSE", "DEFAULT_GRATUITY_EXPENSE", "DEFAULT_LEAVE_ENCASHMENT_EXPENSE", "DEFAULT_GRATUITY_PROVISION",
        "DEFAULT_EMPLOYEE_TAX_PAYABLE", "DEFAULT_OTHER_DEDUCTION_PAYABLE", "DEFAULT_EOBI_PAYABLE", "DEFAULT_PF_PAYABLE", "DEFAULT_SOCIAL_SECURITY_PAYABLE",
    ];

    private readonly IHrmScopeService _scope;
    private readonly IHrmEmployeeDirectory _directory;
    private readonly LoanLedger _ledger;

    public FinalSettlementService(HrmDbContext db, IHrmScopeService scope, IHrmEmployeeDirectory directory, LoanLedger ledger, IHrmPostingHelper posting, ICurrencyService? currency = null, IOrgCurrencyLookup? currencies = null)
        : base(db, posting, currency, currencies)
    {
        _scope = scope;
        _directory = directory;
        _ledger = ledger;
    }

    private IQueryable<FinalSettlement> Query(Guid org) => Db.FinalSettlements.IgnoreQueryFilters().Include(s => s.Employee).Include(s => s.Lines).Where(s => s.OrganizationId == org);

    private static SettlementModel Map(FinalSettlement s) => new()
    {
        Uuid = s.Uuid, SettlementNumber = s.SettlementNumber, EmployeeUuid = s.Employee!.Uuid, EmployeeName = s.Employee.FullName, LastWorkingDay = s.LastWorkingDay, ServiceYears = s.ServiceYears,
        LastDrawnSalary = s.LastDrawnSalary, TotalEarnings = s.TotalEarnings, TotalDeductions = s.TotalDeductions, Net = s.Net, Status = s.Status, Notes = s.Notes, PaidOn = s.PaidOn,
        PaidFromAccount = s.PaidFromAccount, PaymentReference = s.PaymentReference, PostingState = s.PostingState, PostingNote = s.PostingNote,
        Lines = s.Lines.OrderBy(l => l.Sequence).Select(l => new SettlementLineModel { Code = l.Code, Description = l.Description, Type = l.Type, Amount = l.Amount, Basis = l.Basis, PostingRole = l.PostingRole }).ToList()
    };

    private async Task<FinalSettlement> RequireAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Query(caller.OrganizationId).FirstOrDefaultAsync(s => s.Uuid == uuid, ct) ?? throw new NotFoundException("Settlement", uuid);
        if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Settlement", uuid);
        return row;
    }

    public async Task<IReadOnlyList<SettlementModel>> ListAsync(HrmCaller caller, string? status, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var people = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var query = Query(caller.OrganizationId).AsNoTracking().Where(s => people.Contains(s.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) { var st = HrmValidate.OneOf(status, SettlementStatuses.All, "status"); query = query.Where(s => s.Status == st); }
        return (await query.OrderByDescending(s => s.Id).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<SettlementModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) => Map(await RequireAsync(caller, uuid, ct));

    /// <summary>Whole anniversaries plus the part-year, counting the last working day itself: 2020-01-01 to 2024-12-31 is exactly 5.</summary>
    internal static decimal ServiceYears(DateOnly join, DateOnly lastDay)
    {
        var end = lastDay.AddDays(1);
        var years = 0;
        while (join.AddYears(years + 1) <= end) years++;
        var from = join.AddYears(years);
        var length = join.AddYears(years + 1).DayNumber - from.DayNumber;
        return Math.Round(years + (decimal)(end.DayNumber - from.DayNumber) / length, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>Unpaid days as a fraction of months, month by month; a fixed divisor caps a full month at one month's pay.</summary>
    internal static decimal UnpaidMonths(DateOnly from, DateOnly to, int? divisor)
    {
        var months = 0m;
        for (var d = new DateOnly(from.Year, from.Month, 1); d <= to; d = d.AddMonths(1))
        {
            var inMonth = DateTime.DaysInMonth(d.Year, d.Month);
            var start = from > d ? from : d;
            var last = new DateOnly(d.Year, d.Month, inMonth);
            var end = to < last ? to : last;
            var days = end.DayNumber - start.DayNumber + 1;
            if (days == inMonth) months += 1m;
            else months += divisor is { } div ? Math.Min((decimal)days / div, 1m) : (decimal)days / inMonth;
        }
        return months;
    }

    /// <summary>G-21: the rate is data and is flagged; this applies it and asserts nothing. Gratuity = wage (capped by WageCeiling, or FixedBasis) × service years × EmployerPercent / 100.</summary>
    internal static decimal Gratuity(decimal salary, decimal serviceYears, StatutoryRate rate)
    {
        var wage = rate.FixedBasis ?? salary;
        if (rate.WageCeiling is { } cap && wage > cap) wage = cap;
        return LifeMoney.Round(wage * serviceYears * rate.EmployerPercent / 100m);
    }

    private Task<StatutoryRate?> GratuityRateAsync(Guid org, DateOnly on, CancellationToken ct) => Db.StatutoryRates.IgnoreQueryFilters().AsNoTracking()
        .Where(r => r.OrganizationId == org && r.RateType == StatutoryRateTypes.Gratuity && r.EffectiveFrom <= on && (r.EffectiveTo == null || r.EffectiveTo >= on))
        .OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(ct);

    /// <summary>The paid-leave balance per type: Σ ledger delta, positive only. A negative balance is not charged here; HR can add a line.</summary>
    private async Task<List<(int TypeId, decimal Days)>> EncashableAsync(Guid org, int employeeId, CancellationToken ct)
    {
        var paid = await Db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == org && t.IsPaid).Select(t => t.Id).ToListAsync(ct);
        var rows = await Db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == org && l.EmployeeId == employeeId && paid.Contains(l.LeaveTypeId))
            .Select(l => new { l.LeaveTypeId, l.Delta }).ToListAsync(ct);
        return rows.GroupBy(r => r.LeaveTypeId).Select(g => (g.Key, g.Sum(r => r.Delta))).Where(x => x.Item2 > 0).ToList();
    }

    public async Task<SettlementModel> ComputeAsync(HrmCaller caller, SettlementComputeRequest request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid, ct);
        if (employee.Status is EmployeeStatuses.Candidate or EmployeeStatuses.Archived) throw HrmErrors.Conflict("SETTLEMENT_NOT_EMPLOYED", "This person is not on the books.");
        var last = request.LastWorkingDay;
        if (last == default) throw new BadRequestException("The last working day is required.");
        var join = employee.JoinDate ?? await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == org && r.EmployeeId == employee.Id).MinAsync(r => (DateOnly?)r.EffectiveFrom, ct)
            ?? throw HrmErrors.Conflict("SETTLEMENT_NO_JOIN_DATE", "This employee has no join date.");
        if (last < join) throw new BadRequestException("The last working day is before the join date.");

        var salaryLines = await Db.SalaryAssignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && a.EmployeeId == employee.Id && a.ApprovalStatus == SalaryAssignmentStatuses.Approved && a.EffectiveFrom <= last && (a.EffectiveTo == null || a.EffectiveTo >= last))
            .SelectMany(a => a.Lines).Where(l => l.Component!.InGross).Select(l => l.Amount).ToListAsync(ct);
        if (salaryLines.Count == 0) throw HrmErrors.Conflict("SETTLEMENT_NO_SALARY", "This employee has no approved salary on the last working day.");
        var gross = salaryLines.Sum();

        var settings = await _directory.GetSettingsAsync(org, ct);
        var paidStates = new[] { PayrollRunStatuses.Finalized, PayrollRunStatuses.Posted, PayrollRunStatuses.Paid };
        var paidTo = await Db.PayrollRunLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.EmployeeId == employee.Id && !l.IsBlocked && paidStates.Contains(l.Run!.Status)).MaxAsync(l => (DateOnly?)l.Run!.PeriodEnd, ct);
        var from = paidTo is { } p && p.AddDays(1) > join ? p.AddDays(1) : join;

        var lines = new List<FinalSettlementLine>();
        void Line(string code, string text, string type, decimal amount, decimal? basis, string role) => lines.Add(new FinalSettlementLine
        {
            OrganizationId = org, Code = code, Description = text, Type = type, Amount = amount, Basis = basis, PostingRole = role, Sequence = lines.Count + 1
        });
        const string E = SalaryComponentTypes.Earning, D = SalaryComponentTypes.Deduction;

        var notes = new List<string>();
        if (from <= last)
        {
            var months = UnpaidMonths(from, last, settings.WorkingDaysDivisor);
            Line(SalaryCode, $"Salary {from:yyyy-MM-dd} to {last:yyyy-MM-dd}", E, LifeMoney.Round(gross * months), last.DayNumber - from.DayNumber + 1, "DEFAULT_SALARY_EXPENSE");
        }

        var encashable = (await EncashableAsync(org, employee.Id, ct)).Sum(x => x.Days);
        if (encashable > 0)
        {
            var divisor = settings.WorkingDaysDivisor ?? DateTime.DaysInMonth(last.Year, last.Month);
            Line(EncashCode, $"Leave encashment, {encashable:0.##} days", E, LifeMoney.Round(encashable * gross / divisor), encashable, "DEFAULT_LEAVE_ENCASHMENT_EXPENSE");
        }

        var years = ServiceYears(join, last);
        var rate = await GratuityRateAsync(org, last, ct);
        if (rate is null) notes.Add("No gratuity rate is configured for this date: gratuity is 0 and this settlement cannot be posted until one exists and is verified.");
        else
        {
            if (!rate.IsVerified) notes.Add($"Gratuity uses '{rate.Name}', which has not been reviewed (G-21). Posting is blocked until it is verified.");
            Line(GratuityCode, $"Gratuity, {years:0.####} years", E, Gratuity(gross, years, rate), rate.FixedBasis ?? gross, "DEFAULT_GRATUITY_EXPENSE");
        }

        var (loans, advances) = await _ledger.OutstandingAsync(org, employee.Id, ct);
        if (loans > 0) Line(LoanCode, "Loan balance", D, loans, null, "DEFAULT_EMPLOYEE_LOAN_RECEIVABLE");
        if (advances > 0) Line(AdvanceCode, "Advance balance", D, advances, null, "DEFAULT_EMPLOYEE_ADVANCE");

        var openClaims = await Db.ExpenseClaims.IgnoreQueryFilters().AsNoTracking().CountAsync(c => c.OrganizationId == org && c.EmployeeId == employee.Id
            && c.PaymentMode == ExpensePaymentModes.OwnAccount && c.Status == ExpenseClaimStatuses.Approved, ct);
        if (openClaims > 0) notes.Add($"{openClaims} approved expense claim(s) are not paid yet; they are not part of this settlement. Pay them separately.");

        var row = new FinalSettlement
        {
            EmployeeId = employee.Id, LastWorkingDay = last, ServiceYears = years, LastDrawnSalary = gross, Lines = lines, Notes = notes.Count == 0 ? null : string.Join(" ", notes)
        };
        Stamp(row, caller, isNew: true);
        foreach (var l in lines) Stamp(l, caller, isNew: true);
        Totals(row);

        try
        {
            await Db.InTransactionAsync(async () =>
            {
                await LockAsync($"hrm-settlement-{org:N}-{employee.Id}", ct);
                if (await Db.FinalSettlements.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.EmployeeId == employee.Id && s.Status != SettlementStatuses.Cancelled && s.Id != row.Id, ct))
                    throw HrmErrors.Conflict("SETTLEMENT_EXISTS", "This employee already has a settlement. Cancel it to compute again.");
                if (Db.Entry(row).State == EntityState.Detached) Db.FinalSettlements.Add(row);
                await SaveNumberedAsync($"FS-{HrmValidate.Today.Year}", Db.FinalSettlements.IgnoreQueryFilters().Where(s => s.OrganizationId == org).Select(s => s.SettlementNumber), n => row.SettlementNumber = n, ct);
            }, ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            throw HrmErrors.Conflict("SETTLEMENT_EXISTS", "This employee already has a settlement. Cancel it to compute again.");
        }
        return await GetAsync(caller, row.Uuid, ct);
    }

    private static void Totals(FinalSettlement s)
    {
        s.TotalEarnings = s.Lines.Where(l => l.Type == SalaryComponentTypes.Earning).Sum(l => l.Amount);
        s.TotalDeductions = s.Lines.Where(l => l.Type == SalaryComponentTypes.Deduction).Sum(l => l.Amount);
        s.Net = s.TotalEarnings - s.TotalDeductions;
    }

    public async Task<SettlementModel> UpdateAsync(HrmCaller caller, Guid uuid, SettlementUpdate request, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status != SettlementStatuses.Draft) throw HrmErrors.Conflict("SETTLEMENT_NOT_DRAFT", "Only a draft settlement can be changed.");
        // LOAN and ADV come from the loan ledger and clear it exactly at posting: an edit would post one figure and clear another.
        var locked = row.Lines.Where(l => l.Code is LoanCode or AdvanceCode).ToList();
        foreach (var code in new[] { LoanCode, AdvanceCode })
        {
            var before = locked.Where(l => l.Code == code).Sum(l => l.Amount);
            var after = request.Lines.Where(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)).Sum(l => l.Amount);
            if (before != after) throw HrmErrors.Bad("SETTLEMENT_LINE_LOCKED", $"The {code} line comes from the loan ledger and cannot be edited.");
        }
        var kept = new List<FinalSettlementLine>(locked);
        foreach (var line in request.Lines.Where(l => !string.Equals(l.Code, LoanCode, StringComparison.OrdinalIgnoreCase) && !string.Equals(l.Code, AdvanceCode, StringComparison.OrdinalIgnoreCase)))
        {
            var type = HrmValidate.OneOf(line.Type, [SalaryComponentTypes.Earning, SalaryComponentTypes.Deduction], "type");
            if (line.Amount < 0 || line.Amount != LifeMoney.Round(line.Amount)) throw new BadRequestException("A line amount cannot be negative and has at most 2 decimals.");
            var role = line.PostingRole ?? (type == SalaryComponentTypes.Earning ? "DEFAULT_SALARY_EXPENSE" : "DEFAULT_OTHER_DEDUCTION_PAYABLE");
            if (!EditableRoles.Contains(role)) throw HrmErrors.Bad("SETTLEMENT_ROLE_INVALID", $"A settlement line cannot post to {role}.");
            var entry = new FinalSettlementLine
            {
                OrganizationId = caller.OrganizationId, Code = HrmValidate.Code(line.Code), Description = HrmValidate.Name(line.Description, 200, "Description"), Type = type, Amount = line.Amount, Basis = line.Basis, PostingRole = role
            };
            Stamp(entry, caller, isNew: true);
            kept.Add(entry);
        }
        Db.FinalSettlementLines.RemoveRange(row.Lines.Except(locked));
        var seq = 1;
        foreach (var line in kept) line.Sequence = seq++;
        row.Lines = kept;
        if (request.Notes is not null) row.Notes = HrmValidate.Optional(request.Notes, 2000, "Notes");
        Totals(row);
        Stamp(row, caller);
        await SaveAsync(ct, "This settlement");
        return Map(row);
    }

    public async Task<SettlementModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId) throw new ForbiddenException("You cannot approve your own settlement.");
        if (row.Status != SettlementStatuses.Draft) throw HrmErrors.Conflict("SETTLEMENT_NOT_DRAFT", "Only a draft settlement can be approved.");
        row.Status = SettlementStatuses.Approved;
        row.ApprovedBy = caller.UserIdOrNull;
        row.ApprovedAt = DateTime.UtcNow;
        Stamp(row, caller);
        await SaveAsync(ct, "This settlement");
        return Map(row);
    }

    /// <summary>
    /// Accrual: Dr each earning's expense by cost center; Cr each deduction's role; Cr salary payable with the net (Dr when the employee owes,
    /// so the document balances; the balance stays on the payable for HR to collect). Summed by role, no employee on it (G-11).
    /// </summary>
    private async Task<FinancialTransaction> BuildPostingAsync(Guid org, FinalSettlement s, CancellationToken ct)
    {
        var settings = await _directory.GetSettingsAsync(org, ct);
        var terms = await _directory.GetTermsAsync(org, s.EmployeeId, s.LastWorkingDay, ct);
        var shares = terms is null ? [] : await _directory.GetCostCenterSharesAsync(org, terms.RecordId, settings.PayrollCostCenterMode, ct);
        var central = terms is null ? [] : await _directory.GetCostCenterSharesAsync(org, terms.RecordId, PayrollCostCenterModes.Central, ct);
        var liabilityCenter = central.Count > 0 ? central[0].CostCenterCode : null;
        var label = $"Final settlement {s.SettlementNumber}";

        var lines = new List<FinancialTransactionLine>();
        foreach (var group in s.Lines.Where(l => l.Type == SalaryComponentTypes.Earning).GroupBy(l => l.PostingRole))
            foreach (var (center, amount) in LifeMoney.Split(group.Sum(l => l.Amount), shares))
                lines.Add(new() { AccountRole = group.Key, Debit = amount, CostCenter = center, Label = label });
        foreach (var group in s.Lines.Where(l => l.Type == SalaryComponentTypes.Deduction).GroupBy(l => l.PostingRole))
            lines.Add(new() { AccountRole = group.Key, Credit = group.Sum(l => l.Amount), CostCenter = liabilityCenter, Label = label });
        if (s.Net > 0) lines.Add(new() { AccountRole = PayableRole, Credit = s.Net, CostCenter = liabilityCenter, Label = label });
        else if (s.Net < 0) lines.Add(new() { AccountRole = PayableRole, Debit = -s.Net, CostCenter = liabilityCenter, Label = label });

        return new FinancialTransaction
        {
            DocumentType = FinDocType.FinalSettlement, SourceModule = PostingSourceModules.Hrm, SourceDocumentId = s.Uuid, SourceDocumentNumber = s.SettlementNumber,
            TransactionDate = LifeMoney.Date(s.LastWorkingDay), CurrencyCode = await BaseCurrencyAsync(org, ct), Description = label, Lines = LifeMoney.Number(lines)
        };
    }

    private async Task<FinancialTransaction> BuildPaymentAsync(Guid org, FinalSettlement s, CancellationToken ct)
    {
        var label = $"Final settlement payment {s.SettlementNumber}";
        return new FinancialTransaction
        {
            DocumentType = FinDocType.PayrollPayment, SourceModule = PostingSourceModules.Hrm, SourceDocumentId = s.Uuid, SourceDocumentNumber = s.SettlementNumber,
            TransactionDate = LifeMoney.Date(s.PaidOn!.Value), CurrencyCode = await BaseCurrencyAsync(org, ct), Description = label,
            Lines = LifeMoney.Number(
            [
                new() { AccountRole = PayableRole, Debit = s.Net, Label = label },
                new() { AccountRole = LifeMoney.CashRole(s.PaidFromAccount), Credit = s.Net, Label = label }
            ])
        };
    }

    public async Task<SettlementModel> PostAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status != SettlementStatuses.Approved) throw HrmErrors.Conflict("SETTLEMENT_NOT_APPROVED", "Only an approved settlement can be posted.");

        var rate = await GratuityRateAsync(org, row.LastWorkingDay, ct);
        if (rate is not { IsVerified: true })
            throw HrmErrors.Conflict("PAYROLL_STATUTORY_UNVERIFIED", rate is null
                ? "No gratuity rate is configured for this date. Add one under statutory rates and have it verified."
                : $"'{rate.Name}' has not been reviewed. Someone with statutory verification rights must mark it first.");

        await Db.InTransactionAsync(async () =>
        {
            await LockAsync($"hrm-settlement-{org:N}-{row.EmployeeId}", ct);
            // A run may have deducted, or leave been taken, since the compute: posting the old figures would recover money twice.
            var (loans, advances) = await _ledger.OutstandingAsync(org, row.EmployeeId, ct);
            if (loans != row.Lines.Where(l => l.Code == LoanCode).Sum(l => l.Amount) || advances != row.Lines.Where(l => l.Code == AdvanceCode).Sum(l => l.Amount))
                throw HrmErrors.Conflict("SETTLEMENT_STALE", "The loan balance changed since this was computed. Cancel it and compute again.");
            await EnsureSalaryNotInPayrollAsync(org, row, ct);
            var leave = await EncashableAsync(org, row.EmployeeId, ct);
            var encashLine = row.Lines.FirstOrDefault(l => l.Code == EncashCode);
            if (encashLine?.Basis is { } days && days != leave.Sum(x => x.Days))
                throw HrmErrors.Conflict("SETTLEMENT_STALE", "The leave balance changed since this was computed. Cancel it and compute again.");

            await _ledger.ConsumeAllForSettlementAsync(org, row.EmployeeId, row.SettlementNumber, ct);
            if (encashLine is not null)
            {
                var exists = await Db.LeaveLedgerEntries.IgnoreQueryFilters().AnyAsync(l => l.OrganizationId == org && l.ReferenceUuid == row.Uuid && l.Reason == LeaveLedgerReasons.Encashment, ct);
                if (!exists)
                    foreach (var (typeId, balance) in leave)
                    {
                        var entry = new LeaveLedgerEntry
                        {
                            EmployeeId = row.EmployeeId, LeaveTypeId = typeId, Delta = -balance, Reason = LeaveLedgerReasons.Encashment, Date = row.LastWorkingDay, Year = row.LastWorkingDay.Year, ReferenceUuid = row.Uuid, Note = "Encashed in " + row.SettlementNumber
                        };
                        Stamp(entry, caller, isNew: true);
                        Db.LeaveLedgerEntries.Add(entry);
                    }
            }
            row.Status = SettlementStatuses.Posted;
            row.PostingState = HrmPostingStates.Pending;
            Stamp(row, caller);
            await SaveAsync(ct, "This settlement"); // RowVersion: the loser of two concurrent posts rolls its recoveries back.
        }, ct);

        await ApplyAccrualAsync(caller, row, ct);
        return Map(row);
    }

    /// <summary>The days the REMSAL line pays: its basis is their count, ending on the last working day. Null without such a line.</summary>
    internal static (DateOnly From, DateOnly To)? SalaryDays(FinalSettlement s)
    {
        var line = s.Lines.FirstOrDefault(l => l.Code == SalaryCode && l.Type == SalaryComponentTypes.Earning && l.Amount != 0m);
        if (line is null) return null;
        var days = line.Basis is { } b && b >= 1m ? (int)b : 1;
        return (s.LastWorkingDay.AddDays(1 - days), s.LastWorkingDay);
    }

    /// <summary>
    /// REV49-4: the settlement's remaining salary and a payroll run must never pay the same days. A run that is not cancelled or reversed and has an
    /// unblocked line for this person over those days stops the post: a finalized one means the compute is stale, a live one that payroll is still running.
    /// </summary>
    private async Task EnsureSalaryNotInPayrollAsync(Guid org, FinalSettlement row, CancellationToken ct)
    {
        if (SalaryDays(row) is not { } days) return;
        var dead = new[] { PayrollRunStatuses.Cancelled, PayrollRunStatuses.Reversed };
        var run = await Db.PayrollRunLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.EmployeeId == row.EmployeeId && !l.IsBlocked && !dead.Contains(l.Run!.Status)
                && l.Run.PeriodStart <= days.To && l.Run.PeriodEnd >= days.From)
            .Select(l => new { l.Run!.RunNumber, l.Run.Status }).FirstOrDefaultAsync(ct);
        if (run is null) return;
        if (run.Status is PayrollRunStatuses.Finalized or PayrollRunStatuses.Posted or PayrollRunStatuses.Paid)
            throw HrmErrors.Conflict("SETTLEMENT_STALE", $"Payroll run {run.RunNumber} has paid part of the salary on this settlement. Cancel it and compute again.");
        throw HrmErrors.Conflict("SETTLEMENT_PAYROLL_IN_PROGRESS", $"Payroll run {run.RunNumber} ({run.Status}) also pays this person for these days. Recalculate it (the line is then blocked) or cancel it, then post.");
    }

    private async Task ApplyAccrualAsync(HrmCaller caller, FinalSettlement row, CancellationToken ct)
    {
        var outcome = await PostSafelyAsync(() => BuildPostingAsync(caller.OrganizationId, row, ct), ct);
        row.PostingState = outcome.State;
        row.PostingNote = outcome.Note;
        Stamp(row, caller);
        await SaveAsync(ct, "This settlement");
    }

    public async Task<SettlementModel> PayAsync(HrmCaller caller, Guid uuid, LifePayRequest request, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status != SettlementStatuses.Posted) throw HrmErrors.Conflict("SETTLEMENT_NOT_POSTED", "Only a posted settlement can be paid.");
        if (row.PostingState is HrmPostingStates.Failed or HrmPostingStates.Pending) throw HrmErrors.Conflict("SETTLEMENT_NOT_POSTED", "The settlement's posting has not gone through. Retry it first.");
        if (row.Net < 0) throw HrmErrors.Conflict("SETTLEMENT_NOT_PAYABLE", "The employee owes the company on this settlement; there is nothing to pay.");
        if (request.PaidOn == default) throw new BadRequestException("The payment date is required.");
        row.Status = SettlementStatuses.Paid;
        row.PaidOn = request.PaidOn;
        row.PaidFromAccount = HrmValidate.OneOf(request.PaidFromAccount ?? HrmPaymentAccounts.Bank, HrmPaymentAccounts.All, "paidFromAccount");
        row.PaymentReference = HrmValidate.Optional(request.Reference, 100, "Reference");
        if (row.Net > 0) row.PostingState = HrmPostingStates.Pending;
        Stamp(row, caller);
        await SaveAsync(ct, "This settlement"); // RowVersion: of two concurrent payments one gets 409 and posts nothing.
        if (row.Net > 0) await ApplyPaymentAsync(caller, row, ct);
        return Map(row);
    }

    private async Task ApplyPaymentAsync(HrmCaller caller, FinalSettlement row, CancellationToken ct)
    {
        var tx = await BuildPaymentAsync(caller.OrganizationId, row, ct);
        var outcome = await Posting.PostAsync(tx, ct);
        row.PostingState = outcome.State;
        row.PostingNote = outcome.Note;
        Stamp(row, caller);
        await SaveAsync(ct, "This settlement");
    }

    public async Task<SettlementModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status is not (SettlementStatuses.Draft or SettlementStatuses.Approved)) throw HrmErrors.Conflict("SETTLEMENT_NOT_CANCELLABLE", "A posted settlement cannot be cancelled.");
        row.Status = SettlementStatuses.Cancelled;
        Stamp(row, caller);
        await SaveAsync(ct, "This settlement");
        return Map(row);
    }

    /// <summary>Both postings are idempotent by key, so a Paid settlement retries the accrual first, then the payment.</summary>
    public async Task<SettlementModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status is not (SettlementStatuses.Posted or SettlementStatuses.Paid)) throw HrmErrors.Conflict("SETTLEMENT_NOT_POSTED", "This settlement has not been posted.");
        await ApplyAccrualAsync(caller, row, ct);
        if (row.Status == SettlementStatuses.Paid && row.Net > 0 && row.PostingState != HrmPostingStates.Failed) await ApplyPaymentAsync(caller, row, ct);
        return Map(row);
    }
}
