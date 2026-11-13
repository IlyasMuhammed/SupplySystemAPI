using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services.Att;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Pagination;

namespace SMS.Modules.Hrm.Services.Pay;

public interface IPayrollRunService
{
    Task<PaginatedResponse<PayrollRunModel>> ListAsync(HrmCaller caller, string? status, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct = default);
    Task<PayrollRunModel> CreateAsync(HrmCaller caller, PayrollRunCreate request, CancellationToken ct = default);
    Task<PayrollRunModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PaginatedResponse<PayrollRunLineModel>> ListLinesAsync(HrmCaller caller, Guid uuid, string? search, bool blockedOnly, int page, int pageSize, CancellationToken ct = default);
    Task<PayrollRunModel> CalculateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollProgressModel> GetProgressAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollRunModel> ReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollRunModel> ApproveAsync(HrmCaller caller, Guid uuid, bool excludeBlocked, CancellationToken ct = default);
    Task<PayrollRunModel> ReopenAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollPostingReadiness> GetReadinessAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollRunModel> FinalizeAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollRunModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayFile> GetBankFileAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollRunModel> PayAsync(HrmCaller caller, Guid uuid, PayrollPayRequest request, CancellationToken ct = default);
    Task<PayrollRunModel> ReverseAsync(HrmCaller caller, Guid uuid, string? reason, CancellationToken ct = default);
    Task<PayrollRunModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayrollJournalModel> GetJournalAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<PayFile> ExportJournalAsync(HrmCaller caller, Guid uuid, bool excel, CancellationToken ct = default);
    Task<PayrollReconciliationModel> GetReconciliationAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<PayrollHistoryItem>> GetEmployeeHistoryAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default);
}

/// <summary>
/// The run lifecycle (§9): Draft → Calculated → UnderReview → Approved → Finalized → Posted → Paid; Cancelled before Finalized; Reversed from
/// Finalized or Posted. Every move is a read of the status plus a save guarded by RowVersion; the moves that touch money (calculate, finalize,
/// reverse, create, pay) also hold the organization's payroll lock. So two finalizes cannot both win, and only the winner reaches the gateway, after its commit.
/// </summary>
internal sealed class PayrollRunService : HrmServiceBase, IPayrollRunService
{
    internal int BackgroundThreshold { get; init; } = 200;
    private const string NoteSeparator = " | ";

    private static readonly string[] Blockable = [PayrollRunStatuses.Draft, PayrollRunStatuses.Calculated, PayrollRunStatuses.UnderReview, PayrollRunStatuses.Approved];
    private static readonly string[] PostFinalize = [PayrollRunStatuses.Finalized, PayrollRunStatuses.Posted, PayrollRunStatuses.Paid];

    private readonly IHrmScopeService _scope;
    private readonly IHrmEmployeeDirectory _directory;
    private readonly IPayrollCalculationEngine _engine;
    private readonly IPayrollPostingBuilder _builder;
    private readonly IFinancialPostingGateway _gateway;
    private readonly IPayrollPostingReadinessService _readiness;
    private readonly IEnumerable<IPayrollInputProvider> _providers;
    private readonly IAttendancePayInput _attendance;
    private readonly PayrollProgressTracker _progress;
    private readonly IHrmSeeder _seeder;
    private readonly ICurrencyService? _currencies;
    private readonly IOrgCurrencyLookup? _currencyLookup;
    private readonly IFiscalPeriodGuard? _periods;
    private readonly IBackgroundJobClient? _jobs;
    private readonly ILogger<PayrollRunService>? _log;

    public PayrollRunService(HrmDbContext db, IHrmScopeService scope, IHrmEmployeeDirectory directory, IPayrollCalculationEngine engine, IPayrollPostingBuilder builder,
        IFinancialPostingGateway gateway, IPayrollPostingReadinessService readiness, IEnumerable<IPayrollInputProvider> providers, IAttendancePayInput attendance,
        PayrollProgressTracker progress, IHrmSeeder seeder, ICurrencyService? currencies = null, IOrgCurrencyLookup? currencyLookup = null, IFiscalPeriodGuard? periods = null,
        IBackgroundJobClient? jobs = null, ILogger<PayrollRunService>? log = null) : base(db)
    {
        _scope = scope;
        _directory = directory;
        _engine = engine;
        _builder = builder;
        _gateway = gateway;
        _readiness = readiness;
        _providers = providers;
        _attendance = attendance;
        _progress = progress;
        _seeder = seeder;
        _currencies = currencies;
        _currencyLookup = currencyLookup;
        _periods = periods;
        _jobs = jobs;
        _log = log;
    }

    private IQueryable<PayrollRun> Runs(Guid org) => Db.PayrollRuns.IgnoreQueryFilters().Where(r => r.OrganizationId == org);

