using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 C9 (§12, D-17, API-CONTRACT §5) — the trial balance, an account's balance as of a date and its ledger, computed on the
/// fly from the journal lines in the base currency (A-9). The aggregation runs in SQL (GROUP BY account with conditional
/// sums over lines joined to their entries); only per-account totals and the account list (≤ 2000 accounts, A42 D-25) come
/// back. GL_REPORTS_VIEW.
/// <para>D-CORE-4 / D-RPT-1 — "posted" is Status IN (Posted, Reversed): a reversed entry and its reversal both moved the
/// balances and net to zero together.</para>
/// </summary>
public interface ITrialBalanceService
{
    /// <summary>§12.4 — opening / period / closing debit and credit per level-4 account (BR-TB-001..005).</summary>
    Task<TrialBalanceReportModel> GenerateAsync(AccountingCaller caller, TrialBalanceQuery query, CancellationToken ct = default);

    /// <summary>The trial balance as CSV (UTF-8 with BOM, CRLF — like the other GL exports).</summary>
    Task<GlFile> ExportCsvAsync(AccountingCaller caller, TrialBalanceQuery query, CancellationToken ct = default);

    /// <summary>TS-TB-004 — Σ debits and credits posted on or before the date (default today).</summary>
    Task<AccountBalanceModel> GetAccountBalanceAsync(AccountingCaller caller, Guid accountUuid, DateOnly? asOfDate, CancellationToken ct = default);

    /// <summary>TS-TB-005 — the posted lines of one level-4 account in a date range, paged, each with its running balance.</summary>
    Task<AccountLedgerModel> GetAccountLedgerAsync(AccountingCaller caller, Guid accountUuid, AccountLedgerQuery query, CancellationToken ct = default);
}

/// <summary>API-CONTRACT §5 texts.</summary>
public static class TrialBalanceMessages
{
    public const string NoPermission    = "You do not have permission to view general ledger reports.";
    public const string RangeRequired   = "Enter a from and a to date, or a fiscal year.";
    public const string RangeOrder      = "The to date must not be before the from date.";
    public const string AccountNotFound = "Account not found.";
    public const string LeafOnly        = "Choose a Level 4 account.";
}

internal sealed class TrialBalanceService : ITrialBalanceService
{
    internal const int MaxLedgerPageSize = 500;

    private static readonly string[] PostedFamily = [JournalStatuses.Posted, JournalStatuses.Reversed];

    private readonly AccountingDbContext _db;
    private readonly TimeProvider _clock;

