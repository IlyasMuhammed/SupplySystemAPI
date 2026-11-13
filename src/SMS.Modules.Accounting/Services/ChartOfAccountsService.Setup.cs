using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>What <see cref="ChartOfAccountsService.ApplySetupChartAsync"/> did.</summary>
internal sealed class CoaSetupOutcome
{
    /// <summary>True: the chart was empty and replaced by the rows (A42 replace path); false: merge.</summary>
    public bool Replaced { get; init; }
    /// <summary>Accounts created, by code.</summary>
    public List<ChartOfAccount> Created { get; } = [];
    /// <summary>Rows whose code was already an active account (never changed), by code.</summary>
    public List<ChartOfAccount> Existing { get; } = [];
    public int DefaultsAssigned { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// A50 D-12 / D-51 — the onboarding (setup wizard) chart apply, next to the A42 replace paths:
/// <list type="bullet">
/// <item><b>Empty chart</b> (no account at all) and A42 still allows a replacement (status NotStarted/AccountsCreated, no fiscal
/// year, opening balances not posted, no posted journal — the same rules as apply-template): the A42 replace with the rows as
/// an in-memory template (D-9 system accounts, 3900, defaults the rows mark).</item>
/// <item><b>Otherwise a merge</b>: rows whose code is not an active account are inserted under the active group account that
/// has the row's parent code (level = parent + 1, same root type); a row whose parent is missing, a leaf, at the wrong level or of
/// another root is skipped with a warning (and so are its descendants). Existing accounts are never changed or removed. The
/// 2000-account limit (R10) stops the insert with a warning. 3900 (+39/390) is added when missing (D-9). Defaults the rows mark
/// (and DEFAULT_OPENING_BALANCE → 3900) are assigned only where the default is still unassigned.</item>
/// </list>
/// The rows must have passed <see cref="CoaValidator"/>. One transaction, then the A42 event + status recompute.
/// </summary>
internal sealed partial class ChartOfAccountsService
{
    internal const string SetupFormat = "SETUP_WIZARD";

    /// <summary>Null when A42 allows replacing the chart now; else the reason (R13 / BR-TPL-004).</summary>
    internal async Task<string?> ReplaceRefusalAsync(AccountingCaller caller, CancellationToken ct)
    {
        try
        {
            await EnsureReplaceableAsync(caller, ct, uploadErrors: null);
            return null;
        }
        catch (ConflictException ex)
        {
            return ex.Message;
        }
    }

    internal async Task<CoaSetupOutcome> ApplySetupChartAsync(AccountingCaller caller, List<CoaRow> rows, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var empty = !await Accounts(org).AnyAsync(ct);
        CoaSetupOutcome outcome;
        if (empty && await ReplaceRefusalAsync(caller, ct) is null)
        {
            var (_, defaults) = await ReplaceChartAsync(caller, rows, null, ct);
            outcome = new CoaSetupOutcome { Replaced = true, DefaultsAssigned = defaults };
            outcome.Created.AddRange(await Accounts(org).AsNoTracking().Where(a => a.IsActive).ToListAsync(ct));
        }
        else
        {
            outcome = await MergeChartAsync(caller, rows, ct);
        }

        if (outcome.Created.Count > 0 || outcome.DefaultsAssigned > 0)
            await _events.PublishAsync(new CoaUploadedEvent(org, "setup-wizard", outcome.Created.Count, SetupFormat, caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return outcome;
    }

    private async Task<CoaSetupOutcome> MergeChartAsync(AccountingCaller caller, List<CoaRow> rows, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        // D-9 — 3900 (+ 39/390) only when the organization has no Opening Balance Equity yet.
        if (!await Accounts(org).AnyAsync(a => a.IsActive && a.AccountCode == SystemAccounts.OpeningBalanceEquityCode, ct))
            rows = WithOpeningBalanceEquity(rows);
        var outcome = new CoaSetupOutcome { Replaced = false };

        await _db.InTransactionAsync(async () =>
        {
            var active = await Accounts(org).Where(a => a.IsActive).ToListAsync(ct);
            var byCode = active.ToDictionary(a => a.AccountCode, StringComparer.Ordinal);
            var count = active.Count;
            var skippedCodes = new HashSet<string>(StringComparer.Ordinal);
            var limitHit = false;

            foreach (var r in rows.OrderBy(r => r.Level).ThenBy(r => r.SortOrder))
            {
                if (byCode.TryGetValue(r.Code, out var have))
                {
                    if (!outcome.Created.Contains(have)) outcome.Existing.Add(have);
                    continue;
                }
                ChartOfAccount? parent = null;
                if (r.Level > 1)
                {
                    if (r.ParentCode is null || skippedCodes.Contains(r.ParentCode)) { skippedCodes.Add(r.Code); continue; }
                    if (!byCode.TryGetValue(r.ParentCode, out parent) || !parent.IsGroup || parent.Level != r.Level - 1 || parent.RootType != r.RootType)
                    {
                        outcome.Warnings.Add($"Account {r.Code} {r.Name} was not added: its parent {r.ParentCode} is not a level {r.Level - 1} {r.RootType} group in this chart.");
                        skippedCodes.Add(r.Code);
                        continue;
                    }
                }
                if (count >= GlLimits.MaxAccounts)
                {
                    if (!limitHit) outcome.Warnings.Add($"A tenant can have at most {GlLimits.MaxAccounts} accounts: the remaining defaults were not added.");
                    limitHit = true;
                    skippedCodes.Add(r.Code);
                    continue;
                }
                var level = r.Level!.Value;
                var account = new ChartOfAccount
                {
                    OrganizationId = org,
                    AccountCode = r.Code,
                    AccountName = r.Name,
                    Parent = parent,
                    MaterializedPath = (parent?.MaterializedPath ?? "") + "/" + r.Code,
                    Level = (byte)level,
                    RootType = r.RootType,
                    IsGroup = level < 4,
                    AccountType = level == 4 ? r.AccountType : null,
                    Description = r.Description,
                    IsSystemAccount = level == 1 || r.Code == SystemAccounts.OpeningBalanceEquityCode,
                    SortOrder = r.SortOrder,
                    CreatedBy = caller.UserIdOrNull,
                    ModifiedBy = caller.UserIdOrNull,
                };
                _db.ChartOfAccounts.Add(account);
                byCode[r.Code] = account;
                outcome.Created.Add(account);
                count++;
            }
            await _db.SaveChangesAsync(ct);

            // Defaults the rows mark, only where unassigned (never re-pointed).
            var keyed = rows.Where(r => r.IsDefaultAccount && r.DefaultAccountKey is not null && r.Level == 4)
                .GroupBy(r => r.DefaultAccountKey!).ToDictionary(g => g.Key, g => g.First().Code);
            keyed[DefaultKeys.OpeningBalance] = SystemAccounts.OpeningBalanceEquityCode;
            var defaults = await _db.AccountingDefaults.IgnoreQueryFilters().Where(d => d.OrganizationId == org && d.AccountId == null).ToListAsync(ct);
            foreach (var d in defaults)
            {
                if (!keyed.TryGetValue(d.DefaultKey, out var code) || !byCode.TryGetValue(code, out var account) || account.IsGroup || !account.IsActive) continue;
                d.AccountId = account.Id;
                d.ModifiedBy = caller.UserIdOrNull;
                outcome.DefaultsAssigned++;
            }
            await _db.SaveChangesAsync(ct);
        }, ct);
        return outcome;
    }

    /// <summary>
    /// D-51 — accounts something depends on (BR-50-008): posted transactions, any journal line (draft or posted), a non-zero
    /// opening balance or an external mapping.
    /// </summary>
    internal async Task<HashSet<int>> ReferencedAccountIdsAsync(Guid org, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var used = new HashSet<int>();
        if (ids.Count == 0) return used;
        used.UnionWith(await Accounts(org).Where(a => ids.Contains(a.Id) && a.HasTransactions).Select(a => a.Id).ToListAsync(ct));
        used.UnionWith(await _db.JournalEntryLines.IgnoreQueryFilters().Where(l => l.OrganizationId == org && ids.Contains(l.AccountId))
            .Select(l => l.AccountId).Distinct().ToListAsync(ct));
        used.UnionWith(await _db.OpeningBalanceEntries.IgnoreQueryFilters()
            .Where(e => e.OrganizationId == org && ids.Contains(e.AccountId) && (e.DebitAmount != 0 || e.CreditAmount != 0))
            .Select(e => e.AccountId).Distinct().ToListAsync(ct));
        used.UnionWith(await _db.ExternalSystemMappings.IgnoreQueryFilters().Where(m => m.OrganizationId == org && ids.Contains(m.AccountId))
            .Select(m => m.AccountId).Distinct().ToListAsync(ct));
        return used;
    }

    /// <summary>D-51 Full reset: the A42 replace (every account, custom ones included) with the rows. Caller checked references.</summary>
    internal async Task<CoaSetupOutcome> ReplaceForSetupAsync(AccountingCaller caller, List<CoaRow> rows, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        await EnsureReplaceableAsync(caller, ct, uploadErrors: null);
        var (_, defaults) = await ReplaceChartAsync(caller, rows, null, ct);
        var outcome = new CoaSetupOutcome { Replaced = true, DefaultsAssigned = defaults };
        outcome.Created.AddRange(await Accounts(org).AsNoTracking().Where(a => a.IsActive).ToListAsync(ct));
        await _events.PublishAsync(new CoaUploadedEvent(org, "setup-wizard-reset", outcome.Created.Count, SetupFormat, caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return outcome;
    }

    /// <summary>The organization's accounts (tracked), for the setup seeder's status / overwrite.</summary>
    internal Task<List<ChartOfAccount>> AllAccountsAsync(Guid org, CancellationToken ct) => Accounts(org).ToListAsync(ct);

    internal Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