    private async Task<PayrollRun> RequireAsync(HrmCaller caller, Guid uuid, CancellationToken ct) =>
        await Runs(caller.OrganizationId).FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);

    private static ConflictException Status(PayrollRun run, string action) => HrmErrors.Conflict("PAYROLL_STATUS", $"A run that is {run.Status} cannot {action}.");

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<PayrollRunModel> ModelAsync(PayrollRun run, CancellationToken ct)
    {
        var postings = await Db.PayrollPostings.IgnoreQueryFilters().AsNoTracking().Where(p => p.RunId == run.Id && p.OrganizationId == run.OrganizationId).OrderBy(p => p.Id).ToListAsync(ct);
        Guid? reversalOf = run.ReversalOfRunId is { } id ? await Runs(run.OrganizationId).Where(r => r.Id == id).Select(r => (Guid?)r.Uuid).FirstOrDefaultAsync(ct) : null;
        var model = Map(run, postings, reversalOf);
        model.UntrackedAttendanceEmployees = await Db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(c => c.OrganizationId == run.OrganizationId && c.Code == PayrollCalculationEngine.UntrackedAttendanceCode && c.Line!.RunId == run.Id && !c.Line.IsBlocked, ct);
        if (model.UntrackedAttendanceEmployees > 0)
            model.Warnings.Add($"{model.UntrackedAttendanceEmployees} employee(s) have no attendance recorded in this period and are paid in full. Check them: a failed import looks the same as someone absent all month.");
        return model;
    }

    private static PayrollPostingModel Map(PayrollPosting p) => new()
    {
        Kind = p.Kind, Status = p.Status, PostingDate = p.PostingDate, TotalDebit = p.TotalDebit, TotalCredit = p.TotalCredit, LastError = p.LastError,
        LastAttemptAt = p.LastAttemptAt, Attempts = p.Attempts, TargetResultsJson = p.TargetResultsJson
    };

    private static PayrollRunModel Map(PayrollRun r, IReadOnlyList<PayrollPosting> postings, Guid? reversalOf) => new()
    {
        Uuid = r.Uuid, RunNumber = r.RunNumber, PeriodStart = r.PeriodStart, PeriodEnd = r.PeriodEnd, PayDate = r.PayDate, PayrollGroup = r.PayrollGroup, Status = r.Status,
        CurrencyCode = r.CurrencyCode, TotalEmployees = r.TotalEmployees, BlockedEmployees = r.BlockedEmployees, TotalGross = r.TotalGross, TotalDeductions = r.TotalDeductions,
        TotalNet = r.TotalNet, TotalEmployerCost = r.TotalEmployerCost, CalculatedAt = r.CalculatedAt, ReviewedAt = r.ReviewedAt, ApprovedAt = r.ApprovedAt, FinalizedAt = r.FinalizedAt,
        PostedAt = r.PostedAt, PaidAt = r.PaidAt, PaymentReference = r.PaymentReference, PaidFromAccount = r.PaidFromAccount, PostingDate = r.PostingDate, PostingNote = r.PostingNote,
        CostCenterMode = r.CostCenterMode, ReversalOfRunUuid = reversalOf, ReversalReason = r.ReversalReason,
        Postings = postings.Select(Map).ToList(), AllowedActions = AllowedActions(r, postings)
    };

    private static List<string> AllowedActions(PayrollRun r, IReadOnlyList<PayrollPosting> postings)
    {
        var actions = new List<string>();
        var retryable = postings.Any(p => p.Status is PayrollPostingStatuses.Failed or PayrollPostingStatuses.Pending or PayrollPostingStatuses.Held);
        switch (r.Status)
        {
            case PayrollRunStatuses.Draft: actions.AddRange(["calculate", "cancel"]); break;
            case PayrollRunStatuses.Calculated: actions.AddRange(["calculate", "review", "cancel"]); break;
            case PayrollRunStatuses.UnderReview: actions.AddRange(["approve", "reopen", "cancel"]); break;
            case PayrollRunStatuses.Approved: actions.AddRange(["finalize", "reopen", "cancel"]); break;
            case PayrollRunStatuses.Finalized:
                actions.AddRange(["bank-file", "reverse", "payslips"]);
                if (CanPay(r, postings)) actions.Add("pay");
                break;
            case PayrollRunStatuses.Posted: actions.AddRange(["bank-file", "pay", "reverse", "payslips"]); break;
            case PayrollRunStatuses.Paid: actions.AddRange(["bank-file", "payslips"]); break;
        }
        if (retryable) actions.Add("retry-posting");
        return actions;
    }

    /// <summary>G-19: straight from Finalized only when the run has nowhere to post; otherwise the run must have reached Posted first.</summary>
    private static bool CanPay(PayrollRun r, IReadOnlyList<PayrollPosting> postings) =>
        r.Status == PayrollRunStatuses.Posted
        || (r.Status == PayrollRunStatuses.Finalized && postings.Any(p => p.Kind == PayrollPostingKinds.PayrollRun && p.Status == PayrollPostingStatuses.NoTarget));

    public async Task<PaginatedResponse<PayrollRunModel>> ListAsync(HrmCaller caller, string? status, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, 200);
        var query = Runs(caller.OrganizationId).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = HrmValidate.OneOf(status, PayrollRunStatuses.All, "status");
            query = query.Where(r => r.Status == s);
        }
        if (from is { } f) query = query.Where(r => r.PeriodEnd >= f);
        if (to is { } t) query = query.Where(r => r.PeriodStart <= t);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.PeriodEnd).ThenByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var data = new List<PayrollRunModel>();
        foreach (var row in rows) data.Add(await ModelAsync(row, ct));
        return new PaginatedResponse<PayrollRunModel> { Data = data, TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling(total / (double)pageSize) };
    }

    public async Task<PayrollRunModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Runs(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        return await ModelAsync(row, ct);
    }

    public async Task<PaginatedResponse<PayrollRunLineModel>> ListLinesAsync(HrmCaller caller, Guid uuid, string? search, bool blockedOnly, int page, int pageSize, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var run = await Runs(org).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, 200);
        var scope = await _scope.GetAsync(caller, ct);
        var employees = _scope.Apply(Db.Employees, caller, scope);
        var query = Db.PayrollRunLines.IgnoreQueryFilters().AsNoTracking().Where(l => l.RunId == run.Id && l.OrganizationId == org)
            .Where(l => employees.Any(e => e.Id == l.EmployeeId));
        if (blockedOnly) query = query.Where(l => l.IsBlocked);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(l => l.Employee!.FullName.Contains(term) || l.Employee.EmployeeNumber.Contains(term));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.Include(l => l.Employee).Include(l => l.Components).OrderBy(l => l.Employee!.EmployeeNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PaginatedResponse<PayrollRunLineModel> { Data = rows.Select(MapLine).ToList(), TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling(total / (double)pageSize) };
    }

    private static PayrollRunLineModel MapLine(PayrollRunLine l) => new()
    {
        Uuid = l.Uuid, EmployeeUuid = l.Employee!.Uuid, EmployeeNumber = l.Employee.EmployeeNumber, EmployeeName = l.Employee.FullName, DepartmentName = l.DepartmentName,
        DesignationName = l.DesignationName, CostCenterCode = l.CostCenterCode, WorkingDays = l.WorkingDays, PaidDays = l.PaidDays, WorkedDays = l.WorkedDays, Gross = l.Gross,
        TotalDeductions = l.TotalDeductions, Net = l.Net, TotalEmployerCost = l.TotalEmployerCost, IsBlocked = l.IsBlocked, BlockReason = l.BlockReason, PaymentMethod = l.PaymentMethod,
        AttendanceUntracked = l.Components.Any(c => c.Code == PayrollCalculationEngine.UntrackedAttendanceCode),
        // Split rows of one component read as one line; the NET row is what `net` already says.
        Components = l.Components.Where(c => c.Type != SalaryComponentTypes.Result).OrderBy(c => c.Sequence)
            .GroupBy(c => new { c.Code, c.Sequence }).Select(g => new PayrollLineComponentModel { Code = g.Key.Code, Name = g.First().Name, Type = g.First().Type, Amount = g.Sum(c => c.Amount), Basis = g.First().Basis }).ToList()
    };

    public async Task<IReadOnlyList<PayrollHistoryItem>> GetEmployeeHistoryAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        return await Db.PayrollRunLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == caller.OrganizationId && l.EmployeeId == employee.Id)
            .OrderByDescending(l => l.Run!.PeriodEnd)
            .Select(l => new PayrollHistoryItem { RunUuid = l.Run!.Uuid, RunNumber = l.Run.RunNumber, PeriodStart = l.Run.PeriodStart, PeriodEnd = l.Run.PeriodEnd, Status = l.Run.Status, Gross = l.Gross, TotalDeductions = l.TotalDeductions, Net = l.Net, IsBlocked = l.IsBlocked, LineUuid = l.Uuid, PayslipUuid = l.Payslip != null ? (Guid?)l.Payslip.Uuid : null })
            .ToListAsync(ct);
    }

    // ── create ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PayrollRunModel> CreateAsync(HrmCaller caller, PayrollRunCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var length = request.PeriodEnd.DayNumber - request.PeriodStart.DayNumber + 1;
        if (length is < 28 or > 31) throw HrmErrors.Bad("PAYROLL_PERIOD_INVALID", "A payroll period is one month (28 to 31 days): tax is annualized on that assumption.");
        if (request.PayDate < request.PeriodStart) throw HrmErrors.Bad("PAYROLL_PERIOD_INVALID", "The pay date cannot be before the period starts.");
        var group = request.PayrollGroup?.Trim() ?? string.Empty;
        if (group.Length > 30) throw new BadRequestException("A payroll group cannot be longer than 30 characters.");
        var settings = await _directory.GetSettingsAsync(org, ct);
        var mode = settings.PayrollCostCenterMode;
        var currency = await BaseCurrencyAsync(org, ct);

        PayrollRun? created = null;
        try
        {
            await Db.InTransactionAsync(async () =>
            {
                Db.ChangeTracker.Clear();
                await PayrollLock.AcquireAsync(Db, org, ct);
                // Overlap, not only the same dates: the unique index cannot see two runs paying the same days twice.
                if (await Live(org, group).AnyAsync(r => r.PeriodStart <= request.PeriodEnd && r.PeriodEnd >= request.PeriodStart, ct))
                    throw HrmErrors.Conflict("PAYROLL_RUN_EXISTS", "A run for this group already covers part of this period.");
                created = NewRun(caller, request.PeriodStart, request.PeriodEnd, request.PayDate, group, mode, currency, await NextNumberAsync(org, request.PeriodEnd, group, ct));
                Db.PayrollRuns.Add(created);
                await Db.SaveChangesAsync(ct);
            }, ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            throw HrmErrors.Conflict("PAYROLL_RUN_EXISTS", "A run for this group and period already exists.");
        }
        return await ModelAsync(created!, ct);
    }

    private IQueryable<PayrollRun> Live(Guid org, string group) =>
        Runs(org).Where(r => r.PayrollGroup == group && r.Status != PayrollRunStatuses.Cancelled && r.Status != PayrollRunStatuses.Reversed);

    private PayrollRun NewRun(HrmCaller caller, DateOnly from, DateOnly to, DateOnly payDate, string group, string mode, string currency, string number)
    {
        var run = new PayrollRun
        {
            RunNumber = number, PeriodStart = from, PeriodEnd = to, PayDate = payDate, PayrollGroup = group, CurrencyCode = currency,
            CostCenterMode = mode, Status = PayrollRunStatuses.Draft
        };
        Stamp(run, caller, isNew: true);
        return run;
    }

    /// <summary>PAY-2026-10, with the group, and -R1.. when a cancelled or reversed run holds the number (the number is unique and never reused).</summary>
    private async Task<string> NextNumberAsync(Guid org, DateOnly periodEnd, string group, CancellationToken ct)
    {
        var slug = new string(group.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        var root = $"PAY-{periodEnd:yyyy-MM}" + (slug.Length == 0 ? string.Empty : "-" + (slug.Length > 12 ? slug[..12] : slug));
        var taken = await Runs(org).AsNoTracking().Where(r => r.RunNumber.StartsWith(root)).Select(r => r.RunNumber).ToListAsync(ct);
        if (!taken.Contains(root)) return root;
        for (var n = 1; ; n++)
        {
            var next = $"{root}-R{n}";
            if (!taken.Contains(next)) return next;
        }
    }

    private async Task<string> BaseCurrencyAsync(Guid org, CancellationToken ct)
    {
        try
        {
            if (_currencies is not null && _currencyLookup is not null)
            {
                var id = await _currencies.GetBaseCurrencyIdAsync(org, TransactionDomain.Service, ct);
                if (await _currencyLookup.GetAsync(org, id, ct) is { } info) return info.Code;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "The base currency of organization {Org} could not be read; using the PKR fallback.", org);
        }
        return "PKR"; // A35 D-7 fallback
    }

    // ── calculate ──────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PayrollRunModel> CalculateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var run = await Runs(org).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        if (run.Status != PayrollRunStatuses.Draft && run.Status != PayrollRunStatuses.Calculated) throw Status(run, "be calculated");
        var count = (await _directory.GetEmployedInPeriodAsync(org, run.PeriodStart, run.PeriodEnd, run.PayrollGroup, ct)).Count;

        if (!_progress.TryStart(uuid, count)) throw HrmErrors.Conflict("PAYROLL_CALCULATING", "This run is already being calculated.");
        if (count > BackgroundThreshold && _jobs is not null)
        {
            try
            {
                _jobs.Enqueue<PayrollCalculationJob>(j => j.RunAsync(org, uuid, caller.UserId, caller.UserName));
            }
            catch
            {
                _progress.Finish(uuid, "The calculation could not be queued.");
                throw;
            }
            var model = await ModelAsync(run, ct);
            model.CalculationQueued = true;
            return model;
        }
        try
        {
            await _engine.CalculateRunAsync(caller, uuid, ct);
            _progress.Finish(uuid);
        }
        catch (Exception ex)
        {
            _progress.Finish(uuid, ex is ConflictException ? ex.Message : "The calculation failed.");
            throw;
        }
        Db.ChangeTracker.Clear();
        return await GetAsync(caller, uuid, ct);
    }

    public async Task<PayrollProgressModel> GetProgressAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var run = await Runs(caller.OrganizationId).AsNoTracking().Select(r => new { r.Uuid, r.Status, r.TotalEmployees }).FirstOrDefaultAsync(r => r.Uuid == uuid, ct)
            ?? throw new NotFoundException("Payroll run", uuid);
        var entry = _progress.Get(uuid);
        return entry is null
            ? new PayrollProgressModel { Status = run.Status, Total = run.TotalEmployees, Processed = run.TotalEmployees }
            : new PayrollProgressModel { Status = run.Status, Running = entry.Running, Total = entry.Total, Processed = entry.Processed, Error = entry.Error };
    }

    // ── review / approve / reopen / cancel ─────────────────────────────────────────────────────────────────────────

    public async Task<PayrollRunModel> ReviewAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var run = await RequireAsync(caller, uuid, ct);
        if (run.Status != PayrollRunStatuses.Calculated) throw Status(run, "go to review");
        run.Status = PayrollRunStatuses.UnderReview;
        run.ReviewedAt = DateTime.UtcNow;
        run.ReviewedBy = caller.UserIdOrNull;
        Stamp(run, caller);
        await SaveAsync(ct, "This payroll run");
        return await ModelAsync(run, ct);
    }

    public async Task<PayrollRunModel> ApproveAsync(HrmCaller caller, Guid uuid, bool excludeBlocked, CancellationToken ct = default)
    {
        var run = await RequireAsync(caller, uuid, ct);
        if (run.Status != PayrollRunStatuses.UnderReview) throw Status(run, "be approved");
        if (run.BlockedEmployees > 0 && !excludeBlocked)
            throw HrmErrors.Conflict("PAYROLL_HAS_BLOCKED", $"{run.BlockedEmployees} employee(s) are blocked. Fix them and recalculate, or approve without them.");
        if (run.TotalEmployees - run.BlockedEmployees <= 0) throw HrmErrors.Conflict("PAYROLL_NO_PAYABLE_LINES", "No employee on this run can be paid.");
        run.Status = PayrollRunStatuses.Approved;
        run.ApprovedAt = DateTime.UtcNow;
        run.ApprovedBy = caller.UserIdOrNull;
        Stamp(run, caller);
        await SaveAsync(ct, "This payroll run");
        return await ModelAsync(run, ct);
    }

    public async Task<PayrollRunModel> ReopenAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var run = await RequireAsync(caller, uuid, ct);
        if (run.Status != PayrollRunStatuses.UnderReview && run.Status != PayrollRunStatuses.Approved) throw Status(run, "be reopened");
        run.Status = PayrollRunStatuses.Calculated;
        run.ReviewedAt = null;
        run.ReviewedBy = null;
        run.ApprovedAt = null;
        run.ApprovedBy = null;
        Stamp(run, caller);
        await SaveAsync(ct, "This payroll run");
        return await ModelAsync(run, ct);
    }

    public async Task<PayrollRunModel> CancelAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        PayrollRun? run = null;
        await Db.InTransactionAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await PayrollLock.AcquireAsync(Db, org, ct);
            run = await RequireAsync(caller, uuid, ct);
            if (!Blockable.Contains(run.Status)) throw Status(run, "be cancelled");
            foreach (var provider in _providers) await provider.ReleaseAsync(org, run.Id, ct);
            run.Status = PayrollRunStatuses.Cancelled;
            Stamp(run, caller);
            await SaveAsync(ct, "This payroll run");
        }, ct);
        return await ModelAsync(run!, ct);
    }

    // ── readiness + finalize ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<PayrollPostingReadiness> GetReadinessAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var run = await Runs(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        var readiness = await _readiness.CheckAsync(caller.OrganizationId, run.Id, ct);
        return readiness with { UnverifiedStatutory = await UnverifiedAsync(run, ct) };
    }

    /// <summary>G-21 — the tax table and rates this run's lines read that nobody has marked reviewed. Before a calculation: every row in force.</summary>
    private async Task<IReadOnlyList<UnverifiedStatutoryItem>> UnverifiedAsync(PayrollRun run, CancellationToken ct)
    {
        var org = run.OrganizationId;
        var codes = await Db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Line!.RunId == run.Id && c.OrganizationId == org && !c.Line.IsBlocked).Select(c => c.Code).Distinct().ToListAsync(ct);
        var country = (await _directory.GetSettingsAsync(org, ct)).CountryCode;
        var table = await PayrollStatutoryLookup.TaxTableAsync(Db, org, country, run.PeriodEnd, ct);
        var rates = await PayrollStatutoryLookup.RatesAsync(Db, org, country, run.PeriodEnd, ct);
        var everything = codes.Count == 0;

        var list = new List<UnverifiedStatutoryItem>();
        if (table is { IsVerified: false } && (everything || codes.Contains(PayrollCalculationEngine.TaxCode))) list.Add(new("TaxTable", table.Name));
        var used = codes.Where(PayrollCalculationEngine.StatutoryComponents.ContainsKey).Select(c => PayrollCalculationEngine.StatutoryComponents[c].RateType).ToHashSet();
        foreach (var rate in rates.Values.OrderBy(r => r.RateType, StringComparer.Ordinal))
            if (!rate.IsVerified && (used.Contains(rate.RateType) || (everything && rate.RateType != StatutoryRateTypes.Gratuity)))
                list.Add(new("StatutoryRate", rate.Name));
        return list;
    }

    public async Task<PayrollRunModel> FinalizeAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var snapshot = await Runs(org).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        if (snapshot.Status != PayrollRunStatuses.Approved) throw Status(snapshot, "be finalized");

        // Checks that read other modules run before the lock.
        var readiness = await _readiness.CheckAsync(org, snapshot.Id, ct);
        if (!readiness.IsReady) throw HrmErrors.Conflict("POSTING_READINESS", DescribeReadiness(readiness));
        var settings = await _directory.GetSettingsAsync(org, ct);
        var policy = settings.PostDatePolicy;
        var (postingDate, dateWarning) = await DeterminePostingDateAsync(org, snapshot, policy, readiness.InternalGlActive, ct);

        PayrollRun? run = null;
        PayrollPosting? posting = null;
        await Db.InTransactionAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await PayrollLock.AcquireAsync(Db, org, ct);
            run = await RequireAsync(caller, uuid, ct);
            if (run.Status != PayrollRunStatuses.Approved) throw Status(run, "be finalized");

            var unverified = await UnverifiedAsync(run, ct);
            if (unverified.Count > 0)
                throw HrmErrors.Conflict("PAYROLL_STATUTORY_UNVERIFIED", "Not reviewed: " + string.Join("; ", unverified.Select(u => u.Name)) + ". A qualified payroll consultant must confirm them, then someone with HRM_STATUTORY_VERIFY marks them.");
            await EnsureStatutoryUnchangedAsync(run, settings.CountryCode, ct);
            await ConsumeInputsAsync(run, ct);

            run.Status = PayrollRunStatuses.Finalized;
            run.FinalizedAt = DateTime.UtcNow;
            run.FinalizedBy = caller.UserIdOrNull;
            run.PostingDate = postingDate;
            run.PostingNote = dateWarning;
            Stamp(run, caller);

            posting = new PayrollPosting { RunId = run.Id, Kind = PayrollPostingKinds.PayrollRun, IdempotencyKey = $"{PostingSourceModules.Hrm}:{FinDocType.PayrollRun}:{run.Uuid}", Status = PayrollPostingStatuses.Pending, PostingDate = postingDate };
            Stamp(posting, caller, isNew: true);
            Db.PayrollPostings.Add(posting);
            await SaveAsync(ct, "This payroll run");
        }, ct);

        // Only the transaction that won reaches this line, and only after its commit.
        await PostRunAsync(run!, posting!, ct);
        return await ModelAsync(run!, ct);
    }

    private static string DescribeReadiness(PayrollPostingReadiness r)
    {
        var parts = new List<string>();
        if (r.MissingGlRoles.Count > 0) parts.Add("account roles not assigned: " + string.Join(", ", r.MissingGlRoles));
        if (r.MissingQboAccounts.Count > 0) parts.Add("QuickBooks accounts not mapped: " + string.Join(", ", r.MissingQboAccounts.Select(a => a.Role)));
        if (r.MissingCostCenters.Count > 0) parts.Add("cost centers not mapped to QuickBooks: " + string.Join(", ", r.MissingCostCenters.Select(c => c.Code)));
        if (r.CurrencyMismatch is { } m) parts.Add($"currency {m.OrgCurrency} differs from QuickBooks {m.QboCurrency}");
        return parts.Count == 0 ? "The posting targets are not ready." : string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// G-10. The period check is the GL's: with no active GL there is no period to be closed (an HR-only tenant has no fiscal years at all, and must not be refused).
    /// </summary>
    internal async Task<(DateOnly Date, string? Warning)> DeterminePostingDateAsync(Guid org, PayrollRun run, string policy, bool internalGlActive, CancellationToken ct)
    {
        async Task<bool> OpenAsync(DateOnly d) => !internalGlActive || _periods is null || await _periods.IsPostingAllowedAsync(org, d, ct);

        switch (policy)
        {
            case PayrollPostDatePolicies.PayDate:
                if (!await OpenAsync(run.PayDate)) throw HrmErrors.Conflict("PAYROLL_PERIOD_CLOSED", $"The period of {run.PayDate:yyyy-MM-dd} is closed. Reopen it or change the policy.");
                return (run.PayDate, null);
            case PayrollPostDatePolicies.PeriodEnd:
                if (!await OpenAsync(run.PeriodEnd)) throw HrmErrors.Conflict("PAYROLL_PERIOD_CLOSED", $"The period of {run.PeriodEnd:yyyy-MM-dd} is closed.");
                return (run.PeriodEnd, null);
            default:
                if (await OpenAsync(run.PayDate)) return (run.PayDate, null);
                if (!await OpenAsync(run.PeriodEnd)) throw HrmErrors.Conflict("PAYROLL_PERIOD_CLOSED", "Both the pay date and the period end are in closed periods.");
                return (run.PeriodEnd, $"Pay date {run.PayDate:yyyy-MM-dd} is in a closed period; posted on {run.PeriodEnd:yyyy-MM-dd} instead.");
        }
    }

    /// <summary>
    /// D-PAY-8 — a verified rate may have been edited and verified again after the calculation. The amounts must come from the values that were reviewed:
    /// statutory rows and a plain ITAX row are recomputed from their stored basis; any difference asks for a recalculation.
    /// </summary>
    private async Task EnsureStatutoryUnchangedAsync(PayrollRun run, string country, CancellationToken ct)
    {
        var org = run.OrganizationId;
        var table = await PayrollStatutoryLookup.TaxTableAsync(Db, org, country, run.PeriodEnd, ct);
        var rates = await PayrollStatutoryLookup.RatesAsync(Db, org, country, run.PeriodEnd, ct);
        var rows = await Db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Line!.RunId == run.Id && c.OrganizationId == org && !c.Line.IsBlocked && c.Basis != null)
            .Select(c => new { c.LineId, c.Code, c.Amount, c.Basis, c.Line!.PaidDays }).ToListAsync(ct);
        // An employer contribution split across cost centers is several rows of one figure.
        var figures = rows.GroupBy(r => (r.LineId, r.Code)).Select(g => new { g.Key.Code, Amount = g.Sum(r => r.Amount), g.First().Basis, g.First().PaidDays });
        foreach (var row in figures)
        {
            decimal? expected = null;
            if (row.Code == PayrollCalculationEngine.TaxCode)
            {
                if (table is not null)
                {
                    try { expected = PayrollCalculationEngine.Money(Math.Max(TaxSlabCalculator.Annual(table.Slabs.ToList(), row.Basis!.Value), 0m) / 12m); }
                    catch (SalaryFormulaException) { expected = -1m; }
                }
                else expected = -1m;
            }
            else if (PayrollCalculationEngine.StatutoryComponents.TryGetValue(row.Code, out var s))
            {
                if (rates.TryGetValue(s.RateType, out var rate))
                {
                    var capped = rate.WageCeiling is { } ceiling && row.Basis > ceiling ? ceiling : row.Basis!.Value;
                    var basis = rate.FixedBasis is { } fixedBasis ? (rate.WageCeiling is { } cap && fixedBasis > cap ? cap : fixedBasis) : capped;
                    var percent = s.Employee ? rate.EmployeePercent : rate.EmployerPercent;
                    expected = row.PaidDays > 0m ? PayrollCalculationEngine.Money(basis * percent / 100m) : 0m;
                }
                else expected = -1m;
            }
            if (expected is { } value && value != row.Amount)
                throw HrmErrors.Conflict("PAYROLL_STATUTORY_CHANGED", "A tax table or statutory rate changed after this run was calculated. Reopen and recalculate it.");
        }
    }

    /// <summary>
    /// D-PAY-7 — under the organization lock: what the calculation reserved goes back, each unblocked line asks the providers again and must get what it
    /// snapshotted (anything else: 409, roll back, recalculate), then consumes it. This also frees what blocked lines had reserved, and is why no installment
    /// or adjustment can be deducted by two runs.
    /// </summary>
    private async Task ConsumeInputsAsync(PayrollRun run, CancellationToken ct)
    {
        var org = run.OrganizationId;
        var lines = await Db.PayrollRunLines.IgnoreQueryFilters().AsNoTracking().Include(l => l.Components)
            .Where(l => l.RunId == run.Id && l.OrganizationId == org && !l.IsBlocked).ToListAsync(ct);
        foreach (var provider in _providers) await provider.ReleaseAsync(org, run.Id, ct);
        var attendance = await _attendance.GetManyAsync(org, lines.Select(l => l.EmployeeId).ToList(), run.PeriodStart, run.PeriodEnd, ct);
        foreach (var line in lines)
        {
            var actual = new List<PayrollInput>();
            foreach (var provider in _providers)
                actual.AddRange(await provider.GetInputsAsync(org, run.Id, line.EmployeeId, run.PeriodStart, run.PeriodEnd, ct));
            var now = actual.GroupBy(i => i.ComponentCode.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(i => PayrollCalculationEngine.Money(i.Amount)));
            var then = line.Components.Where(c => c.Sequence >= PayrollCalculationEngine.InputSequenceBase && c.Sequence != int.MaxValue)
                .GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));
            if (now.Count != then.Count || now.Any(kv => !then.TryGetValue(kv.Key, out var v) || v != kv.Value))
                throw HrmErrors.Conflict("PAYROLL_INPUTS_CHANGED", "A loan, advance, reimbursement or adjustment changed after this run was calculated. Reopen and recalculate it.");
            foreach (var provider in _providers) await provider.MarkConsumedAsync(org, run.Id, line.EmployeeId, ct);

            // D-PAY-9: overtime approved after the calculation was not paid by this run: it must not be stamped paid. A line that paid none stamps none.
            var overtimeRow = line.Components.FirstOrDefault(c => c.Code == PayrollCalculationEngine.OvertimeCode && c.Basis != null);
            if (overtimeRow is null) continue;
            var hoursNow = attendance.TryGetValue(line.EmployeeId, out var said) ? PayrollCalculationEngine.Money(said.WeightedOvertimeHours) : 0m;
            if (hoursNow != overtimeRow.Basis)
                throw HrmErrors.Conflict("PAYROLL_INPUTS_CHANGED", "Approved overtime changed after this run was calculated. Reopen and recalculate it.");
            await _attendance.MarkOvertimePaidAsync(org, run.Id, line.EmployeeId, run.PeriodStart, run.PeriodEnd, ct);
        }
    }

    // ── posting (after commit) ─────────────────────────────────────────────────────────────────────────────────────

    private async Task PostRunAsync(PayrollRun run, PayrollPosting posting, CancellationToken ct)
    {
        PostingResult result;
        FinancialTransaction? tx = null;
        try
        {
            tx = await _builder.BuildAsync(run, ct);
            result = await _gateway.PostAsync(tx, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Posting of payroll run {Run} failed.", run.RunNumber);
            result = new PostingResult { Status = PostingStatus.PartialFailure, Message = "The posting could not be sent: " + ex.GetBaseException().Message, SourceDocumentId = run.Uuid, DocumentType = FinDocType.PayrollRun };
        }
        if (tx is not null)
        {
            posting.TotalDebit = tx.Lines.Sum(l => l.Debit);
            posting.TotalCredit = tx.Lines.Sum(l => l.Credit);
        }
        Apply(posting, result);

        // §7.7: Posted once one active target has it; no target at all stays Finalized (D-11).
        var anyPosted = result.TargetResults.Any(t => t.Status == TargetPostingStatus.Posted);
        if (run.Status == PayrollRunStatuses.Finalized && anyPosted)
        {
            run.Status = PayrollRunStatuses.Posted;
            run.PostedAt = DateTime.UtcNow;
        }
        SetNote(run, posting.Status switch
        {
            PayrollPostingStatuses.NoTarget => "No posting required.",
            PayrollPostingStatuses.Posted => null,
            PayrollPostingStatuses.Held => "Held: QuickBooks sync of payroll is switched off for this connection.",
            PayrollPostingStatuses.Pending => "Posting is queued.",
            _ => "Posting failed: " + posting.LastError,
        });
        await SavePostingStateAsync(run, posting, ct);
    }

    private static void SetNote(PayrollRun run, string? status)
    {
        var warning = run.PostingNote?.Split(NoteSeparator)[0];
        if (warning is not null && !warning.StartsWith("Pay date", StringComparison.Ordinal)) warning = null;
        var note = warning is null ? status : status is null ? warning : warning + NoteSeparator + status;
        run.PostingNote = note is { Length: > 500 } ? note[..500] : note;
    }

    /// <summary>The money has already been committed; a lost race on this bookkeeping must not fail the request.</summary>
    private async Task SavePostingStateAsync(PayrollRun run, PayrollPosting posting, CancellationToken ct)
    {
        try
        {
            await Db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _log?.LogInformation(ex, "Payroll run {Run} changed while its posting state was saved.", run.RunNumber);
            await Db.Entry(run).ReloadAsync(ct);
            await Db.Entry(posting).ReloadAsync(ct);
        }
    }

    internal static void Apply(PayrollPosting posting, PostingResult result)
    {
        posting.Attempts++;
        posting.LastAttemptAt = DateTime.UtcNow;
        posting.TargetResultsJson = JsonSerializer.Serialize(result.TargetResults.Select(t => new { target = t.TargetCode, status = t.Status.ToString(), internalId = t.InternalId, externalId = t.ExternalId, error = t.Error }));
        posting.Status = StatusFrom(result, posting.Kind);
        var message = result.Message ?? result.TargetResults.Where(t => t.Error is not null).Select(t => $"{t.TargetCode}: {t.Error}").FirstOrDefault();
        posting.LastError = posting.Status is PayrollPostingStatuses.Posted or PayrollPostingStatuses.NoTarget ? null : message is { Length: > 2000 } ? message[..2000] : message;
    }

    /// <summary>D-11 and §7.7. A failure at one target keeps the row retryable even when another target has it.</summary>
    internal static string StatusFrom(PostingResult result, string kind)
    {
        switch (result.Status)
        {
            case PostingStatus.NoTargetConfigured:
            case PostingStatus.ModuleNotLicensed:
                return PayrollPostingStatuses.NoTarget;
            case PostingStatus.ValidationFailed:
                return PayrollPostingStatuses.Failed;
        }
        var targets = result.TargetResults;
        if (result.Status == PostingStatus.PartialFailure && targets.Count == 0) return PayrollPostingStatuses.Failed;
        if (targets.Any(t => t.Status == TargetPostingStatus.Failed)) return PayrollPostingStatuses.Failed;
        if (targets.Any(t => t.Status == TargetPostingStatus.Posted)) return PayrollPostingStatuses.Posted;
        if (targets.Any(t => t.Status == TargetPostingStatus.Held)) return PayrollPostingStatuses.Held;
        if (targets.Any(t => t.Status == TargetPostingStatus.Queued)) return PayrollPostingStatuses.Pending;
        // Only skipped targets: a void of something that never posted is done; a posting that nowhere took is not.
        if (targets.Count > 0) return kind == PayrollPostingKinds.Void ? PayrollPostingStatuses.Posted : PayrollPostingStatuses.Failed;
        return PayrollPostingStatuses.Posted;
    }

    public async Task<PayrollRunModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var run = await RequireAsync(caller, uuid, ct);
        if (!PostFinalize.Contains(run.Status) && run.Status != PayrollRunStatuses.Reversed) throw Status(run, "be posted");
        var postings = await Db.PayrollPostings.IgnoreQueryFilters().Where(p => p.RunId == run.Id && p.OrganizationId == run.OrganizationId).OrderBy(p => p.Id).ToListAsync(ct);
        foreach (var posting in postings.Where(p => p.Status != PayrollPostingStatuses.Posted && p.Status != PayrollPostingStatuses.Reversed))
        {
            switch (posting.Kind)
            {
                case PayrollPostingKinds.PayrollRun when run.Status != PayrollRunStatuses.Reversed:
                    await PostRunAsync(run, posting, ct);
                    break;
                case PayrollPostingKinds.PayrollPayment when run.Status == PayrollRunStatuses.Paid:
                    await PostPaymentAsync(run, posting, ct);
                    break;
                case PayrollPostingKinds.Void when run.Status == PayrollRunStatuses.Reversed:
                    await PostVoidAsync(caller, run, posting, ct);
                    break;
            }
        }
        return await ModelAsync(run, ct);
    }

    private async Task PostPaymentAsync(PayrollRun run, PayrollPosting posting, CancellationToken ct)
    {
        PostingResult result;
        FinancialTransaction? tx = null;
        try
        {
            tx = await _builder.BuildPaymentAsync(run, posting.PostingDate ?? DateOnly.FromDateTime(run.PaidAt ?? DateTime.UtcNow), ct);
            result = await _gateway.PostAsync(tx, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Posting of the payment of payroll run {Run} failed.", run.RunNumber);
            result = new PostingResult { Status = PostingStatus.PartialFailure, Message = "The posting could not be sent: " + ex.GetBaseException().Message, SourceDocumentId = run.Uuid, DocumentType = FinDocType.PayrollPayment };
        }
        if (tx is not null)
        {
            posting.TotalDebit = tx.Lines.Sum(l => l.Debit);
            posting.TotalCredit = tx.Lines.Sum(l => l.Credit);
        }
        Apply(posting, result);
        await SavePostingStateAsync(run, posting, ct);
    }

    private async Task PostVoidAsync(HrmCaller caller, PayrollRun run, PayrollPosting posting, CancellationToken ct)
    {
        PostingResult result;
        try
        {
            result = await _gateway.VoidAsync(new VoidRequest
            {
                SourceDocumentId = run.Uuid, SourceModule = PostingSourceModules.Hrm, DocumentType = FinDocType.PayrollRun, SourceDocumentNumber = run.RunNumber,
                Reason = run.ReversalReason ?? "Payroll reversal", VoidedBy = caller.UserIdOrNull, VoidDate = (posting.PostingDate ?? HrmValidate.Today).ToDateTime(TimeOnly.MinValue)
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "The void of payroll run {Run} failed.", run.RunNumber);
            result = new PostingResult { Status = PostingStatus.PartialFailure, Message = "The reversal could not be sent: " + ex.GetBaseException().Message, SourceDocumentId = run.Uuid, DocumentType = FinDocType.PayrollRun };
        }
        Apply(posting, result);
        if (posting.Status == PayrollPostingStatuses.Posted)
        {
            var original = await Db.PayrollPostings.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.RunId == run.Id && p.OrganizationId == run.OrganizationId && p.Kind == PayrollPostingKinds.PayrollRun, ct);
            if (original is not null) original.Status = PayrollPostingStatuses.Reversed;
        }
        await SavePostingStateAsync(run, posting, ct);
    }

    // ── pay ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PayFile> GetBankFileAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var run = await Runs(org).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        if (!PostFinalize.Contains(run.Status)) throw HrmErrors.Conflict("PAYROLL_NOT_PAYABLE", "The bank file exists once the run is finalized.");
        // The file holds everyone's account number: a caller limited to part of the organization cannot have it (a partial file would underpay silently).
        if (!(await _scope.GetAsync(caller, ct)).All) throw new ForbiddenException("The bank file needs access to all employees.");

        var lines = await Db.PayrollRunLines.IgnoreQueryFilters().AsNoTracking().Include(l => l.Employee)
            .Where(l => l.RunId == run.Id && l.OrganizationId == org && !l.IsBlocked && l.PaymentMethod == BankPaymentMethods.Bank)
            .OrderBy(l => l.Employee!.EmployeeNumber).ToListAsync(ct);
        var bank = await _directory.GetPrimaryBankDetailsAsync(org, lines.Select(l => l.EmployeeId).ToList(), ct);

        var missing = lines.Where(l => !bank.TryGetValue(l.EmployeeId, out var b) || (b.AccountNumber is null && b.Iban is null)).Select(l => l.Employee!.EmployeeNumber).ToList();
        if (missing.Count > 0) throw HrmErrors.Conflict("PAYROLL_BANK_MISSING", "No active account now for: " + string.Join(", ", missing) + ".");

        var csv = new StringBuilder();
        csv.AppendLine("Employee Name,Bank,Account Number,IBAN,Amount,Reference");
        foreach (var line in lines)
        {
            var b = bank[line.EmployeeId];
            csv.AppendLine(string.Join(",",
                Csv(b.AccountTitle ?? line.Employee!.FullName), Csv(b.BankName), Csv(b.AccountNumber), Csv(b.Iban),
                line.Net.ToString("0.00", CultureInfo.InvariantCulture), Csv(run.RunNumber)));
        }

        // S3 left the system: logged per employee before the bytes go (§1.3).
        foreach (var line in lines)
        {
            var log = new SensitiveAccessLog { EmployeeId = line.EmployeeId, Field = SensitiveFields.BankAccount, Purpose = "Bank file", UserId = caller.UserIdOrNull, UserName = caller.UserName };
            Stamp(log, caller, isNew: true);
            Db.SensitiveAccessLogs.Add(log);
        }
        await Db.SaveChangesAsync(ct);
        return new PayFile { Content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), FileName = $"{run.RunNumber}-bank.csv", ContentType = "text/csv" };
    }

    /// <summary>Quoted, and a leading = + - @ neutralized: a name must not run as a spreadsheet formula.</summary>
    internal static string Csv(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public async Task<PayrollRunModel> PayAsync(HrmCaller caller, Guid uuid, PayrollPayRequest request, CancellationToken ct = default)
    {
        var reference = HrmValidate.Name(request.Reference, 100, "Payment reference");
        var account = HrmValidate.OneOf(request.PaidFromAccount ?? HrmPaymentAccounts.Bank, HrmPaymentAccounts.All, "paidFromAccount");
        // REV49-10: as Finalize does, refuse a payment date in a closed GL period before the run is marked Paid (it used to fail later, in the posting).
        if (_periods is not null)
        {
            var current = await Runs(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
            var glActive = (await _readiness.CheckAsync(caller.OrganizationId, current.Id, ct)).InternalGlActive;
            if (glActive && !await _periods.IsPostingAllowedAsync(caller.OrganizationId, request.PaidOn, ct))
                throw HrmErrors.Conflict("PAYROLL_PERIOD_CLOSED", $"The period of {request.PaidOn:yyyy-MM-dd} is closed. Choose a payment date in an open period.");
        }
        PayrollRun? run = null;
        PayrollPosting? posting = null;
        await Db.InTransactionAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await PayrollLock.AcquireAsync(Db, caller.OrganizationId, ct);
            run = await RequireAsync(caller, uuid, ct);
            var postings = await Db.PayrollPostings.IgnoreQueryFilters().AsNoTracking().Where(p => p.RunId == run.Id && p.OrganizationId == run.OrganizationId).ToListAsync(ct);
            if (!CanPay(run, postings))
                throw HrmErrors.Conflict("PAYROLL_NOT_PAYABLE", run.Status == PayrollRunStatuses.Finalized
                    ? "This run has a posting target and has not reached it yet: retry the posting first."
                    : $"A run that is {run.Status} cannot be paid.");
            run.Status = PayrollRunStatuses.Paid;
            run.PaidAt = DateTime.UtcNow;
            run.PaidBy = caller.UserIdOrNull;
            run.PaymentReference = reference;
            run.PaidFromAccount = account;
            Stamp(run, caller);
            posting = new PayrollPosting { RunId = run.Id, Kind = PayrollPostingKinds.PayrollPayment, IdempotencyKey = $"{PostingSourceModules.Hrm}:{FinDocType.PayrollPayment}:{run.Uuid}", Status = PayrollPostingStatuses.Pending, PostingDate = request.PaidOn };
            Stamp(posting, caller, isNew: true);
            Db.PayrollPostings.Add(posting);
            await SaveAsync(ct, "This payroll run");
        }, ct);
        await PostPaymentAsync(run!, posting!, ct);
        return await ModelAsync(run!, ct);
    }

    // ── reverse ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PayrollRunModel> ReverseAsync(HrmCaller caller, Guid uuid, string? reason, CancellationToken ct = default)
    {
        var why = HrmValidate.Name(reason, 500, "Reason");
        var org = caller.OrganizationId;
        PayrollRun? run = null;
        PayrollPosting? voidRow = null;
        PayrollRun? correction = null;
        await Db.InTransactionAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            voidRow = null;
            await PayrollLock.AcquireAsync(Db, org, ct);
            run = await RequireAsync(caller, uuid, ct);
            if (run.Status == PayrollRunStatuses.Paid) throw HrmErrors.Conflict("PAYROLL_ALREADY_PAID", "A paid run cannot be reversed: the money has left the bank.");
            if (run.Status != PayrollRunStatuses.Finalized && run.Status != PayrollRunStatuses.Posted) throw Status(run, "be reversed");

            foreach (var provider in _providers) await provider.ReleaseAsync(org, run.Id, ct);
            await _attendance.ReleaseOvertimeAsync(org, run.Id, ct);

            var original = await Db.PayrollPostings.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.RunId == run.Id && p.OrganizationId == org && p.Kind == PayrollPostingKinds.PayrollRun, ct);
            if (original is { Status: not PayrollPostingStatuses.NoTarget })
            {
                voidRow = new PayrollPosting { RunId = run.Id, Kind = PayrollPostingKinds.Void, IdempotencyKey = original.IdempotencyKey + ":void", Status = PayrollPostingStatuses.Pending, PostingDate = HrmValidate.Today, TotalDebit = original.TotalDebit, TotalCredit = original.TotalCredit };
                Stamp(voidRow, caller, isNew: true);
                Db.PayrollPostings.Add(voidRow);
            }
            run.Status = PayrollRunStatuses.Reversed;
            run.ReversalReason = why;
            Stamp(run, caller);
            await SaveAsync(ct, "This payroll run"); // first: the filtered unique index must see this run as Reversed before the correction exists

            // A-27: the original moves to Reversed, the correction is a new Draft pointing at it.
            correction = NewRun(caller, run.PeriodStart, run.PeriodEnd, run.PayDate, run.PayrollGroup, (await _directory.GetSettingsAsync(org, ct)).PayrollCostCenterMode, run.CurrencyCode,
                await NextNumberAsync(org, run.PeriodEnd, run.PayrollGroup, ct));
            correction.ReversalOfRunId = run.Id;
            Db.PayrollRuns.Add(correction);
            await Db.SaveChangesAsync(ct);
        }, ct);

        if (voidRow is not null) await PostVoidAsync(caller, run!, voidRow, ct);
        var model = await ModelAsync(run!, ct);
        model.CorrectionRunUuid = correction!.Uuid;
        return model;
    }

    // ── journal, export, reconciliation ────────────────────────────────────────────────────────────────────────────

    public async Task<PayrollJournalModel> GetJournalAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var run = await Runs(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        var tx = await _builder.BuildAsync(run, ct);
        return new PayrollJournalModel
        {
            RunNumber = run.RunNumber, PostingDate = run.PostingDate, CurrencyCode = run.CurrencyCode, Description = tx.Description,
            TotalDebit = tx.Lines.Sum(l => l.Debit), TotalCredit = tx.Lines.Sum(l => l.Credit),
            Lines = tx.Lines.Select(l => new PayrollJournalLineModel { AccountRole = l.AccountRole ?? string.Empty, AccountCode = l.AccountCode, CostCenter = l.CostCenter, Debit = l.Debit, Credit = l.Credit, Description = l.Label }).ToList()
        };
    }

    public async Task<PayFile> ExportJournalAsync(HrmCaller caller, Guid uuid, bool excel, CancellationToken ct = default)
    {
        var run = await Runs(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        if (!PostFinalize.Contains(run.Status)) throw HrmErrors.Conflict("PAYROLL_NOT_FINALIZED", "The journal export exists once the run is finalized.");
        var journal = await GetJournalAsync(caller, uuid, ct);
        string[] headers = ["Account role", "Account code", "Cost center", "Debit", "Credit", "Description"];
        if (!excel)
        {
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", headers));
            foreach (var l in journal.Lines)
                csv.AppendLine(string.Join(",", Csv(l.AccountRole), Csv(l.AccountCode), Csv(l.CostCenter), l.Debit.ToString("0.00", CultureInfo.InvariantCulture), l.Credit.ToString("0.00", CultureInfo.InvariantCulture), Csv(l.Description)));
            return new PayFile { Content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), FileName = $"{run.RunNumber}-journal.csv", ContentType = "text/csv" };
        }
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Journal");
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var l in journal.Lines)
        {
            sheet.Cell(row, 1).Value = l.AccountRole;
            sheet.Cell(row, 2).Value = l.AccountCode ?? string.Empty;
            sheet.Cell(row, 3).Value = l.CostCenter ?? string.Empty;
            sheet.Cell(row, 4).Value = l.Debit;
            sheet.Cell(row, 5).Value = l.Credit;
            sheet.Cell(row, 6).Value = l.Description ?? string.Empty;
            row++;
        }
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return new PayFile { Content = stream.ToArray(), FileName = $"{run.RunNumber}-journal.xlsx", ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };
    }

    public async Task<PayrollReconciliationModel> GetReconciliationAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var run = await Runs(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Payroll run", uuid);
        var tx = await _builder.BuildAsync(run, ct);
        var postings = await Db.PayrollPostings.IgnoreQueryFilters().AsNoTracking().Where(p => p.RunId == run.Id && p.OrganizationId == run.OrganizationId).OrderBy(p => p.Id).ToListAsync(ct);
        var debit = tx.Lines.Sum(l => l.Debit);
        var credit = tx.Lines.Sum(l => l.Credit);
        return new PayrollReconciliationModel
        {
            RunNumber = run.RunNumber, RunGross = run.TotalGross, RunDeductions = run.TotalDeductions, RunNet = run.TotalNet, RunEmployerCost = run.TotalEmployerCost,
            JournalDebit = debit, JournalCredit = credit, Balanced = debit == credit, Postings = postings.Select(Map).ToList()
        };
    }
}

