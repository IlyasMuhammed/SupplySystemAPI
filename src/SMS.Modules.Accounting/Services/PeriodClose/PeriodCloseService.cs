using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 C10 (§13, D-11, D-18, API-CONTRACT §6) — the period close state machine and the year-end close
/// (api/finance/period-close, api/finance/year-end-close). Period transitions follow <see cref="PeriodCloseRules"/>; the
/// year-end closing entry is written and posted through the journal engine (<see cref="JournalEntryWriter"/>: journal type
/// CLOSE, IsClosingEntry, key yearclose:{year uuid}) — journal rows are never written here.
/// Permissions (D-15): reads GL_PERIOD_CLOSE_VIEW (or EXECUTE/ADMIN); lock / adjustment-only / reopen GL_PERIOD_CLOSE_EXECUTE
/// (or ADMIN); final close and the year-end close GL_PERIOD_CLOSE_ADMIN.
/// </summary>
public interface IPeriodCloseService
{
    /// <summary>Every period of the year with its posted / draft entry counts and allowed actions; the year-close pre-checks.</summary>
    Task<PeriodCloseOverviewModel> GetPeriodStatusesAsync(AccountingCaller caller, Guid fiscalYearUuid, CancellationToken ct = default);

    /// <summary>Open → AdjustmentOnly / Locked, AdjustmentOnly → Locked (BR-PC-001: no drafts).</summary>
    Task<PeriodCloseResultModel> ClosePeriodAsync(AccountingCaller caller, Guid periodUuid, string? targetStatus, CancellationToken ct = default);

    /// <summary>Locked / AdjustmentOnly → Open. A Closed period cannot be reopened (BR-PC-002).</summary>
    Task<PeriodCloseResultModel> ReopenPeriodAsync(AccountingCaller caller, Guid periodUuid, CancellationToken ct = default);

    /// <summary>Locked → Closed, irreversible (BR-PC-003).</summary>
    Task<PeriodCloseResultModel> FinalClosePeriodAsync(AccountingCaller caller, Guid periodUuid, CancellationToken ct = default);

    /// <summary>TS-PC-009 — the closing entry <see cref="CloseYearAsync"/> would post, with the failing pre-checks.</summary>
    Task<YearEndPreviewModel> PreviewYearEndCloseAsync(AccountingCaller caller, Guid fiscalYearUuid, Guid? retainedEarningsAccountUuid,
        CancellationToken ct = default);

    /// <summary>§13.4 — pre-checks, closing entry (Revenue Dr / Expense Cr / retained earnings) auto-posted, year closed, all
    /// periods Closed, FiscalYearClosedEvent. A second run returns the existing result (BR-PC-009, TS-PC-008).</summary>
    Task<YearEndCloseResultModel> CloseYearAsync(AccountingCaller caller, Guid fiscalYearUuid, Guid? retainedEarningsAccountUuid,
        CancellationToken ct = default);
}

/// <summary>
/// A47 D-11 — the fiscal period transitions: Open → AdjustmentOnly | Locked; AdjustmentOnly → Open | Locked; Locked → Open |
/// Closed; Closed → nothing. Shared rule (the A42 PUT fiscal-periods/{uuid} keeps its own A42 transitions, SCHEMA-REQUESTS SR-RPT-1).
/// </summary>
public static class PeriodCloseRules
{
    public static IReadOnlyList<string> AllowedFrom(string status) => status switch
    {
        PeriodStatuses.Open           => [PeriodStatuses.AdjustmentOnly, PeriodStatuses.Locked],
        PeriodStatuses.AdjustmentOnly => [PeriodStatuses.Open, PeriodStatuses.Locked],
        PeriodStatuses.Locked         => [PeriodStatuses.Open, PeriodStatuses.Closed],
        _                             => [],
    };

    public static bool IsAllowed(string from, string to) => AllowedFrom(from).Contains(to);

    /// <summary>BR-PC-001 / contract §6 — moving to these needs a period without drafts.</summary>
    public static bool NeedsNoDrafts(string to) => to is PeriodStatuses.AdjustmentOnly or PeriodStatuses.Locked or PeriodStatuses.Closed;

    /// <summary>The contract's allowedActions of a period in this status, for a caller who may execute / administer.</summary>
    public static List<string> Actions(string status, bool canExecute, bool canAdminister)
    {
        var actions = new List<string>();
        foreach (var to in AllowedFrom(status))
        {
            if (to == PeriodStatuses.Closed) { if (canAdminister) actions.Add(PeriodCloseActions.FinalClose); continue; }
            if (!canExecute) continue;
            actions.Add(to switch
            {
                PeriodStatuses.Locked         => PeriodCloseActions.Lock,
                PeriodStatuses.AdjustmentOnly => PeriodCloseActions.AdjustmentOnly,
                _                             => PeriodCloseActions.Reopen,
            });
        }
        return actions;
    }
}

