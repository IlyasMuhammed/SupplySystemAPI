using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>Save-all refused: every problem at once (400; the controller returns the list).</summary>
public sealed class DefaultsValidationException : BadRequestException
{
    public IReadOnlyList<DefaultMessageModel> Problems { get; }

    public DefaultsValidationException(IReadOnlyList<DefaultMessageModel> problems)
        : base(problems.Count == 1 ? problems[0].Message : $"{problems.Count} accounting defaults are invalid.") => Problems = problems;
}

/// <summary>
/// A42 §8 / D-24 — the 16 accounting defaults (rows created lazily, D-15). An assignment must be an active leaf account of
/// the same organization (hard rule); a type other than the expected one is a warning only. DEFAULT_OPENING_BALANCE is
/// assigned automatically to 3900 and locked (D-52). After Complete a required default cannot be cleared (D-30).
/// </summary>
public interface IDefaultsService
{
    Task<AccountingDefaultsModel> GetAsync(AccountingCaller caller, CancellationToken ct = default);
    Task<AccountingDefaultModel> AssignAsync(AccountingCaller caller, string defaultKey, Guid? accountUuid, CancellationToken ct = default);
    Task<AccountingDefaultsModel> SaveAllAsync(AccountingCaller caller, SaveDefaultsRequest request, CancellationToken ct = default);
    Task<DefaultsValidationModel> ValidateAsync(AccountingCaller caller, CancellationToken ct = default);
}

internal sealed class DefaultsService : IDefaultsService
{
    internal const string LockedMessage = "The Opening Balance Equity default is assigned automatically to account 3900 and cannot be changed.";
    internal const string LeafMessage = "Only Level 4 accounts can be assigned as defaults.";
    internal const string InactiveMessage = "The default account must be active.";
    internal const string NotFoundMessage = "Account not found.";
    internal const string RequiredAfterCompleteMessage = "A required default cannot be cleared after the setup is complete.";

    private readonly AccountingDbContext _db;
    private readonly IAccountingInitializer _init;
    private readonly ISetupStatusService _setup;
    private readonly IAccountingEventPublisher _events;

    public DefaultsService(AccountingDbContext db, IAccountingInitializer init, ISetupStatusService setup, IAccountingEventPublisher events)
    {
        _db = db;
        _init = init;
        _setup = setup;
        _events = events;
    }