    public TrialBalanceService(AccountingDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    // ── trial balance ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<TrialBalanceReportModel> GenerateAsync(AccountingCaller caller, TrialBalanceQuery query, CancellationToken ct = default)
    {
        caller.Require(caller.CanViewReports, TrialBalanceMessages.NoPermission);
        query ??= new TrialBalanceQuery();
        var org = caller.OrganizationId;
        var (from, to, year) = await ResolveRangeAsync(org, query, ct);

        // REV-1 — closing entries are left out of the period only; the opening always includes them (earlier year-end closes).
        var lines = PostedLines(_db, org, query.IncludeClosingEntries, Clean(query.CostCenter), Clean(query.Project), closingExcludedFrom: from)
            .Where(x => x.EntryDate <= to);
        if (!query.IncludeOpeningBalances) lines = lines.Where(x => x.EntryDate >= from);

        // §12.4 — one GROUP BY in SQL; the opening/period split is a conditional sum.
        var sums = await lines
            .GroupBy(x => x.AccountId)
            .Select(g => new
            {
                AccountId     = g.Key,
                OpeningDebit  = g.Sum(x => x.EntryDate < from ? x.Debit : 0m),
                OpeningCredit = g.Sum(x => x.EntryDate < from ? x.Credit : 0m),
                PeriodDebit   = g.Sum(x => x.EntryDate >= from ? x.Debit : 0m),
                PeriodCredit  = g.Sum(x => x.EntryDate >= from ? x.Credit : 0m),
            })
            .ToDictionaryAsync(s => s.AccountId, ct);

        // Level 4 only; D-RPT-2 — an inactive account with activity is listed too (hiding it would unbalance the report).
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && !a.IsGroup)
            .Select(a => new { a.Id, a.Uuid, a.AccountCode, a.AccountName, a.RootType, a.AccountType, a.Level, a.IsActive })
            .ToListAsync(ct);

        var report = new TrialBalanceReportModel
        {
            FromDate               = from,
            ToDate                 = to,
            FiscalYear             = year,
            GeneratedAt            = _clock.GetUtcNow().UtcDateTime,
            CurrencyCode           = await BaseCurrencyAsync(org, to, ct),
            IncludeOpeningBalances = query.IncludeOpeningBalances,
            IncludeClosingEntries  = query.IncludeClosingEntries,
        };
        foreach (var a in accounts.OrderBy(a => a.AccountCode, StringComparer.Ordinal))
        {
            var has = sums.TryGetValue(a.Id, out var s);
            if (!has && (!query.IncludeZeroBalances || !a.IsActive)) continue; // BR-TB-005
            var line = new TrialBalanceLineModel
            {
                Account       = new AccountRefModel { Uuid = a.Uuid, AccountCode = a.AccountCode, AccountName = a.AccountName, AccountType = a.AccountType, RootType = a.RootType },
                RootType      = a.RootType,
                AccountType   = a.AccountType,
                Level         = a.Level,
                IsActive      = a.IsActive,
                OpeningDebit  = s?.OpeningDebit ?? 0m,
                OpeningCredit = s?.OpeningCredit ?? 0m,
                PeriodDebit   = s?.PeriodDebit ?? 0m,
                PeriodCredit  = s?.PeriodCredit ?? 0m,
            };
            line.ClosingDebit  = line.OpeningDebit + line.PeriodDebit;
            line.ClosingCredit = line.OpeningCredit + line.PeriodCredit;
            line.Balance       = line.ClosingDebit - line.ClosingCredit;
            report.Lines.Add(line);
        }

        report.Totals = new TrialBalanceTotalsModel
        {
            OpeningDebit  = report.Lines.Sum(l => l.OpeningDebit),
            OpeningCredit = report.Lines.Sum(l => l.OpeningCredit),
            PeriodDebit   = report.Lines.Sum(l => l.PeriodDebit),
            PeriodCredit  = report.Lines.Sum(l => l.PeriodCredit),
            ClosingDebit  = report.Lines.Sum(l => l.ClosingDebit),
            ClosingCredit = report.Lines.Sum(l => l.ClosingCredit),
        };
        report.TotalDebit = report.Totals.ClosingDebit;
        report.TotalCredit = report.Totals.ClosingCredit;
        report.IsBalanced = Math.Abs(report.TotalDebit - report.TotalCredit) < JournalLimits.BalanceTolerance; // BR-TB-002
        return report;
    }

    public async Task<GlFile> ExportCsvAsync(AccountingCaller caller, TrialBalanceQuery query, CancellationToken ct = default)
    {
        var tb = await GenerateAsync(caller, query, ct);
        var sb = new StringBuilder();
        void Row(IEnumerable<string> fields) => sb.Append(string.Join(",", fields.Select(MappingCsv.Escape))).Append("\r\n"); // CRLF on every host (A42 D-58)

        Row(["account_code", "account_name", "root_type", "account_type", "opening_debit", "opening_credit", "period_debit", "period_credit",
             "closing_debit", "closing_credit", "balance"]);
        foreach (var l in tb.Lines)
            Row([l.Account.AccountCode, l.Account.AccountName, l.RootType, l.AccountType ?? "", Money(l.OpeningDebit), Money(l.OpeningCredit),
                 Money(l.PeriodDebit), Money(l.PeriodCredit), Money(l.ClosingDebit), Money(l.ClosingCredit), Money(l.Balance)]);
        var t = tb.Totals;
        Row(["", "TOTAL", "", "", Money(t.OpeningDebit), Money(t.OpeningCredit), Money(t.PeriodDebit), Money(t.PeriodCredit),
             Money(t.ClosingDebit), Money(t.ClosingCredit), Money(t.ClosingDebit - t.ClosingCredit)]);

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new GlFile(bytes, $"trial-balance-{tb.FromDate:yyyy-MM-dd}-{tb.ToDate:yyyy-MM-dd}.csv", "text/csv");
    }