/// <summary>API-CONTRACT §6 — PC-001..PC-010 (travel as result.errorCode through [JournalErrorFilter]).</summary>
public static class PeriodCloseErrorCodes
{
    public const string PeriodDrafts = "PC-001", ClosedNoReopen = "PC-002", OnlyLockedFinal = "PC-003", Transition = "PC-004",
        AllPeriodsLocked = "PC-005", Period13 = "PC-006", YearDrafts = "PC-007", RetainedEarningsInvalid = "PC-008",
        RetainedEarningsMissing = "PC-009", YearClosed = "PC-010";
}

/// <summary>API-CONTRACT §6 — the exact texts.</summary>
public static class PeriodCloseMessages
{
    public const string NoViewPermission   = "You do not have permission to view the period close.";
    public const string NoPermission       = "You do not have permission to close fiscal periods.";
    public const string NoAdminPermission  = "You do not have permission to final-close periods or close the fiscal year.";
    public const string YearNotFound       = "Fiscal year not found.";
    public const string PeriodNotFound     = "Fiscal period not found.";
    public const string TargetInvalid      = "Target status must be Locked or AdjustmentOnly.";
    public const string ClosedNoReopen     = "Closed periods cannot be reopened";
    public const string OnlyLockedFinal    = "Only a Locked period can be closed finally";
    public const string AllPeriodsLocked   = "All periods must be Locked or Closed";
    public const string Period13           = "Period 13 must be Open or AdjustmentOnly";
    public const string RetainedEarnings   = "Retained earnings account must be an active Level 4 Equity account";
    public const string NoRetainedEarnings = "Choose a retained earnings account, or set the DEFAULT_RETAINED_EARNINGS default";
    public const string YearClosed         = "Fiscal year is closed";

    /// <summary>PC-001 — TS-PC-002 "Period has 2 unposted draft entries".</summary>
    public static string PeriodDrafts(int n) => $"Period has {n} unposted draft entries";
    /// <summary>PC-004.</summary>
    public static string Transition(string from, string to) => $"Cannot change a {from} period to {to}";
    /// <summary>PC-007.</summary>
    public static string YearDrafts(int n) => $"The fiscal year has {n} unposted draft entries";
}

internal sealed class PeriodCloseService : IPeriodCloseService
{
    /// <summary>D-RPT-7 — one closing entry carries at most this many Revenue/Expense lines (+ 1 retained earnings line = JournalLimits.MaxLines).</summary>
    internal const int MaxClosingLinesPerEntry = JournalLimits.MaxLines - 1;

    private readonly AccountingDbContext _db;
    private readonly JournalEntryWriter _writer;
    private readonly JournalTypeResolver _types;
    private readonly IAccountingEventPublisher _events;
    private readonly ISetupStatusService _setup;
    private readonly TimeProvider _clock;

    public PeriodCloseService(AccountingDbContext db, JournalEntryWriter writer, JournalTypeResolver types, IAccountingEventPublisher events,
        ISetupStatusService setup, TimeProvider? clock = null)
    {
        _db = db;
        _writer = writer;
        _types = types;
        _events = events;
        _setup = setup;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private sealed record Blocker(string Code, string Message, bool IsValidation)
    {
        public Exception ToException() => IsValidation
            ? new JournalValidationException(Code, Message)
            : new JournalConflictException(Code, Message);
    }

    // ── status ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PeriodCloseOverviewModel> GetPeriodStatusesAsync(AccountingCaller caller, Guid fiscalYearUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanViewPeriodClose, PeriodCloseMessages.NoViewPermission);
        var org = caller.OrganizationId;
        var year = await LoadYearAsync(org, fiscalYearUuid, track: false, ct);
        var counts = await EntryCountsAsync(year, ct);
        var closing = await ClosingEntriesAsync(org, year.Uuid, ct);
        var re = await DefaultRetainedEarningsAsync(org, ct);

        var model = new PeriodCloseOverviewModel
        {
            FiscalYear   = YearModel(year),
            ClosingEntry = closing.Select(Ref).FirstOrDefault(),
            DefaultRetainedEarningsAccount = re is null ? null : AccountRef(re),
            Periods      = year.Periods.OrderBy(p => p.PeriodNumber).Select(p => StatusModel(caller, year, p, counts)).ToList(),
        };
        model.YearCloseBlockers = Blockers(year, counts.Values.Sum(c => c.Drafts), re, explicitAccount: false).Select(b => b.Message).ToList();
        model.CanCloseYear = model.YearCloseBlockers.Count == 0;
        return model;
    }

    // ── period transitions (D-11) ──────────────────────────────────────────────────────────────────────────────────

