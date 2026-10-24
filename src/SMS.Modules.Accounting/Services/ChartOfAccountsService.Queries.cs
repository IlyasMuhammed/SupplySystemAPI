using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

internal sealed partial class ChartOfAccountsService
{
    private static T Fill<T>(T m, ChartOfAccount a, ChartOfAccount? parent, string? deleteBlockedReason) where T : AccountModel
    {
        m.CanDelete = deleteBlockedReason is null;
        m.DeleteBlockedReason = deleteBlockedReason;
        m.Uuid = a.Uuid;
        m.AccountCode = a.AccountCode;
        m.AccountName = a.AccountName;
        m.ParentUuid = parent?.Uuid;
        m.ParentCode = parent?.AccountCode;
        m.MaterializedPath = a.MaterializedPath;
        m.Level = a.Level;
        m.RootType = a.RootType;
        m.IsGroup = a.IsGroup;
        m.AccountType = a.AccountType;
        m.Description = a.Description;
        m.ExternalCode = a.ExternalCode;
        m.IsSystemAccount = a.IsSystemAccount;
        m.IsActive = a.IsActive;
        m.HasTransactions = a.HasTransactions;
        m.OpeningBalanceDebit = a.OpeningBalanceDebit;
        m.OpeningBalanceCredit = a.OpeningBalanceCredit;
        m.CurrentBalance = a.CurrentBalance;
        m.SortOrder = a.SortOrder;
        return m;
    }

    /// <summary>Depth-first: roots by code, children by sort order then code.</summary>
    private static List<ChartOfAccount> DepthFirst(IReadOnlyList<ChartOfAccount> accounts)
    {
        var ids = accounts.Select(a => a.Id).ToHashSet();
        var children = accounts.Where(a => a.ParentAccountId is { } p && ids.Contains(p)).ToLookup(a => a.ParentAccountId!.Value);
        var result = new List<ChartOfAccount>(accounts.Count);
        void Visit(ChartOfAccount a)
        {
            result.Add(a);
            foreach (var c in children[a.Id].OrderBy(c => c.SortOrder).ThenBy(c => c.AccountCode, StringComparer.Ordinal)) Visit(c);
        }
        foreach (var root in accounts.Where(a => a.ParentAccountId is not { } p || !ids.Contains(p)).OrderBy(a => a.AccountCode, StringComparer.Ordinal))
            Visit(root);
        return result;
    }

    /// <summary>Models with parent refs and the delete refusal of every account (one batch, see DeleteBlockedReasonsAsync).</summary>
    private async Task<List<AccountModel>> ToModelsAsync(Guid org, IReadOnlyList<ChartOfAccount> accounts, IReadOnlyDictionary<int, ChartOfAccount> parents,
        CancellationToken ct)
    {
        var reasons = await DeleteBlockedReasonsAsync(org, accounts, ct);
        return accounts.Select(a => Fill(new AccountModel(), a, a.ParentAccountId is { } p ? parents.GetValueOrDefault(p) : null, reasons[a.Id])).ToList();
    }

    public async Task<ChartTreeModel> GetTreeAsync(AccountingCaller caller, bool includeInactive = false, CancellationToken ct = default)
    {
        caller.Require(caller.CanReadGlLookups); // A47 SR-FE-2
        var all = await Accounts(caller.OrganizationId).AsNoTracking().Where(a => includeInactive || a.IsActive).ToListAsync(ct);
        var byId = all.ToDictionary(a => a.Id);
        var reasons = await DeleteBlockedReasonsAsync(caller.OrganizationId, all, ct);
        var nodes = new Dictionary<int, AccountTreeNodeModel>();
        var roots = new List<AccountTreeNodeModel>();
        foreach (var a in DepthFirst(all))
        {
            var parent = a.ParentAccountId is { } p ? byId.GetValueOrDefault(p) : null;
            var node = Fill(new AccountTreeNodeModel(), a, parent, reasons[a.Id]);
            nodes[a.Id] = node;
            if (parent is not null && nodes.TryGetValue(parent.Id, out var parentNode)) parentNode.Children.Add(node);
            else roots.Add(node);
        }
        var active = all.Where(a => a.IsActive).ToList();
        return new ChartTreeModel
        {
            Roots = roots,
            Summary = new ChartSummaryModel
            {
                TotalAccounts = active.Count,
                PostableAccounts = active.Count(a => !a.IsGroup),
                LastModifiedAt = all.Count == 0 ? null : all.Max(a => a.ModifiedAt),
            },
        };
    }

    public async Task<List<AccountModel>> GetFlatAsync(AccountingCaller caller, AccountFlatQuery query, CancellationToken ct = default)
    {
        caller.Require(caller.CanReadGlLookups); // A47 SR-FE-2 — the A47 pages' account pickers
        var q = Accounts(caller.OrganizationId).AsNoTracking();
        if (query.Level is { } level) q = q.Where(a => a.Level == level);
        if (!string.IsNullOrWhiteSpace(query.RootType)) q = q.Where(a => a.RootType == query.RootType);
        if (query.IsGroup is { } isGroup) q = q.Where(a => a.IsGroup == isGroup);
        if (query.IsActive is { } isActive) q = q.Where(a => a.IsActive == isActive);
        if (!string.IsNullOrWhiteSpace(query.AccountType)) q = q.Where(a => a.AccountType == query.AccountType);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            q = q.Where(a => a.AccountCode.StartsWith(s) || a.AccountName.Contains(s));
        }
        var list = await q.ToListAsync(ct);
        var parentIds = list.Where(a => a.ParentAccountId != null).Select(a => a.ParentAccountId!.Value).Distinct().ToList();
        var parents = await Accounts(caller.OrganizationId).AsNoTracking().Where(a => parentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return await ToModelsAsync(caller.OrganizationId, list.OrderBy(a => a.MaterializedPath, StringComparer.Ordinal).ToList(), parents, ct);
    }