    public async Task<AccountingDefaultsModel> GetAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        if (await EnsureOpeningBalanceDefaultAsync(org, ct) is { } evt)
        {
            await _events.PublishAsync(evt, ct);
            await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        }
        return await BuildAsync(org, ct);
    }

    public async Task<AccountingDefaultModel> AssignAsync(AccountingCaller caller, string defaultKey, Guid? accountUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var key = NormalizeKey(defaultKey) ?? throw new NotFoundException($"Unknown accounting default {defaultKey}.");
        if (DefaultKeys.IsLocked(key)) throw new BadRequestException(LockedMessage);

        var row = await _db.AccountingDefaults.IgnoreQueryFilters().SingleAsync(d => d.OrganizationId == org && d.DefaultKey == key, ct);
        var complete = await _setup.GetStatusCodeAsync(org, ct) == SetupStatuses.Complete;
        var (account, problem) = await ResolveAsync(org, row, accountUuid, complete, ct);
        if (problem is not null) throw new BadRequestException(problem);

        var events = new List<AccountingDefaultAssignedEvent>();
        if (row.AccountId != account?.Id)
        {
            row.AccountId = account?.Id;
            row.ModifiedBy = caller.UserIdOrNull;
            events.Add(new AccountingDefaultAssignedEvent(org, key, account?.Uuid, account?.AccountCode, caller.UserIdOrNull, caller.UserName));
        }
        if (await EnsureOpeningBalanceDefaultAsync(org, ct, save: false) is { } obe) events.Add(obe);
        await _db.SaveChangesAsync(ct);

        foreach (var e in events) await _events.PublishAsync(e, ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);

        var model = await BuildAsync(org, ct);
        return model.Categories.SelectMany(c => c.Items).Single(i => i.DefaultKey == key);
    }

    public async Task<AccountingDefaultsModel> SaveAllAsync(AccountingCaller caller, SaveDefaultsRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);

        var rows = await _db.AccountingDefaults.IgnoreQueryFilters().Where(d => d.OrganizationId == org).ToListAsync(ct);
        var complete = await _setup.GetStatusCodeAsync(org, ct) == SetupStatuses.Complete;
        var problems = new List<DefaultMessageModel>();
        var changes = new List<(AccountingDefault Row, ChartOfAccount? Account)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in request.Items ?? [])
        {
            var key = NormalizeKey(item.DefaultKey);
            if (key is null)
            {
                problems.Add(new DefaultMessageModel { DefaultKey = item.DefaultKey ?? "", Message = $"Unknown accounting default {item.DefaultKey}." });
                continue;
            }
            if (!seen.Add(key))
            {
                problems.Add(new DefaultMessageModel { DefaultKey = key, Message = $"{key} is listed more than once." });
                continue;
            }
            var row = rows.Single(r => r.DefaultKey == key);
            if (DefaultKeys.IsLocked(key))
            {
                // The locked default may be sent back unchanged, empty or as 3900 (the page posts every row); anything else is refused.
                var current = row.AccountId is { } id
                    ? await _db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.Id == id).Select(a => (Guid?)a.Uuid).FirstOrDefaultAsync(ct)
                    : null;
                var plug = await _db.ChartOfAccounts.IgnoreQueryFilters()
                    .Where(a => a.OrganizationId == org && a.IsActive && !a.IsGroup && a.AccountCode == SystemAccounts.OpeningBalanceEquityCode)
                    .Select(a => (Guid?)a.Uuid).FirstOrDefaultAsync(ct);
                if (item.AccountUuid is { } sent && sent != Guid.Empty && sent != current && sent != plug)
                    problems.Add(new DefaultMessageModel { DefaultKey = key, Message = LockedMessage });
                continue;
            }
            var (account, problem) = await ResolveAsync(org, row, item.AccountUuid, complete, ct);
            if (problem is not null) problems.Add(new DefaultMessageModel { DefaultKey = key, Message = problem });
            else changes.Add((row, account));
        }
        if (problems.Count > 0) throw new DefaultsValidationException(problems);

        var events = new List<AccountingDefaultAssignedEvent>();
        await _db.InTransactionAsync(async () =>
        {
            foreach (var (row, account) in changes)
            {
                if (row.AccountId == account?.Id) continue;
                row.AccountId = account?.Id;
                row.ModifiedBy = caller.UserIdOrNull;
                events.Add(new AccountingDefaultAssignedEvent(org, row.DefaultKey, account?.Uuid, account?.AccountCode, caller.UserIdOrNull, caller.UserName));
            }
            if (await EnsureOpeningBalanceDefaultAsync(org, ct, save: false) is { } obe) events.Add(obe);
            await _db.SaveChangesAsync(ct);
        }, ct);

        foreach (var e in events) await _events.PublishAsync(e, ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return await BuildAsync(org, ct);
    }

    public async Task<DefaultsValidationModel> ValidateAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var model = await BuildAsync(org, ct);
        var items = model.Categories.SelectMany(c => c.Items).ToList();
        var result = new DefaultsValidationModel
        {
            MissingRequired = items.Where(i => i.IsRequired && (i.Account is null || i.Problem is not null)).Select(i => i.DefaultKey).ToList(),
            Problems = items.Where(i => i.Problem is not null).Select(i => new DefaultMessageModel { DefaultKey = i.DefaultKey, Message = i.Problem! }).ToList(),
            Warnings = items.Where(i => i.Warning is not null).Select(i => new DefaultMessageModel { DefaultKey = i.DefaultKey, Message = i.Warning! }).ToList(),
        };
        result.IsValid = result.MissingRequired.Count == 0 && result.Problems.Count == 0;
        return result;
    }

    // ── rules ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var upper = key.Trim().ToUpperInvariant();
        return DefaultKeys.ByKey.ContainsKey(upper) ? upper : null;
    }

    /// <summary>D-24 hard rules: same organization, leaf, active. Null clears (refused for a required default after Complete).</summary>
    private async Task<(ChartOfAccount? Account, string? Problem)> ResolveAsync(Guid org, AccountingDefault row, Guid? accountUuid,
        bool complete, CancellationToken ct)
    {
        if (accountUuid is null || accountUuid == Guid.Empty)
            return (null, complete && row.IsRequired ? RequiredAfterCompleteMessage : null);

        var account = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .SingleOrDefaultAsync(a => a.OrganizationId == org && a.Uuid == accountUuid, ct);
        if (account is null) return (null, NotFoundMessage);
        if (account.IsGroup) return (null, LeafMessage);
        if (!account.IsActive) return (null, InactiveMessage);
        return (account, null);
    }

    /// <summary>
    /// D-52 — DEFAULT_OPENING_BALANCE always points at the active leaf 3900 when the chart has one (whoever built the chart).
    /// Returns the event to publish when it changed.
    /// </summary>
    private async Task<AccountingDefaultAssignedEvent?> EnsureOpeningBalanceDefaultAsync(Guid org, CancellationToken ct, bool save = true)
    {
        var obe = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && a.IsActive && !a.IsGroup && a.AccountCode == SystemAccounts.OpeningBalanceEquityCode)
            .FirstOrDefaultAsync(ct);
        if (obe is null) return null;
        var row = await _db.AccountingDefaults.IgnoreQueryFilters()
            .SingleOrDefaultAsync(d => d.OrganizationId == org && d.DefaultKey == DefaultKeys.OpeningBalance, ct);
        if (row is null || row.AccountId == obe.Id) return null;
        row.AccountId = obe.Id;
        if (save) await _db.SaveChangesAsync(ct);
        return new AccountingDefaultAssignedEvent(org, DefaultKeys.OpeningBalance, obe.Uuid, obe.AccountCode);
    }

    private async Task<AccountingDefaultsModel> BuildAsync(Guid org, CancellationToken ct)
    {
        var rows = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org).ToListAsync(ct);
        var ids = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        var items = rows.Select(r =>
        {
            var account = r.AccountId is { } id ? accounts.GetValueOrDefault(id) : null;
            string? problem = null, warning = null;
            if (r.AccountId is not null && account is null) problem = NotFoundMessage;
            else if (account is { IsActive: false }) problem = $"Account {account.AccountCode} is inactive.";
            else if (account is { IsGroup: true }) problem = $"Account {account.AccountCode} is a group account.";
            else if (account is not null && r.ExpectedAccountType is not null && account.AccountType != r.ExpectedAccountType)
                warning = $"Account {account.AccountCode} is of type {account.AccountType ?? "(none)"}; {r.ExpectedAccountType} is expected.";
            return (r.SortOrder, Model: new AccountingDefaultModel
            {
                DefaultKey          = r.DefaultKey,
                DefaultLabel        = r.DefaultLabel,
                Category            = r.Category,
                IsRequired          = r.IsRequired,
                ExpectedAccountType = r.ExpectedAccountType,
                HelpText            = r.HelpText,
                IsLocked            = DefaultKeys.IsLocked(r.DefaultKey),
                Account             = account is null ? null : new AccountRefModel
                    { Uuid = account.Uuid, AccountCode = account.AccountCode, AccountName = account.AccountName, AccountType = account.AccountType },
                Warning             = warning,
                Problem             = problem,
            });
        })
        .OrderBy(x => x.SortOrder).ThenBy(x => x.Model.DefaultKey, StringComparer.Ordinal)
        .Select(x => x.Model)
        .ToList();

        return new AccountingDefaultsModel
        {
            Categories = DefaultCategories.All
                .Select(c => new AccountingDefaultCategoryModel { Category = c, Items = items.Where(i => i.Category == c).ToList() })
                .Where(c => c.Items.Count > 0)
                .ToList(),
            RequiredCount = items.Count(i => i.IsRequired),
            AssignedRequiredCount = items.Count(i => i.IsRequired && i.Account is not null && i.Problem is null),
        };
    }
}