    public async Task<PeriodCloseResultModel> ClosePeriodAsync(AccountingCaller caller, Guid periodUuid, string? targetStatus, CancellationToken ct = default)
    {
        caller.Require(caller.CanExecutePeriodClose || caller.CanAdministerPeriodClose, PeriodCloseMessages.NoPermission);
        var target = string.IsNullOrWhiteSpace(targetStatus)
            ? PeriodStatuses.Locked
            : new[] { PeriodStatuses.Locked, PeriodStatuses.AdjustmentOnly }.FirstOrDefault(s => string.Equals(s, targetStatus.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new BadRequestException(PeriodCloseMessages.TargetInvalid);
        return await TransitionAsync(caller, periodUuid, target, _ => null, ct);
    }

    public async Task<PeriodCloseResultModel> ReopenPeriodAsync(AccountingCaller caller, Guid periodUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanExecutePeriodClose || caller.CanAdministerPeriodClose, PeriodCloseMessages.NoPermission);
        return await TransitionAsync(caller, periodUuid, PeriodStatuses.Open, from => from == PeriodStatuses.Closed
            ? new Blocker(PeriodCloseErrorCodes.ClosedNoReopen, PeriodCloseMessages.ClosedNoReopen, false) : null, ct);
    }

    public async Task<PeriodCloseResultModel> FinalClosePeriodAsync(AccountingCaller caller, Guid periodUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanAdministerPeriodClose, PeriodCloseMessages.NoAdminPermission);
        return await TransitionAsync(caller, periodUuid, PeriodStatuses.Closed, from => from != PeriodStatuses.Locked
            ? new Blocker(PeriodCloseErrorCodes.OnlyLockedFinal, PeriodCloseMessages.OnlyLockedFinal, false) : null, ct);
    }

