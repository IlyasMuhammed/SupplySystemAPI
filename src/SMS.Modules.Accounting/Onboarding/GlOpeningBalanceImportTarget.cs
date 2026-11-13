using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Onboarding;

/// <summary>
/// A50 D-24 / D-54 — GL_OPENING_BALANCE: a trial balance (AccountCode, Debit, Credit, Notes) into the A42 opening balance
/// <b>draft</b> (the batch save behind <c>PUT api/finance/opening-balances</c>). Posting stays the separate "Post opening
/// balances" action (A42 §10). Validation (BR-50-011), over the whole file: the account exists, is active, is a level 4 leaf and
/// is not 3900 (the plug); amounts ≥ 0; not both sides; not both zero; and the rows that pass must balance — otherwise every
/// row carries UNBALANCED and nothing is imported (TS-50-016). An account with a draft already is a duplicate (opening
/// balances are never updated, IMP-OB-06: clear the draft on the GL page first). Refused once the opening balances are posted.
/// </summary>
internal sealed class GlOpeningBalanceImportTarget : IImportTarget
{
    public const string EntityType = "GL_OPENING_BALANCE";
    internal const string FAccount = "account_code", FDebit = "debit", FCredit = "credit", FNotes = "notes";
    internal const string AccountList = "accounts";
    internal const string UnbalancedCode = "UNBALANCED", DraftExistsCode = "DRAFT_EXISTS", DuplicateInFileCode = "DUPLICATE_IN_FILE";

    private readonly AccountingDbContext _db;
    private readonly IOpeningBalanceService _opening;
    private readonly IAccountingInitializer _init;

    public GlOpeningBalanceImportTarget(AccountingDbContext db, IOpeningBalanceService opening, IAccountingInitializer init)
    {
        _db = db;
        _opening = opening;
        _init = init;
    }