    public async Task<AccountDetailModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        var a = await FindAsync(org, uuid, ct);
        var parent = a.ParentAccountId is { } p ? await Accounts(org).AsNoTracking().FirstOrDefaultAsync(x => x.Id == p, ct) : null;
        var reason = await DeleteBlockedReasonAsync(a, ct);
        var detail = Fill(new AccountDetailModel(), a, parent, reason);
        detail.Parent = parent is null ? null : new AccountParentRef { Uuid = parent.Uuid, AccountCode = parent.AccountCode, AccountName = parent.AccountName };
        detail.ChildrenCount = await Accounts(org).CountAsync(x => x.ParentAccountId == a.Id && x.IsActive, ct);
        detail.CreatedAt = a.CreatedAt;
        detail.ModifiedAt = a.ModifiedAt;
        return detail;
    }

    /// <summary>§2.3 — the parent's code plus the first free digit (then two digits) not used by an active account.</summary>
    public async Task<SuggestCodeModel> SuggestCodeAsync(AccountingCaller caller, Guid parentUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        var parent = await Accounts(org).AsNoTracking().FirstOrDefaultAsync(a => a.Uuid == parentUuid && a.IsActive, ct)
            ?? throw new NotFoundException("Parent account not found.");
        if (!parent.IsGroup) throw new BadRequestException("Accounts can only be added under a group account (levels 1–3).");
        var prefix = parent.AccountCode;
        var used = (await Accounts(org).Where(a => a.IsActive && a.AccountCode.StartsWith(prefix)).Select(a => a.AccountCode).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var candidates = Enumerable.Range(1, 9).Select(d => prefix + d).Concat(Enumerable.Range(10, 90).Select(d => prefix + d));
        var code = candidates.FirstOrDefault(c => c.Length <= GlLimits.MaxCodeLength && !used.Contains(c))
            ?? throw new ConflictException($"No free account code is left under {prefix}.");
        return new SuggestCodeModel { Code = code };
    }

    public async Task<GlFile> ExportAsync(AccountingCaller caller, string? format, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        var f = CoaFile.NormalizeFormat(format);
        var accounts = await Accounts(org).AsNoTracking().Where(a => a.IsActive).ToListAsync(ct);
        var defaults = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && d.AccountId != null).ToListAsync(ct);
        var keyByAccount = defaults.GroupBy(d => d.AccountId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(d => d.SortOrder).First().DefaultKey);
        var byId = accounts.ToDictionary(a => a.Id);
        var rows = DepthFirst(accounts).Select(a => new CoaRow
        {
            Code = a.AccountCode, Name = a.AccountName, ParentCode = a.ParentAccountId is { } p ? byId.GetValueOrDefault(p)?.AccountCode : null,
            Level = a.Level, RootType = a.RootType, IsGroup = a.IsGroup, AccountType = a.AccountType, Description = a.Description, SortOrder = a.SortOrder,
            IsDefaultAccount = keyByAccount.ContainsKey(a.Id), DefaultAccountKey = keyByAccount.GetValueOrDefault(a.Id),
        }).ToList();
        return f == CoaFile.Csv
            ? new GlFile(CoaFile.WriteCsv(rows), "chart-of-accounts.csv", "text/csv")
            : new GlFile(CoaFile.WriteJson(new CoaFile.JsonHeader(null, "Chart of Accounts", null, null, "1.0"), rows), "chart-of-accounts.json", "application/json");
    }

    public async Task<List<AccountModel>> GetSubtreeAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        var root = await FindAsync(org, uuid, ct);
        var prefix = root.MaterializedPath + "/";
        var list = await Accounts(org).AsNoTracking().Where(a => a.IsActive && a.MaterializedPath.StartsWith(prefix)).ToListAsync(ct);
        var parents = list.Append(root).ToDictionary(a => a.Id);
        return await ToModelsAsync(org, list.OrderBy(a => a.MaterializedPath, StringComparer.Ordinal).ToList(), parents, ct);
    }

    public async Task<List<AccountModel>> GetAncestorsAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        var account = await FindAsync(org, uuid, ct);
        var ids = await AncestorIdsAsync(org, account, ct);
        var list = await Accounts(org).AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        var byId = list.ToDictionary(a => a.Id);
        return await ToModelsAsync(org, list.OrderBy(a => a.Level).ToList(), byId, ct);
    }

    /// <summary>§6.3 — ancestry by recursive CTE on SQL Server; a parent walk on other providers (tests).</summary>
    internal async Task<List<int>> AncestorIdsAsync(Guid org, ChartOfAccount account, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            return await _db.Database.SqlQuery<int>($"""
                WITH Up AS (
                    SELECT [Id], [ParentAccountId], 0 AS [Depth] FROM [finance].[ChartOfAccounts] WHERE [Id] = {account.Id} AND [OrganizationId] = {org}
                    UNION ALL
                    SELECT c.[Id], c.[ParentAccountId], u.[Depth] + 1 FROM [finance].[ChartOfAccounts] c
                    INNER JOIN Up u ON c.[Id] = u.[ParentAccountId] WHERE c.[OrganizationId] = {org}
                )
                SELECT [Id] AS [Value] FROM Up WHERE [Depth] > 0
                """).ToListAsync(ct);
        }
        var ids = new List<int>();
        var parentId = account.ParentAccountId;
        while (parentId is { } p && ids.Count < 4)
        {
            ids.Add(p);
            parentId = await Accounts(org).Where(a => a.Id == p).Select(a => a.ParentAccountId).FirstOrDefaultAsync(ct);
        }
        return ids;
    }
}