    /// <summary>One D-11 transition; <paramref name="refusal"/> is the action's own refusal (checked before the shared rule, PC-004).</summary>
    private async Task<PeriodCloseResultModel> TransitionAsync(AccountingCaller caller, Guid periodUuid, string target, Func<string, Blocker?> refusal,
        CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var yearId = await _db.FiscalPeriods.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && p.Uuid == periodUuid).Select(p => (int?)p.FiscalYearId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(PeriodCloseMessages.PeriodNotFound);
        FiscalYear year = null!;
        FiscalPeriod period = null!;
        var from = string.Empty;

        // REV-11 — the checks (year, transition, BR-PC-001 drafts) and the status change in one transaction under the
        // organization's period lock (Exclusive), on fresh rows; engine writers taking it Shared cannot add a draft in between.
        await _db.InTransactionAsync(async () =>
        {
            await GlPeriodLock.AcquireAsync(_db, org, exclusive: true, ct);
            year = await _db.FiscalYears.IgnoreQueryFilters().Include(y => y.Periods).SingleAsync(y => y.OrganizationId == org && y.Id == yearId, ct);
            await ReloadAsync(year, ct);
            period = year.Periods.Single(p => p.Uuid == periodUuid);
            if (year.IsClosed) throw new JournalConflictException(PeriodCloseErrorCodes.YearClosed, PeriodCloseMessages.YearClosed); // PC-010

            from = period.Status;
            if (refusal(from) is { } own) throw own.ToException();
            if (!PeriodCloseRules.IsAllowed(from, target))
                throw new JournalConflictException(PeriodCloseErrorCodes.Transition, PeriodCloseMessages.Transition(from, target));
            if (PeriodCloseRules.NeedsNoDrafts(target))
            {
                var drafts = await DraftCountAsync(period, ct);
                if (drafts > 0) throw new JournalConflictException(PeriodCloseErrorCodes.PeriodDrafts, PeriodCloseMessages.PeriodDrafts(drafts)); // BR-PC-001
            }

            period.Status = target;
            period.ModifiedBy = caller.UserIdOrNull;
            await _db.SaveChangesAsync(ct);
        }, ct);

        await _events.PublishAsync(new FiscalPeriodStatusChangedEvent(org, period.Uuid, period.PeriodName, from, target,
            caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);

        var counts = await EntryCountsAsync(year, ct);
        return new PeriodCloseResultModel { Period = StatusModel(caller, year, period, counts), OldStatus = from, NewStatus = target };
    }

    // ── year-end close (§13.4, D-18) ───────────────────────────────────────────────────────────────────────────────

    public async Task<YearEndPreviewModel> PreviewYearEndCloseAsync(AccountingCaller caller, Guid fiscalYearUuid, Guid? retainedEarningsAccountUuid,
        CancellationToken ct = default)
    {
        caller.Require(caller.CanViewPeriodClose, PeriodCloseMessages.NoViewPermission);
        var org = caller.OrganizationId;
        var year = await LoadYearAsync(org, fiscalYearUuid, track: false, ct);
        var preview = new YearEndPreviewModel
        {
            FiscalYear   = YearModel(year),
            EntryDate    = year.EndDate,
            PeriodNumber = year.Periods.Any(p => p.PeriodNumber == FiscalYearService.AdjustmentPeriodNumber) ? FiscalYearService.AdjustmentPeriodNumber : 12,
        };

        var existing = await ClosingEntriesAsync(org, year.Uuid, ct);
        if (existing.Count > 0)
        {
            var done = await ExistingResultAsync(year, existing, ct);
            preview.ExistingClosingEntry = done.ClosingEntry;
            preview.RetainedEarningsAccount = done.RetainedEarningsAccount;
            preview.TotalRevenue = done.TotalRevenueClosing;
            preview.TotalExpenses = done.TotalExpenseClosing;
            preview.NetIncome = done.NetIncomeTransferred;
            preview.Lines = await LinesOfEntriesAsync(org, existing.Select(e => e.Id).ToList(), ct);
            preview.Blockers = [PeriodCloseMessages.YearClosed];
            return preview;
        }

        var re = retainedEarningsAccountUuid is { } u ? await AccountByUuidAsync(org, u, ct) : await DefaultRetainedEarningsAsync(org, ct);
        var plan = await PlanAsync(year, re, ct);
        preview.RetainedEarningsAccount = re is null ? null : AccountRef(re);
        preview.TotalRevenue = plan.Revenue;
        preview.TotalExpenses = plan.Expenses;
        preview.NetIncome = plan.NetIncome;
        preview.Lines = plan.Lines;
        preview.Blockers = Blockers(year, await YearDraftCountAsync(year, ct), re, retainedEarningsAccountUuid is not null).Select(b => b.Message).ToList();
        preview.CanExecute = preview.Blockers.Count == 0;
        return preview;
    }

    public async Task<YearEndCloseResultModel> CloseYearAsync(AccountingCaller caller, Guid fiscalYearUuid, Guid? retainedEarningsAccountUuid,
        CancellationToken ct = default)
    {
        caller.Require(caller.CanAdministerPeriodClose, PeriodCloseMessages.NoAdminPermission);
        var org = caller.OrganizationId;
        await LoadYearAsync(org, fiscalYearUuid, track: false, ct); // 404 before any transaction

        YearEndCloseResultModel? already = null;
        FiscalYear year = null!;
        Plan plan = null!;
        AccountInfo re = null!;
        JournalType closingType = null!;
        var written = new List<JournalEntry>();

        try
        {
            await _db.InTransactionAsync(async () =>
            {
                written.Clear();
                already = null;
                // REV-5 — the checks, the plan and the writes run under the organization's period lock (Exclusive), on fresh rows.
                await GlPeriodLock.AcquireAsync(_db, org, exclusive: true, ct);
                year = await LoadYearAsync(org, fiscalYearUuid, track: true, ct);
                await ReloadAsync(year, ct);

                // BR-PC-009 / TS-PC-008 — closed already: the existing result, alreadyClosed (D-RPT-8: also a year closed without a
                // closing entry — nothing to transfer, or the A42 close).
                var existing = await ClosingEntriesAsync(org, year.Uuid, ct);
                if (existing.Count > 0 || year.IsClosed)
                {
                    already = await ExistingResultAsync(year, existing, ct);
                    return;
                }

                // 1. Pre-checks (BR-PC-004, BR-PC-008, D-18); the first failing one answers (the transaction rolls back).
                var account = retainedEarningsAccountUuid is { } u ? await AccountByUuidAsync(org, u, ct) : await DefaultRetainedEarningsAsync(org, ct);
                var blockers = Blockers(year, await YearDraftCountAsync(year, ct), account, retainedEarningsAccountUuid is not null);
                if (blockers.Count > 0) throw blockers[0].ToException();
                re = account!;

                // 2. The year's P&L balances → the closing lines.
                plan = await PlanAsync(year, re, ct);
                closingType = await _types.ByCodeAsync(org, SystemJournalTypes.Closing, ct);
                var key = JournalKeys.YearClose(year.Uuid);
                var hasPeriod13 = year.Periods.Any(p => p.PeriodNumber == FiscalYearService.AdjustmentPeriodNumber);

                // 3 + 4. One closing entry per ≤ 499 P&L lines (+ its retained earnings line), auto-posted by the engine, dated the
                // year end: period 13 (a closing entry prefers it), else period 12 — Locked, so the period status check is skipped
                // there (SkipPeriodCheck; a closed fiscal year is still refused by the engine).
                for (var i = 0; i < plan.Chunks.Count; i++)
                {
                    var draft = new JournalDraft
                    {
                        OrganizationId  = org,
                        JournalTypeId   = closingType.Id,
                        EntryDate       = year.EndDate,
                        Reference       = year.FiscalYearName,
                        Description     = $"Year-end close {year.FiscalYearName}: Revenue and Expense to retained earnings" +
                                          (plan.Chunks.Count > 1 ? $" ({i + 1} of {plan.Chunks.Count})" : ""),
                        EntryType       = JournalEntryTypes.YearEndClose,
                        IdempotencyKey  = i == 0 ? key : $"{key}:{i + 1}",
                        IsClosingEntry  = true,
                        AmountsInBase   = true,
                        SkipPeriodCheck = !hasPeriod13,
                        UserId          = caller.UserIdOrNull,
                        UserName        = caller.UserName,
                        Lines           = plan.Chunks[i],
                    };
                    var result = await _writer.CreateAsync(draft, post: true, ct);
                    await _db.SaveChangesAsync(ct);
                    written.Add(result.Entry);
                }

                // 5 + 6. The year is closed, every period Closed.
                year.IsClosed = true;
                year.ClosedAt = Now;
                year.ClosedBy = caller.UserIdOrNull;
                year.ModifiedBy = caller.UserIdOrNull;
                foreach (var p in year.Periods)
                {
                    p.Status = PeriodStatuses.Closed;
                    p.ModifiedBy = caller.UserIdOrNull;
                }
                await _db.SaveChangesAsync(ct);
            }, ct);
        }
        catch (DbUpdateException)
        {
            // Belt and braces (the lock serializes closes): a concurrent close won the yearclose: key (filtered unique index, D-5).
            _db.ChangeTracker.Clear();
            var winner = await ClosingEntriesAsync(org, fiscalYearUuid, ct);
            if (winner.Count == 0) throw;
            return await ExistingResultAsync(await LoadYearAsync(org, fiscalYearUuid, track: false, ct), winner, ct);
        }
        if (already is not null) return already;

        // 7. Events after the commit (A42 D-20).
        foreach (var e in written)
            await _events.PublishAsync(new JournalEntryPostedEvent(org, e.Uuid, e.EntryNumber, closingType.Code, e.TotalDebit, e.EntryDate,
                caller.UserIdOrNull, caller.UserName), ct);
        await _events.PublishAsync(new FiscalYearClosedEvent(org, year.Uuid, year.FiscalYearName, written.FirstOrDefault()?.EntryNumber,
            plan.NetIncome, caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);

        var refs = written.Select(Ref).ToList();
        return new YearEndCloseResultModel
        {
            FiscalYear              = YearModel(year),
            ClosingEntry            = refs.FirstOrDefault(),
            ClosingEntries          = refs,
            TotalRevenueClosing     = plan.Revenue,
            TotalExpenseClosing     = plan.Expenses,
            NetIncomeTransferred    = plan.NetIncome,
            AlreadyClosed           = false,
            RetainedEarningsAccount = AccountRef(re),
        };
    }

    /// <summary>A tracked year (and its periods) re-read from the database — the scope may hold an older copy (REV-5/REV-11).</summary>
    private async Task ReloadAsync(FiscalYear year, CancellationToken ct)
    {
        await _db.Entry(year).ReloadAsync(ct);
        foreach (var p in year.Periods) await _db.Entry(p).ReloadAsync(ct);
    }

    // ── the closing entry ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed record AccountInfo(int Id, Guid Uuid, string AccountCode, string AccountName, string RootType, bool IsGroup, byte Level, bool IsActive);

    private sealed record Plan(decimal Revenue, decimal Expenses, List<YearEndPreviewLineModel> Lines, List<List<JournalDraftLine>> Chunks)
    {
        public decimal NetIncome => Revenue - Expenses;
    }

    /// <summary>
    /// §13.4 steps 2–3 — every Revenue/Expense leaf's balance over the year's dates (Posted + Reversed lines, D-RPT-1), closed
    /// by the opposite amount; the retained earnings line takes the difference (credit = profit, debit = loss, none at 0).
    /// D-RPT-5 — adjustment entries flagged isClosingEntry (manual entries posted into period 13 / an AdjustmentOnly period)
    /// are part of the year's P&amp;L and are counted; only earlier year-close entries (key yearclose:) are left out.
    /// </summary>
    private async Task<Plan> PlanAsync(FiscalYear year, AccountInfo? re, CancellationToken ct)
    {
        var org = year.OrganizationId;
        var plAccounts = _db.ChartOfAccounts.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && !a.IsGroup && (a.RootType == RootTypes.Revenue || a.RootType == RootTypes.Expense))
            .Select(a => a.Id);
        var sums = await TrialBalanceService.PostedLines(_db, org, includeClosingEntries: true, null, null)
            .Where(x => x.EntryDate >= year.StartDate && x.EntryDate <= year.EndDate && plAccounts.Contains(x.AccountId)
                        && (x.IdempotencyKey == null || !x.IdempotencyKey.StartsWith(JournalKeys.YearClosePrefix)))
            .GroupBy(x => x.AccountId)
            .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        var ids = sums.Select(s => s.AccountId).ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && ids.Contains(a.Id))
            .Select(a => new AccountInfo(a.Id, a.Uuid, a.AccountCode, a.AccountName, a.RootType, a.IsGroup, a.Level, a.IsActive))
            .ToDictionaryAsync(a => a.Id, ct);

        decimal revenue = 0m, expenses = 0m;
        var lines = new List<YearEndPreviewLineModel>();
        var drafts = new List<JournalDraftLine>();
        foreach (var s in sums.OrderBy(s => accounts[s.AccountId].AccountCode, StringComparer.Ordinal))
        {
            var a = accounts[s.AccountId];
            var balance = s.Debit - s.Credit; // debit − credit
            var isRevenue = a.RootType == RootTypes.Revenue;
            if (isRevenue) revenue -= balance; else expenses += balance;
            if (balance == 0m) continue;
            var line = new YearEndPreviewLineModel
            {
                Account = AccountRef(a), RootType = a.RootType, Balance = isRevenue ? -balance : balance,
                ClosingDebit = balance < 0 ? -balance : 0m, ClosingCredit = balance > 0 ? balance : 0m,
            };
            lines.Add(line);
            drafts.Add(new JournalDraftLine { AccountId = a.Id, Debit = line.ClosingDebit, Credit = line.ClosingCredit, Description = $"Close {a.AccountCode} {a.AccountName}" });
        }

        var net = revenue - expenses;
        if (re is not null && net != 0m)
            lines.Add(new YearEndPreviewLineModel
            {
                Account = AccountRef(re), RootType = re.RootType, Balance = 0m,
                ClosingDebit = net < 0 ? -net : 0m, ClosingCredit = net > 0 ? net : 0m,
            });

        // D-RPT-7 — at most 499 P&L lines per entry, each chunk balanced by its own retained earnings line.
        var chunks = new List<List<JournalDraftLine>>();
        if (re is not null)
            foreach (var chunk in drafts.Chunk(MaxClosingLinesPerEntry))
            {
                var list = chunk.ToList();
                var diff = list.Sum(l => l.Debit) - list.Sum(l => l.Credit); // > 0: profit → credit retained earnings
                if (diff != 0m)
                    list.Add(new JournalDraftLine
                    {
                        AccountId = re.Id, Debit = diff < 0 ? -diff : 0m, Credit = diff > 0 ? diff : 0m,
                        Description = diff > 0 ? $"Net income {year.FiscalYearName}" : $"Net loss {year.FiscalYearName}",
                    });
                chunks.Add(list);
            }
        return new Plan(revenue, expenses, lines, chunks);
    }