    public ImportProfileDefinition Describe() => new(
        EntityType, "General ledger opening balances",
        "The trial balance at the opening balance date: one row per leaf account with its debit or its credit. Totals must balance. "
      + "Rows go into the draft opening balances; post them from General ledger setup.",
        ImportOrder: 14, IsOpeningBalance: true, MatchFields: [FAccount],
        Fields:
        [
            new(FAccount, "Account Code", "An active level 4 account of the chart (not 3900).", ImportFieldTypes.Lookup, IsRequired: true, IsKey: true,
                SortOrder: 1, SampleValue: "1112", MaxLength: GlLimits.MaxCodeLength, LookupList: AccountList,
                Aliases: ["account", "gl_account", "account_no", "account_number", "code", "hkont", "saknr"]),
            new(FDebit, "Debit", "Blank or 0 when the balance is a credit.", ImportFieldTypes.Decimal, IsRequired: false, IsKey: false,
                SortOrder: 2, SampleValue: "250000.00", MinValue: 0m, DecimalPlaces: 2, Aliases: ["dr", "debit_amount"]),
            new(FCredit, "Credit", "Blank or 0 when the balance is a debit.", ImportFieldTypes.Decimal, IsRequired: false, IsKey: false,
                SortOrder: 3, SampleValue: "0", MinValue: 0m, DecimalPlaces: 2, Aliases: ["cr", "credit_amount"]),
            new(FNotes, "Notes", null, ImportFieldTypes.Text, IsRequired: false, IsKey: false, SortOrder: 4, SampleValue: null, MaxLength: 500,
                Aliases: ["memo", "description", "account_name"])
        ],
        AllowUpdate: false,
        RequiresModule: ModuleCodes.GeneralLedger,
        RequiredPermissions: [PermissionCodes.GL_SETUP_MANAGE]);

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var codes = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.IsActive && !a.IsGroup && a.Level == 4 && a.AccountCode != SystemAccounts.OpeningBalanceEquityCode)
            .OrderBy(a => a.AccountCode).Select(a => a.AccountCode).Take(1000).ToListAsync(ct);
        return [new ImportTemplateList(AccountList, codes)];
    }

    private sealed class Plan
    {
        public int RowNumber { get; init; }
        public ChartOfAccount? Account { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public string? Notes { get; set; }
        public Guid? Existing { get; set; }
        public List<ImportFieldError> Errors { get; } = [];
    }

    private static string? Value(IReadOnlyDictionary<string, string?> v, string key) =>
        v.TryGetValue(key, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static decimal? Amount(IReadOnlyDictionary<string, string?> v, string key, List<ImportFieldError> errors)
    {
        var s = Value(v, key);
        if (s is null) return 0m;
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        errors.Add(new(key, "INVALID_DECIMAL", $"{s} is not a number.", s));
        return null;
    }

    private async Task<List<Plan>> PlanAsync(Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, CancellationToken ct)
    {
        await _init.EnsureAsync(org, ct);
        var codes = rows.Select(r => Value(r.Values, FAccount)).Where(c => c is not null).Distinct().ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && codes.Contains(a.AccountCode)).ToListAsync(ct);
        var drafts = await _db.OpeningBalanceEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org).Select(e => new { e.AccountId, e.Uuid }).ToListAsync(ct);
        var posted = await _db.CoaSetupStatuses.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.OpeningBalancesPostedAt != null, ct);

        var plans = new List<Plan>(rows.Count);
        foreach (var (rowNumber, values) in rows)
        {
            var p = new Plan { RowNumber = rowNumber, Notes = Value(values, FNotes) };
            plans.Add(p);
            if (posted) { p.Errors.Add(new(FAccount, "ALREADY_POSTED", OpeningBalanceService.AlreadyPostedMessage, null)); continue; }

            var code = Value(values, FAccount);
            var account = code is null ? null
                : accounts.Where(a => a.AccountCode == code).OrderByDescending(a => a.IsActive).FirstOrDefault();
            if (account is null) p.Errors.Add(new(FAccount, "LOOKUP_NOT_FOUND", $"Account {code} is not in the chart of accounts.", code));
            else if (!account.IsActive) p.Errors.Add(new(FAccount, "INACTIVE", $"Account {code} is inactive.", code));
            else if (account.IsGroup || account.Level != 4) p.Errors.Add(new(FAccount, "NOT_LEAF", $"Account {code} is a group: only level 4 accounts take opening balances.", code));
            else if (account.AccountCode == SystemAccounts.OpeningBalanceEquityCode) p.Errors.Add(new(FAccount, "PLUG_ACCOUNT", OpeningBalanceService.PlugMessage, code));
            else
            {
                p.Account = account;
                p.Existing = drafts.FirstOrDefault(d => d.AccountId == account.Id)?.Uuid;
                // REV50-9 — an error, not only a match: A40 would skip the row, and the imported set would no longer be the balanced one.
                if (p.Existing is not null)
                    p.Errors.Add(new(FAccount, DraftExistsCode,
                        $"Account {code} already has a draft opening balance. Clear it on the General ledger setup page first.", code));
            }

            var debit = Amount(values, FDebit, p.Errors);
            var credit = Amount(values, FCredit, p.Errors);
            if (debit is { } d && credit is { } c)
            {
                p.Debit = Math.Round(d, 4, MidpointRounding.AwayFromZero);
                p.Credit = Math.Round(c, 4, MidpointRounding.AwayFromZero);
                if (d < 0 || c < 0) p.Errors.Add(new(d < 0 ? FDebit : FCredit, "OUT_OF_RANGE", OpeningBalanceService.NegativeMessage, null));
                else if (d > 0 && c > 0) p.Errors.Add(new(FCredit, "BOTH_SIDES", OpeningBalanceService.BothSidesMessage, null));
                else if (d == 0 && c == 0) p.Errors.Add(new(FDebit, "ZERO_AMOUNT", "Enter a debit or a credit greater than zero.", null));
            }
            if (p.Notes is { Length: > 500 }) p.Errors.Add(new(FNotes, "MAX_LENGTH", "Notes can be at most 500 characters.", null));
        }

        // REV50-9 — an account on two rows: every such row is refused (the two could land in different A40 batches).
        foreach (var group in plans.Where(p => p.Account is not null).GroupBy(p => p.Account!.Id).Where(g => g.Count() > 1))
        {
            var rowsText = string.Join(", ", group.Select(p => p.RowNumber));
            foreach (var p in group)
                p.Errors.Add(new(FAccount, DuplicateInFileCode,
                    $"Account {p.Account!.AccountCode} is on more than one row (rows {rowsText}). Put its balance on one row.", p.Account.AccountCode));
        }
        return plans;
    }

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        var plans = await PlanAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct);

        // BR-50-011 — the rows that would be imported must balance; otherwise none is.
        var good = plans.Where(p => p.Errors.Count == 0).ToList();
        var debits = good.Sum(p => p.Debit);
        var credits = good.Sum(p => p.Credit);
        if (debits != credits)
        {
            var message = $"Debits do not equal credits: debits {debits.ToString("#,##0.00", CultureInfo.InvariantCulture)}, "
                        + $"credits {credits.ToString("#,##0.00", CultureInfo.InvariantCulture)} (difference "
                        + $"{Math.Abs(debits - credits).ToString("#,##0.00", CultureInfo.InvariantCulture)}). Nothing is imported until the file balances.";
            foreach (var p in good) p.Errors.Add(new(FDebit, UnbalancedCode, message, null));
        }
        return plans.Select(p => new ImportRowCheck(p.RowNumber, p.Existing, p.Errors)).ToList();
    }

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return [];
        if (rows.FirstOrDefault(r => !string.Equals(r.Action, "CREATE", StringComparison.OrdinalIgnoreCase)) is { } update)
            throw new BadRequestException($"Row {update.RowNumber}: opening balances are never updated; clear the draft on the General ledger setup page first.");

        // Balance was checked over the whole file in validation; a batch (100 rows) is a slice of it.
        var plans = await PlanAsync(organizationId, rows.Select(r => (r.RowNumber, r.Values)).ToList(), ct);
        if (plans.FirstOrDefault(p => p.Errors.Count > 0) is { } bad)
            throw new BadRequestException($"Row {bad.RowNumber}: {bad.Errors[0].Message}");
        if (plans.FirstOrDefault(p => p.Existing is not null) is { } dup)
            throw new BadRequestException($"Row {dup.RowNumber}: account {dup.Account!.AccountCode} already has a draft opening balance.");
        if (plans.GroupBy(p => p.Account!.Id).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new BadRequestException($"Row {twice.Skip(1).First().RowNumber}: account {twice.First().Account!.AccountCode} is listed more than once.");

        var caller = new AccountingCaller(organizationId, userId, AccountingCaller.AllGlCodes);
        await _opening.SaveBatchAsync(caller, new SaveOpeningBalancesRequest
        {
            Entries = plans.Select(p => new OpeningEntryInput { AccountUuid = p.Account!.Uuid, DebitAmount = p.Debit, CreditAmount = p.Credit, Notes = p.Notes }).ToList(),
        }, ct);

        var ids = plans.Select(p => p.Account!.Id).ToList();
        var entries = await _db.OpeningBalanceEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && ids.Contains(e.AccountId)).ToDictionaryAsync(e => e.AccountId, e => e.Uuid, ct);
        return plans.Select(p => new ImportRowOutcome(p.RowNumber, "IMPORTED", entries[p.Account!.Id])).ToList();
    }
}