/// <summary>Null default of POST's service. It answers the GL half through the existing seam, so a host without POST's work still refuses an unmapped role.</summary>
internal sealed class DefaultPayrollPostingReadiness : IPayrollPostingReadinessService
{
    private readonly HrmDbContext _db;
    private readonly IPostingRoleReadiness _roles;

    public DefaultPayrollPostingReadiness(HrmDbContext db, IPostingRoleReadiness roles)
    {
        _db = db;
        _roles = roles;
    }

    public async Task<PayrollPostingReadiness> CheckAsync(Guid organizationId, int runId, CancellationToken ct = default)
    {
        var rows = await _db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Line!.RunId == runId && !c.Line.IsBlocked)
            .Select(c => new { c.PostingRole, c.ExpenseRole }).Distinct().ToListAsync(ct);
        var roles = rows.SelectMany(r => new[] { r.PostingRole, r.ExpenseRole }).Where(r => r is not null).Select(r => r!)
            .Append(PayrollRoles.SalaryPayable).Append(PayrollRoles.Bank).Distinct().ToList();
        var missing = await _roles.MissingRolesAsync(roles, ct);
        return new PayrollPostingReadiness
        {
            IsReady = missing.Count == 0, NoTarget = missing.Count == 0, InternalGlActive = missing.Count > 0,
            InternalGlReady = missing.Count == 0, MissingGlRoles = missing
        };
    }
}