    /// <summary>The result of a year already closed, read back from its closing entries' lines (none: zeros).</summary>
    private async Task<YearEndCloseResultModel> ExistingResultAsync(FiscalYear year, List<JournalEntry> entries, CancellationToken ct)
    {
        var ids = entries.Select(e => e.Id).ToList();
        var lines = await (from l in _db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
                           join a in _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking() on l.AccountId equals a.Id
                           where l.OrganizationId == year.OrganizationId && ids.Contains(l.JournalEntryId)
                           select new { a.Uuid, a.AccountCode, a.AccountName, a.RootType, l.DebitAmount, l.CreditAmount }).ToListAsync(ct);
        var revenue = lines.Where(l => l.RootType == RootTypes.Revenue).Sum(l => l.DebitAmount - l.CreditAmount);
        var expenses = lines.Where(l => l.RootType == RootTypes.Expense).Sum(l => l.CreditAmount - l.DebitAmount);
        var re = lines.FirstOrDefault(l => l.RootType == RootTypes.Equity);
        var refs = entries.Select(Ref).ToList();
        return new YearEndCloseResultModel
        {
            FiscalYear              = YearModel(year),
            ClosingEntry            = refs.FirstOrDefault(),
            ClosingEntries          = refs,
            TotalRevenueClosing     = revenue,
            TotalExpenseClosing     = expenses,
            NetIncomeTransferred    = revenue - expenses,
            AlreadyClosed           = true,
            RetainedEarningsAccount = re is null ? null : new AccountRefModel { Uuid = re.Uuid, AccountCode = re.AccountCode, AccountName = re.AccountName, RootType = re.RootType },
        };
    }