    // ── account balance / ledger ───────────────────────────────────────────────────────────────────────────────────

    private sealed record LeafAccount(int Id, AccountRefModel Ref);

    private async Task<LeafAccount> LeafAsync(Guid org, Guid uuid, CancellationToken ct)
    {
        var a = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.OrganizationId == org && x.Uuid == uuid)
            .Select(x => new { x.Id, x.Uuid, x.AccountCode, x.AccountName, x.RootType, x.AccountType, x.IsGroup, x.Level })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException(TrialBalanceMessages.AccountNotFound);
        if (a.IsGroup || a.Level != 4) throw new BadRequestException(TrialBalanceMessages.LeafOnly);
        return new LeafAccount(a.Id, new AccountRefModel
            { Uuid = a.Uuid, AccountCode = a.AccountCode, AccountName = a.AccountName, AccountType = a.AccountType, RootType = a.RootType });
    }

    public async Task<AccountBalanceModel> GetAccountBalanceAsync(AccountingCaller caller, Guid accountUuid, DateOnly? asOfDate, CancellationToken ct = default)
    {
        caller.Require(caller.CanViewReports, TrialBalanceMessages.NoPermission);
        var org = caller.OrganizationId;
        var account = await LeafAsync(org, accountUuid, ct);
        var date = asOfDate ?? DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var id = account.Id;

        // Every posting through the date, closing entries included (they are real postings; TS-TB-004).
        var totals = await PostedLines(_db, org, includeClosingEntries: true, null, null)
            .Where(x => x.AccountId == id && x.EntryDate <= date)
            .GroupBy(x => 1)
            .Select(g => new { Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) })
            .FirstOrDefaultAsync(ct);
        var debit = totals?.Debit ?? 0m;
        var credit = totals?.Credit ?? 0m;
        return new AccountBalanceModel { Account = account.Ref, AsOfDate = date, Debit = debit, Credit = credit, Balance = debit - credit };
    }

    public async Task<AccountLedgerModel> GetAccountLedgerAsync(AccountingCaller caller, Guid accountUuid, AccountLedgerQuery query, CancellationToken ct = default)
    {
        caller.Require(caller.CanViewReports, TrialBalanceMessages.NoPermission);
        query ??= new AccountLedgerQuery();
        var org = caller.OrganizationId;
        var account = await LeafAsync(org, accountUuid, ct);
        if (query.FromDate is not { } from || query.ToDate is not { } to) throw new BadRequestException(TrialBalanceMessages.RangeRequired);
        if (to < from) throw new BadRequestException(TrialBalanceMessages.RangeOrder);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, MaxLedgerPageSize);
        var accountId = account.Id;
        var all = PostedLines(_db, org, query.IncludeClosingEntries, null, null, closingExcludedFrom: from).Where(x => x.AccountId == accountId); // REV-1

        var opening = await all.Where(x => x.EntryDate < from).SumAsync(x => x.Debit - x.Credit, ct);
        var range = all.Where(x => x.EntryDate >= from && x.EntryDate <= to);
        var totals = await range.GroupBy(x => 1)
            .Select(g => new { Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit), Count = g.Count() })
            .FirstOrDefaultAsync(ct);
        var count = totals?.Count ?? 0;

        // Contract order: entryDate, entryNumber, lineNumber (entry id breaks a tie) — stable, so a page's running balance
        // starts where the previous page ended: opening + Σ of the rows ordered before the page's first row.
        var ordered = range.OrderBy(x => x.EntryDate).ThenBy(x => x.EntryNumber).ThenBy(x => x.JournalEntryId).ThenBy(x => x.LineNumber);
        var skip = (page - 1) * size;
        List<PostedLine> rows = [];
        if (skip < count) rows = await ordered.Skip(skip).Take(size).ToListAsync(ct);
        var before = 0m;
        if (skip > 0 && rows.Count > 0)
        {
            // A range predicate on the sort key, not a SUM over TOP(n) of the sorted join (that took 46 s for the last page of a
            // 6,000-line account on LocalDB, task 428).
            var f = rows[0];
            DateOnly d = f.EntryDate; string n = f.EntryNumber; int id = f.JournalEntryId, ln = f.LineNumber;
            before = await range.Where(x => x.EntryDate < d
                    || (x.EntryDate == d && (string.Compare(x.EntryNumber, n) < 0
                        || (x.EntryNumber == n && (x.JournalEntryId < id || (x.JournalEntryId == id && x.LineNumber < ln))))))
                .SumAsync(x => x.Debit - x.Credit, ct);
        }

        var typeIds = rows.Select(r => r.JournalTypeId).Distinct().ToList();
        var typeCodes = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.OrganizationId == org && typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct);

        var running = opening + before;
        var data = new List<LedgerLineModel>(rows.Count);
        foreach (var r in rows)
        {
            running += r.Debit - r.Credit;
            data.Add(new LedgerLineModel
            {
                LineUuid        = r.LineUuid,
                Entry           = new JournalEntryRefModel { Uuid = r.EntryUuid, EntryNumber = r.EntryNumber },
                EntryDate       = r.EntryDate,
                JournalTypeCode = typeCodes.GetValueOrDefault(r.JournalTypeId, string.Empty),
                Status          = r.Status,
                Reference       = r.Reference,
                Description     = r.Memo ?? r.EntryDescription,
                LineNumber      = r.LineNumber,
                PartnerName     = r.PartnerName,
                Debit           = r.Debit,
                Credit          = r.Credit,
                RunningBalance  = running,
                Reconciled      = r.Reconciled,
                IsClosingEntry  = r.IsClosingEntry,
            });
        }

        var totalDebit = totals?.Debit ?? 0m;
        var totalCredit = totals?.Credit ?? 0m;
        return new AccountLedgerModel
        {
            Account        = account.Ref,
            FromDate       = from,
            ToDate         = to,
            OpeningBalance = opening,
            ClosingBalance = opening + totalDebit - totalCredit,
            TotalDebit     = totalDebit,
            TotalCredit    = totalCredit,
            Page = new PaginatedResponse<LedgerLineModel>
            {
                Data = data, TotalRecords = count, Page = page, PageSize = size, TotalPages = (int)Math.Ceiling(count / (double)size),
            },
        };
    }

    // ── shared ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One line of a Posted/Reversed entry with its header's fields — a SQL join, never materialized whole.</summary>
    internal sealed class PostedLine
    {
        public int       AccountId        { get; init; }
        public int       JournalEntryId   { get; init; }
        public Guid      LineUuid         { get; init; }
        public int       LineNumber       { get; init; }
        public DateOnly  EntryDate        { get; init; }
        public decimal   Debit            { get; init; }
        public decimal   Credit           { get; init; }
        public string?   Memo             { get; init; }
        public string?   PartnerName      { get; init; }
        public bool      Reconciled       { get; init; }
        public Guid      EntryUuid        { get; init; }
        public string    EntryNumber      { get; init; } = string.Empty;
        public string    Status           { get; init; } = string.Empty;
        public string?   Reference        { get; init; }
        public string?   EntryDescription { get; init; }
        public bool      IsClosingEntry   { get; init; }
        public int       JournalTypeId    { get; init; }
        public string?   IdempotencyKey   { get; init; }
    }

    /// <summary>
    /// The organization's lines of Posted/Reversed entries (BR-TB-001, D-RPT-1); closing entries left out unless asked
    /// (BR-TB-004); optional cost center / project (task 332). The organization is explicit and the tenant filter ignored
    /// (a super admin bypasses it, and its OR defeats index seeks).
    /// </summary>
    internal static IQueryable<PostedLine> PostedLines(AccountingDbContext db, Guid org, bool includeClosingEntries, string? costCenter, string? project,
        DateOnly? closingExcludedFrom = null)
    {
        // REV-1 — without includeClosingEntries a closing entry is left out only when dated on/after closingExcludedFrom
        // (the report's from date): an earlier year's close stays in the opening balance.
        var cutoff = closingExcludedFrom ?? DateOnly.MinValue;
        var q = from l in db.JournalEntryLines.IgnoreQueryFilters()
                join j in db.JournalEntries.IgnoreQueryFilters() on l.JournalEntryId equals j.Id
                where l.OrganizationId == org && j.OrganizationId == org && PostedFamily.Contains(j.Status)
                select new { l, j };
        if (!includeClosingEntries) q = q.Where(x => !x.j.IsClosingEntry || x.j.EntryDate < cutoff);
        if (costCenter is not null) q = q.Where(x => x.l.CostCenter == costCenter);
        if (project is not null) q = q.Where(x => x.l.Project == project);
        return q.Select(x => new PostedLine
        {
            AccountId        = x.l.AccountId,
            JournalEntryId   = x.l.JournalEntryId,
            LineUuid         = x.l.Uuid,
            LineNumber       = x.l.LineNumber,
            EntryDate        = x.j.EntryDate,
            Debit            = x.l.DebitAmount,
            Credit           = x.l.CreditAmount,
            Memo             = x.l.Memo,
            PartnerName      = x.l.PartnerName,
            Reconciled       = x.l.Reconciled,
            EntryUuid        = x.j.Uuid,
            EntryNumber      = x.j.EntryNumber,
            Status           = x.j.Status,
            Reference        = x.j.Reference,
            EntryDescription = x.j.Description,
            IsClosingEntry   = x.j.IsClosingEntry,
            JournalTypeId    = x.j.JournalTypeId,
            IdempotencyKey   = x.j.IdempotencyKey,
        });
    }

    /// <summary>Explicit dates win; else the fiscal period's (RPT extra); else the fiscal year's. Also the year to show.</summary>
    private async Task<(DateOnly From, DateOnly To, FiscalYearRefModel? Year)> ResolveRangeAsync(Guid org, TrialBalanceQuery query, CancellationToken ct)
    {
        DateOnly? from = query.FromDate, to = query.ToDate;
        FiscalYearRefModel? year = null;
        var years = _db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Where(y => y.OrganizationId == org);
        if (query.FiscalYearUuid is { } yu)
        {
            var y = await years.Where(x => x.Uuid == yu).Select(x => new { x.Uuid, x.FiscalYearName, x.StartDate, x.EndDate }).FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Fiscal year not found.");
            year = new FiscalYearRefModel { Uuid = y.Uuid, FiscalYearName = y.FiscalYearName };
            if (from is null || to is null) (from, to) = (from ?? y.StartDate, to ?? y.EndDate);
        }
        if ((from is null || to is null) && query.FiscalPeriodUuid is { } pu)
        {
            var p = await _db.FiscalPeriods.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.OrganizationId == org && x.Uuid == pu).Select(x => new { x.StartDate, x.EndDate }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Fiscal period not found.");
            (from, to) = (from ?? p.StartDate, to ?? p.EndDate);
        }
        if (from is null || to is null) throw new BadRequestException(TrialBalanceMessages.RangeRequired);
        if (to < from) throw new BadRequestException(TrialBalanceMessages.RangeOrder);

        if (year is null)
        {
            DateOnly f = from.Value, t = to.Value;
            year = await years.Where(y => y.StartDate <= f && t <= y.EndDate)
                .Select(y => new FiscalYearRefModel { Uuid = y.Uuid, FiscalYearName = y.FiscalYearName }).FirstOrDefaultAsync(ct);
        }
        return (from.Value, to.Value, year);
    }

    /// <summary>The reporting currency of the fiscal year containing the date, else of the latest year (as the engine).</summary>
    private async Task<string?> BaseCurrencyAsync(Guid org, DateOnly date, CancellationToken ct)
    {
        var years = _db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Where(y => y.OrganizationId == org);
        return await years.Where(y => y.StartDate <= date && date <= y.EndDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct)
               ?? await years.OrderByDescending(y => y.StartDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
