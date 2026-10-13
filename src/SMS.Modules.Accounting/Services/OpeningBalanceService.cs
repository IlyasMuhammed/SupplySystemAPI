using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>Opening balance entries refused: every problem at once (400; the controller returns the list).</summary>
public sealed class OpeningBalanceValidationException : BadRequestException
{
    public IReadOnlyList<OpeningBalanceError> Errors { get; }

    public OpeningBalanceValidationException(IReadOnlyList<OpeningBalanceError> errors)
        : base(errors.Count == 1 ? errors[0].Message : $"{errors.Count} opening balances are invalid.") => Errors = errors;
}

/// <summary>
/// A42 §10 / D-10 / D-11 / D-12 / D-19 — GL opening balances: draft entries per active leaf account (never 3900, which
/// takes the difference), then one post that writes the OPENING_BALANCE journal entry (JE-YYYY-NNNNN, plug line to 3900),
/// the accounts' opening/current balances and the setup flags, all in one transaction. Skip = post with no entries.
/// The conversion date is the A40 opening balance date (Finance, <see cref="IOpeningBalanceDateProvider"/>); once posted
/// it is locked through <see cref="GlOpeningDateLockSource"/>.
/// </summary>
public interface IOpeningBalanceService
{
    Task<OpeningBalancesModel> GetAsync(AccountingCaller caller, CancellationToken ct = default);
    Task<OpeningBalancesModel> SaveBatchAsync(AccountingCaller caller, SaveOpeningBalancesRequest request, CancellationToken ct = default);
    Task<PostOpeningBalancesResult> PostAsync(AccountingCaller caller, PostOpeningBalancesRequest request, CancellationToken ct = default);
    Task<OpeningSummaryModel> SummaryAsync(AccountingCaller caller, CancellationToken ct = default);
}

internal sealed class OpeningBalanceService : IOpeningBalanceService
{
    internal const string DateMissingMessage = "Set the opening balance date in Finance settings first.";
    internal const string DateOutsideMessage = "The opening balance date must fall within the first fiscal year.";
    internal const string AlreadyPostedMessage = "Opening balances were already posted.";
    internal const string LeafMessage = "Only Level 4 accounts accept opening balances.";
    internal const string BothSidesMessage = "Cannot have both debit and credit amounts.";
    internal const string PlugMessage = "Opening Balance Equity (3900) is calculated automatically.";
    internal const string NegativeMessage = "Opening balances cannot be negative.";
    internal const string NoFiscalYearMessage = "Set up a fiscal year first.";
    internal const string NothingToPostMessage = "Enter at least one opening balance, or skip this step.";
    internal const string SkipWithDraftsMessage = "Delete the draft opening balances before skipping, or post them instead.";
    internal const string PlugMissingMessage = "Opening Balance Equity (3900) is missing from the chart of accounts.";
    internal const string ProfitAndLossWarning = "Revenue and expense accounts usually start the fiscal year at zero.";

    internal static string DifferenceMessage(decimal difference) =>
        $"Debits and credits differ by {Math.Abs(difference).ToString("#,##0.00", CultureInfo.InvariantCulture)}. " +
        "Confirm the difference to post it to Opening Balance Equity (3900).";

    private readonly AccountingDbContext _db;
    private readonly IAccountingInitializer _init;
    private readonly ISetupStatusService _setup;
    private readonly IAccountingEventPublisher _events;
    private readonly JournalNumbers _numbers;
    private readonly IOpeningBalanceDateProvider? _dates;
    private readonly IOpeningSubledgerTotals? _subledger;

    public OpeningBalanceService(AccountingDbContext db, IAccountingInitializer init, ISetupStatusService setup,
        IAccountingEventPublisher events, JournalNumbers numbers,
        IOpeningBalanceDateProvider? dates = null, IOpeningSubledgerTotals? subledger = null)
    {
        _db = db;
        _init = init;
        _setup = setup;
        _events = events;
        _numbers = numbers;
        _dates = dates;
        _subledger = subledger;
    }

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<OpeningBalancesModel> GetAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var status = await LoadStatusAsync(org, ct, track: false);
        var posted = status.OpeningBalancesPostedAt is not null;