    private async Task<List<YearEndPreviewLineModel>> LinesOfEntriesAsync(Guid org, List<int> entryIds, CancellationToken ct)
    {
        var rows = await (from l in _db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
                          join a in _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking() on l.AccountId equals a.Id
                          where l.OrganizationId == org && entryIds.Contains(l.JournalEntryId)
                          orderby l.JournalEntryId, l.LineNumber
                          select new { a.Uuid, a.AccountCode, a.AccountName, a.RootType, l.DebitAmount, l.CreditAmount }).ToListAsync(ct);
        return rows.Select(r => new YearEndPreviewLineModel
        {
            Account = new AccountRefModel { Uuid = r.Uuid, AccountCode = r.AccountCode, AccountName = r.AccountName, RootType = r.RootType },
            RootType = r.RootType,
            // The natural balance that was closed: a revenue line debited 100 closed a credit balance of 100.
            Balance = r.RootType switch { RootTypes.Revenue => r.DebitAmount - r.CreditAmount, RootTypes.Expense => r.CreditAmount - r.DebitAmount, _ => 0m },
            ClosingDebit = r.DebitAmount, ClosingCredit = r.CreditAmount,
        }).ToList();
    }

    // ── pre-checks ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>§13.4 step 1 / D-18 — every failing pre-check, in the spec's order.</summary>
    private static List<Blocker> Blockers(FiscalYear year, int drafts, AccountInfo? re, bool explicitAccount)
    {
        var blockers = new List<Blocker>();
        if (year.IsClosed) blockers.Add(new(PeriodCloseErrorCodes.YearClosed, PeriodCloseMessages.YearClosed, false));
        var monthly = year.Periods.Where(p => p.PeriodNumber != FiscalYearService.AdjustmentPeriodNumber).ToList();
        if (monthly.Count == 0 || monthly.Any(p => p.Status is not (PeriodStatuses.Locked or PeriodStatuses.Closed)))
            blockers.Add(new(PeriodCloseErrorCodes.AllPeriodsLocked, PeriodCloseMessages.AllPeriodsLocked, false)); // BR-PC-004
        if (year.Periods.FirstOrDefault(p => p.PeriodNumber == FiscalYearService.AdjustmentPeriodNumber) is { } p13
            && p13.Status is not (PeriodStatuses.Open or PeriodStatuses.AdjustmentOnly))
            blockers.Add(new(PeriodCloseErrorCodes.Period13, PeriodCloseMessages.Period13, false));
        if (drafts > 0) blockers.Add(new(PeriodCloseErrorCodes.YearDrafts, PeriodCloseMessages.YearDrafts(drafts), false));
        if (re is null)
            blockers.Add(explicitAccount
                ? new(PeriodCloseErrorCodes.RetainedEarningsInvalid, PeriodCloseMessages.RetainedEarnings, true)
                : new(PeriodCloseErrorCodes.RetainedEarningsMissing, PeriodCloseMessages.NoRetainedEarnings, true));
        else if (re.RootType != RootTypes.Equity || re.IsGroup || re.Level != 4 || !re.IsActive)
            blockers.Add(new(PeriodCloseErrorCodes.RetainedEarningsInvalid, PeriodCloseMessages.RetainedEarnings, true)); // BR-PC-008
        return blockers;
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static PeriodCloseFiscalYearModel YearModel(FiscalYear y) => new()
    {
        Uuid = y.Uuid, FiscalYearName = y.FiscalYearName, StartDate = y.StartDate, EndDate = y.EndDate, IsClosed = y.IsClosed, ClosedAt = y.ClosedAt,
    };

    private static PeriodCloseStatusModel StatusModel(AccountingCaller caller, FiscalYear year, FiscalPeriod p, Dictionary<int, (int Posted, int Drafts)> counts)
    {
        var c = counts.GetValueOrDefault(p.Id);
        return new PeriodCloseStatusModel
        {
            Uuid              = p.Uuid,
            PeriodNumber      = p.PeriodNumber,
            PeriodName        = p.PeriodName,
            StartDate         = p.StartDate,
            EndDate           = p.EndDate,
            Status            = p.Status,
            PostedEntryCount  = c.Posted,
            DraftEntryCount   = c.Drafts,
            HasUnpostedDrafts = c.Drafts > 0,
            AllowedActions    = year.IsClosed ? [] : PeriodCloseRules.Actions(p.Status,
                caller.CanExecutePeriodClose || caller.CanAdministerPeriodClose, caller.CanAdministerPeriodClose),
        };
    }

    private async Task<FiscalYear> LoadYearAsync(Guid org, Guid uuid, bool track, CancellationToken ct)
    {
        var q = _db.FiscalYears.IgnoreQueryFilters().Include(y => y.Periods).Where(y => y.OrganizationId == org && y.Uuid == uuid);
        if (!track) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct) ?? throw new NotFoundException(PeriodCloseMessages.YearNotFound);
    }

    /// <summary>The year's closing entries (key yearclose:{uuid}, then yearclose:{uuid}:2 …), in order.</summary>
    private async Task<List<JournalEntry>> ClosingEntriesAsync(Guid org, Guid yearUuid, CancellationToken ct)
    {
        var key = JournalKeys.YearClose(yearUuid);
        var more = key + ":";
        return await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == org && j.IdempotencyKey != null && (j.IdempotencyKey == key || j.IdempotencyKey.StartsWith(more)))
            .OrderBy(j => j.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Posted (+ Reversed) and Draft entry counts per period: an entry belongs to its FiscalPeriodId; one without (an A44 draft
    /// dated outside every period when written) to the monthly period its date falls in.
    /// </summary>
    private async Task<Dictionary<int, (int Posted, int Drafts)>> EntryCountsAsync(FiscalYear year, CancellationToken ct)
    {
        var org = year.OrganizationId;
        var periodIds = year.Periods.Select(p => p.Id).ToList();
        var byPeriod = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == org && j.FiscalPeriodId != null && periodIds.Contains(j.FiscalPeriodId.Value))
            .GroupBy(j => new { PeriodId = j.FiscalPeriodId!.Value, Draft = j.Status == JournalStatuses.Draft })
            .Select(g => new { g.Key.PeriodId, g.Key.Draft, Count = g.Count() })
            .ToListAsync(ct);
        var unassigned = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == org && j.FiscalPeriodId == null && j.EntryDate >= year.StartDate && j.EntryDate <= year.EndDate)
            .GroupBy(j => new { j.EntryDate, Draft = j.Status == JournalStatuses.Draft })
            .Select(g => new { g.Key.EntryDate, g.Key.Draft, Count = g.Count() })
            .ToListAsync(ct);

        var counts = year.Periods.ToDictionary(p => p.Id, _ => (Posted: 0, Drafts: 0));
        void Add(int periodId, bool draft, int n)
        {
            var c = counts[periodId];
            counts[periodId] = draft ? (c.Posted, c.Drafts + n) : (c.Posted + n, c.Drafts);
        }
        foreach (var r in byPeriod) Add(r.PeriodId, r.Draft, r.Count);
        foreach (var r in unassigned)
        {
            var p = year.Periods.Where(x => x.StartDate <= r.EntryDate && r.EntryDate <= x.EndDate)
                .OrderBy(x => x.PeriodNumber == FiscalYearService.AdjustmentPeriodNumber ? 1 : 0).FirstOrDefault();
            if (p is not null) Add(p.Id, r.Draft, r.Count);
        }
        return counts;
    }