        var entries = await _db.OpeningBalanceEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org).ToListAsync(ct);
        var withEntry = entries.Select(e => e.AccountId).ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && !a.IsGroup && (a.IsActive || withEntry.Contains(a.Id)
                || (posted && (a.OpeningBalanceDebit != 0 || a.OpeningBalanceCredit != 0))))
            .ToListAsync(ct);
        var byAccount = entries.ToDictionary(e => e.AccountId);
        var conversionDate = posted ? status.ConversionDate : await GetConversionDateAsync(org, ct);

        var groups = RootTypes.All.Select(root => new OpeningGroupModel
        {
            RootType = root,
            Rows = accounts.Where(a => a.RootType == root)
                .OrderBy(a => a.AccountCode, StringComparer.Ordinal)
                .Select(a =>
                {
                    var entry = byAccount.GetValueOrDefault(a.Id);
                    var debit = posted ? a.OpeningBalanceDebit : entry?.DebitAmount ?? 0m;
                    var credit = posted ? a.OpeningBalanceCredit : entry?.CreditAmount ?? 0m;
                    return new OpeningRowModel
                    {
                        AccountUuid  = a.Uuid,
                        AccountCode  = a.AccountCode,
                        AccountName  = a.AccountName,
                        AccountType  = a.AccountType,
                        DebitAmount  = debit,
                        CreditAmount = credit,
                        Notes        = entry?.Notes,
                        IsPlug       = IsPlug(a),
                        Warning      = a.RootType is RootTypes.Revenue or RootTypes.Expense && (debit != 0 || credit != 0) ? ProfitAndLossWarning : null,
                    };
                })
                .ToList(),
        }).ToList();

        return new OpeningBalancesModel
        {
            ConversionDate = conversionDate,
            IsPosted       = posted,
            PostedAt       = status.OpeningBalancesPostedAt,
            IsSkipped      = posted && entries.Count == 0,
            Groups         = groups,
            Summary        = Summarize(entries, conversionDate),
            Subledger      = await BuildSubledgerAsync(org, entries, ct),
        };
    }

    public async Task<OpeningSummaryModel> SummaryAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var status = await LoadStatusAsync(org, ct, track: false);
        var entries = await _db.OpeningBalanceEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org).ToListAsync(ct);
        var date = status.OpeningBalancesPostedAt is not null ? status.ConversionDate : await GetConversionDateAsync(org, ct);
        return Summarize(entries, date);
    }

    // ── writes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<OpeningBalancesModel> SaveBatchAsync(AccountingCaller caller, SaveOpeningBalancesRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var status = await LoadStatusAsync(org, ct, track: false);
        if (status.OpeningBalancesPostedAt is not null) throw new ConflictException(AlreadyPostedMessage); // BR-OB-004

        var inputs = request.Entries ?? [];
        var uuids = inputs.Select(i => i.AccountUuid).Distinct().ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && uuids.Contains(a.Uuid)).ToDictionaryAsync(a => a.Uuid, ct);

        var errors = new List<OpeningBalanceError>();
        var seen = new HashSet<Guid>();
        foreach (var input in inputs)
        {
            var account = accounts.GetValueOrDefault(input.AccountUuid);
            string? message;
            if (!seen.Add(input.AccountUuid)) message = $"Account {account?.AccountCode ?? input.AccountUuid.ToString()} is listed more than once.";
            else if (account is null) message = "Account not found.";
            else message = CheckEntry(account, input.DebitAmount, input.CreditAmount)
                ?? (input.Notes?.Trim().Length > 500 ? "Notes can be at most 500 characters." : null);
            if (message is not null)
                errors.Add(new OpeningBalanceError { AccountUuid = input.AccountUuid, AccountCode = account?.AccountCode, Message = message });
        }
        if (errors.Count > 0) throw new OpeningBalanceValidationException(errors);

        var ids = accounts.Values.Select(a => a.Id).ToList();
        var existing = await _db.OpeningBalanceEntries.IgnoreQueryFilters()
            .Where(e => e.OrganizationId == org && ids.Contains(e.AccountId)).ToDictionaryAsync(e => e.AccountId, ct);

        await _db.InTransactionAsync(async () =>
        {
            foreach (var input in inputs)
            {
                var account = accounts[input.AccountUuid];
                var debit = Math.Round(input.DebitAmount, 4, MidpointRounding.AwayFromZero);
                var credit = Math.Round(input.CreditAmount, 4, MidpointRounding.AwayFromZero);
                var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
                var entry = existing.GetValueOrDefault(account.Id);
                if (debit == 0 && credit == 0)
                {
                    if (entry is not null) _db.OpeningBalanceEntries.Remove(entry);
                    continue;
                }
                if (entry is null)
                {
                    entry = new OpeningBalanceEntry { OrganizationId = org, AccountId = account.Id, CreatedBy = caller.UserIdOrNull };
                    _db.OpeningBalanceEntries.Add(entry);
                }
                entry.DebitAmount = debit;
                entry.CreditAmount = credit;
                entry.Notes = notes;
                entry.EntryStatus = OpeningEntryStatuses.Draft;
                entry.ModifiedBy = caller.UserIdOrNull;
            }
            await _db.SaveChangesAsync(ct);
        }, ct);

        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return await GetAsync(caller, ct);
    }

    public async Task<PostOpeningBalancesResult> PostAsync(AccountingCaller caller, PostOpeningBalancesRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var status = await LoadStatusAsync(org, ct, track: true);
        if (status.OpeningBalancesPostedAt is not null) throw new ConflictException(AlreadyPostedMessage);

        var entries = await _db.OpeningBalanceEntries.IgnoreQueryFilters().Include(e => e.Account)
            .Where(e => e.OrganizationId == org).ToListAsync(ct);
        var date = await GetConversionDateAsync(org, ct);
        var now = DateTime.UtcNow;

        if (request.Skip)
        {
            if (entries.Count > 0) throw new ConflictException(SkipWithDraftsMessage);
            await _db.InTransactionAsync(async () =>
            {
                await ClaimPostAsync(status, now, ct);
                status.OpeningBalancesEntered = true;
                status.OpeningBalancesBalanced = true;
                status.ConversionDate = date;
                await _db.SaveChangesAsync(ct);
            }, ct);
            var skipped = Summarize(entries, date);
            await _events.PublishAsync(new OpeningBalancePostedEvent(org, 0, 0, 0, date, null, Skipped: true, caller.UserIdOrNull, caller.UserName), ct);
            await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
            return new PostOpeningBalancesResult { Summary = skipped };
        }

        // D-11 / BR-OB-006 — the A40 opening balance date, inside the first (earliest) fiscal year.
        if (date is not { } conversionDate) throw new BadRequestException(DateMissingMessage);
        var firstYear = await _db.FiscalYears.IgnoreQueryFilters().AsNoTracking()
            .Where(y => y.OrganizationId == org).OrderBy(y => y.StartDate).FirstOrDefaultAsync(ct)
            ?? throw new BadRequestException(NoFiscalYearMessage);
        if (conversionDate < firstYear.StartDate || conversionDate > firstYear.EndDate) throw new BadRequestException(DateOutsideMessage);

        if (entries.Count == 0) throw new BadRequestException(NothingToPostMessage);
        var errors = entries
            .Select(e => (e, Message: CheckEntry(e.Account, e.DebitAmount, e.CreditAmount)))
            .Where(x => x.Message is not null)
            .Select(x => new OpeningBalanceError { AccountUuid = x.e.Account.Uuid, AccountCode = x.e.Account.AccountCode, Message = x.Message! })
            .ToList();
        if (errors.Count > 0) throw new OpeningBalanceValidationException(errors);

        var summary = Summarize(entries, conversionDate);
        var difference = summary.Difference;
        if (difference != 0 && !request.AcknowledgeDifference) throw new BadRequestException(DifferenceMessage(difference)); // BR-OB-005

        ChartOfAccount? plug = null;
        if (difference != 0)
            plug = await _db.ChartOfAccounts.IgnoreQueryFilters()
                .Where(a => a.OrganizationId == org && a.IsActive && !a.IsGroup && a.AccountCode == SystemAccounts.OpeningBalanceEquityCode)
                .FirstOrDefaultAsync(ct)
                ?? throw new ConflictException(PlugMissingMessage);

        var number = await _numbers.NextAsync(org, now, ct);
        var plugDebit = difference < 0 ? -difference : 0m;
        var plugCredit = difference > 0 ? difference : 0m;
        var journal = new JournalEntry
        {
            OrganizationId = org,
            EntryNumber    = number,
            EntryType      = JournalEntryTypes.OpeningBalance,
            EntryDate      = conversionDate,
            Reference      = $"Opening Balance Entry — {conversionDate:yyyy-MM-dd}",
            Description    = "General ledger opening balances",
            Status         = JournalStatuses.Posted,
            TotalDebit     = summary.TotalDebits + plugDebit,
            TotalCredit    = summary.TotalCredits + plugCredit,
            CurrencyCode   = firstYear.ReportingCurrencyCode,
            PostedAt       = now,
            PostedBy       = caller.UserIdOrNull,
            CreatedBy      = caller.UserIdOrNull,
        };
        var line = 0;
        foreach (var e in entries.OrderBy(e => e.Account.AccountCode, StringComparer.Ordinal))
            journal.Lines.Add(new JournalEntryLine
            {
                OrganizationId = org, LineNumber = ++line, AccountId = e.AccountId,
                DebitAmount = e.DebitAmount, CreditAmount = e.CreditAmount, Memo = e.Notes,
            });
        if (plug is not null)
            journal.Lines.Add(new JournalEntryLine
            {
                OrganizationId = org, LineNumber = ++line, AccountId = plug.Id,
                DebitAmount = plugDebit, CreditAmount = plugCredit, Memo = "Opening balance difference",
            });

        await _db.InTransactionAsync(async () =>
        {
            await ClaimPostAsync(status, now, ct);
            _db.JournalEntries.Add(journal);

            foreach (var e in entries)
            {
                Apply(e.Account, e.DebitAmount, e.CreditAmount, caller.UserIdOrNull);
                e.EntryStatus = OpeningEntryStatuses.Posted;
                e.PostedAt = now;
                e.PostedBy = caller.UserIdOrNull;
                e.ConversionDate = conversionDate;
                e.JournalEntry = journal;
                e.ModifiedBy = caller.UserIdOrNull;
            }
            if (plug is not null) Apply(plug, plugDebit, plugCredit, caller.UserIdOrNull);

            status.OpeningBalancesEntered = true;
            status.OpeningBalancesBalanced = true; // balanced, or the difference was acknowledged and plugged (§10.3 step 4)
            status.ConversionDate = conversionDate;
            await _db.SaveChangesAsync(ct);
        }, ct);

        await _events.PublishAsync(new OpeningBalancePostedEvent(org, summary.TotalDebits, summary.TotalCredits, difference, conversionDate,
            number, Skipped: false, caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return new PostOpeningBalancesResult { JournalEntryNumber = number, Summary = summary };
    }

    // ── rules ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool IsPlug(ChartOfAccount a) => a.AccountCode == SystemAccounts.OpeningBalanceEquityCode;

    /// <summary>BR-OB-001 / 003 / D-19.</summary>
    private static string? CheckEntry(ChartOfAccount account, decimal debit, decimal credit)
    {
        if (account.IsGroup || account.Level != 4) return LeafMessage;
        if (IsPlug(account)) return PlugMessage;
        if (!account.IsActive) return $"Account {account.AccountCode} is inactive.";
        if (debit < 0 || credit < 0) return NegativeMessage;
        if (debit > 0 && credit > 0) return BothSidesMessage;
        return null;
    }

    /// <summary>§10.3 step 3 — opening debit/credit, current balance in the account's normal direction, HasTransactions.</summary>
    private static void Apply(ChartOfAccount account, decimal debit, decimal credit, int? userId)
    {
        account.OpeningBalanceDebit = debit;
        account.OpeningBalanceCredit = credit;
        account.CurrentBalance += RootTypes.IsDebitNormal(account.RootType) ? debit - credit : credit - debit;
        account.HasTransactions = true;
        account.ModifiedBy = userId;
    }

    /// <summary>
    /// Marks the status row posted. On SQL Server this is a conditional UPDATE (PostedAt IS NULL) inside the transaction, so
    /// of two concurrent posts only one gets the row; the other is refused.
    /// </summary>
    private async Task ClaimPostAsync(CoaSetupStatus status, DateTime now, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            var claimed = await _db.CoaSetupStatuses.IgnoreQueryFilters()
                .Where(s => s.Id == status.Id && s.OpeningBalancesPostedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.OpeningBalancesPostedAt, now), ct);
            if (claimed == 0) throw new ConflictException(AlreadyPostedMessage);
        }
        status.OpeningBalancesPostedAt = now;
    }

    internal static OpeningSummaryModel Summarize(IReadOnlyCollection<OpeningBalanceEntry> entries, DateOnly? conversionDate)
    {
        var debits = entries.Sum(e => e.DebitAmount);
        var credits = entries.Sum(e => e.CreditAmount);
        var difference = debits - credits;
        return new OpeningSummaryModel
        {
            TotalDebits    = debits,
            TotalCredits   = credits,
            Difference     = difference,
            IsBalanced     = difference == 0,
            ConversionDate = conversionDate,
            PlugSide       = difference > 0 ? "Credit" : difference < 0 ? "Debit" : null,
            EntryCount     = entries.Count,
        };
    }

    private async Task<DateOnly?> GetConversionDateAsync(Guid org, CancellationToken ct) =>
        _dates is null ? null : await _dates.GetAsync(org, ct);

    private async Task<CoaSetupStatus> LoadStatusAsync(Guid org, CancellationToken ct, bool track)
    {
        var query = _db.CoaSetupStatuses.IgnoreQueryFilters().Where(s => s.OrganizationId == org);
        if (!track) query = query.AsNoTracking();
        return await query.SingleAsync(ct);
    }

    /// <summary>D-12 — Finance's A40 sub-ledger totals next to the draft/posted amounts of the A/R, A/P and Inventory defaults.</summary>
    private async Task<SubledgerReferenceModel?> BuildSubledgerAsync(Guid org, IReadOnlyCollection<OpeningBalanceEntry> entries, CancellationToken ct)
    {
        if (_subledger is null) return null;
        OpeningSubledgerTotals totals;
        try
        {
            totals = await _subledger.GetAsync(org, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // information only (D-12): never fails the page
        }

        var keys = new[] { DefaultKeys.Receivable, DefaultKeys.Payable, DefaultKeys.Inventory };
        var defaults = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && keys.Contains(d.DefaultKey) && d.AccountId != null)
            .Join(_db.ChartOfAccounts.IgnoreQueryFilters(), d => d.AccountId, a => a.Id,
                (d, a) => new { d.DefaultKey, a.Id, a.AccountCode, a.RootType })
            .ToListAsync(ct);

        (decimal? Balance, string? Code) For(string key)
        {
            var d = defaults.FirstOrDefault(x => x.DefaultKey == key);
            if (d is null) return (null, null);
            var e = entries.FirstOrDefault(x => x.AccountId == d.Id);
            var debit = e?.DebitAmount ?? 0m;
            var credit = e?.CreditAmount ?? 0m;
            return (RootTypes.IsDebitNormal(d.RootType) ? debit - credit : credit - debit, d.AccountCode);
        }

        var (ar, arCode) = For(DefaultKeys.Receivable);
        var (ap, apCode) = For(DefaultKeys.Payable);
        var (inv, invCode) = For(DefaultKeys.Inventory);
        return new SubledgerReferenceModel
        {
            CustomerOpeningTotal     = totals.CustomerOpeningTotal,
            SupplierOpeningTotal     = totals.SupplierOpeningTotal,
            OpeningStockValue        = totals.OpeningStockValue,
            ReceivableAccountBalance = ar,
            PayableAccountBalance    = ap,
            InventoryAccountBalance  = inv,
            ReceivableAccountCode    = arCode,
            PayableAccountCode       = apCode,
            InventoryAccountCode     = invCode,
            CurrencyCode             = totals.CurrencyCode,
        };
    }
}

/// <summary>
/// D-11 / D-44 — pins the A40 opening balance date once the GL opening balances are posted (or skipped with a date): the
/// conversion date copied at posting. Reads only AccountingDbContext — it must NOT depend on IOpeningBalanceDateService /
/// IOpeningBalanceDateProvider (Finance's date service consumes every lock source: that would be a DI cycle).
/// </summary>
internal sealed class GlOpeningDateLockSource : IOpeningDateLockSource
{
    private readonly AccountingDbContext _db;

    public GlOpeningDateLockSource(AccountingDbContext db) => _db = db;

    public string Description => "General ledger opening balances";

    public async Task<DateOnly?> GetLockedDateAsync(Guid organizationId, CancellationToken ct = default) =>
        await _db.CoaSetupStatuses.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.OpeningBalancesPostedAt != null)
            .Select(s => s.ConversionDate)
            .FirstOrDefaultAsync(ct);
}