    /// <summary>BR-PC-001 — drafts of the period: assigned to it, or unassigned and dated inside it.</summary>
    private Task<int> DraftCountAsync(FiscalPeriod period, CancellationToken ct) =>
        _db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.OrganizationId == period.OrganizationId && j.Status == JournalStatuses.Draft
            && (j.FiscalPeriodId == period.Id
                || (j.FiscalPeriodId == null && j.EntryDate >= period.StartDate && j.EntryDate <= period.EndDate)), ct);

    /// <summary>D-18 — drafts anywhere in the year (by its periods or its dates).</summary>
    private Task<int> YearDraftCountAsync(FiscalYear year, CancellationToken ct) =>
        _db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.OrganizationId == year.OrganizationId && j.Status == JournalStatuses.Draft
            && (j.FiscalYearId == year.Id || (j.EntryDate >= year.StartDate && j.EntryDate <= year.EndDate)), ct);

    private async Task<AccountInfo?> AccountByUuidAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Uuid == uuid)
            .Select(a => new AccountInfo(a.Id, a.Uuid, a.AccountCode, a.AccountName, a.RootType, a.IsGroup, a.Level, a.IsActive))
            .FirstOrDefaultAsync(ct);

    /// <summary>BR-PC-008 — the DEFAULT_RETAINED_EARNINGS account, if assigned.</summary>
    private async Task<AccountInfo?> DefaultRetainedEarningsAsync(Guid org, CancellationToken ct)
    {
        var id = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && d.DefaultKey == DefaultKeys.RetainedEarnings).Select(d => d.AccountId).FirstOrDefaultAsync(ct);
        return id is null ? null : await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Id == id)
            .Select(a => new AccountInfo(a.Id, a.Uuid, a.AccountCode, a.AccountName, a.RootType, a.IsGroup, a.Level, a.IsActive))
            .FirstOrDefaultAsync(ct);
    }

    private static AccountRefModel AccountRef(AccountInfo a) =>
        new() { Uuid = a.Uuid, AccountCode = a.AccountCode, AccountName = a.AccountName, RootType = a.RootType };

    private static JournalEntryRefModel Ref(JournalEntry e) => new() { Uuid = e.Uuid, EntryNumber = e.EntryNumber };
}
