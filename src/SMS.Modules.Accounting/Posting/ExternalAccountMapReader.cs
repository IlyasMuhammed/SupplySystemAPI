using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using DomainSystemTypes = SMS.Modules.Accounting.Domain.ExternalSystemTypes;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// A44 D-16 — the A42 chart ↔ external account mappings (<c>finance.ExternalSystemMappings</c>) for the Integration module,
/// for the ambient organization. Reads: active mappings by leaf code, and by accounting default key (the external account of
/// the account assigned to each default). Writes follow the A42 mapping rules (D-58): leaf + active account of the same
/// organization, one mapping per (system, account), external code ≤ 100; a null external code deletes; all rows are checked
/// before anything is saved. New rows are Outbound. A written row gets SyncStatus Success, LastSyncedAt now and the caller's
/// note (e.g. "Auto-mapped by name") in SyncErrorMessage — A42 has no note column (D-46).
/// </summary>
internal sealed class ExternalAccountMapReader : IExternalAccountMapReader
{
    private readonly AccountingDbContext _db;
    private readonly ITenantContext _tenant;

    public ExternalAccountMapReader(AccountingDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<ExternalAccountMap> GetAsync(string systemType, CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        var type = Normalize(systemType);
        var rows = await _db.ExternalSystemMappings.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrganizationId == org && m.SystemType == type && m.IsActive)
            .Join(_db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org && a.IsActive && !a.IsGroup),
                m => m.AccountId, a => a.Id,
                (m, a) => new { a.Id, a.Uuid, a.AccountCode, a.AccountName, m.ExternalAccountCode, m.ExternalAccountName })
            .ToListAsync(ct);
        var byCode = rows.GroupBy(r => r.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var r = g.First();
                return new ExternalAccountMapEntry(r.Uuid, r.AccountCode, r.AccountName, r.ExternalAccountCode, r.ExternalAccountName);
            }, StringComparer.OrdinalIgnoreCase);

        var defaults = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && d.AccountId != null)
            .Select(d => new { d.DefaultKey, d.AccountId })
            .ToListAsync(ct);
        var byAccountId = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().ExternalAccountCode);
        var byDefault = defaults.Where(d => byAccountId.ContainsKey(d.AccountId!.Value))
            .ToDictionary(d => d.DefaultKey, d => byAccountId[d.AccountId!.Value], StringComparer.OrdinalIgnoreCase);

        return new ExternalAccountMap { ByAccountCode = byCode, ByDefaultKey = byDefault };
    }

    public async Task<IReadOnlyList<ChartAccountRef>> ListLeafAccountsAsync(CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        return await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && !a.IsGroup)
            .OrderBy(a => a.AccountCode)
            .Select(a => new ChartAccountRef(a.Uuid, a.AccountCode, a.AccountName, a.AccountType, a.IsActive))
            .ToListAsync(ct);
    }

    public async Task<int> UpsertMappingsAsync(string systemType, IReadOnlyList<AccountMappingUpsert> rows, CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        var type = Normalize(systemType);
        if (rows is null || rows.Count == 0) return 0;

        var uuids = rows.Select(r => r.AccountUuid).Distinct().ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && uuids.Contains(a.Uuid)).ToDictionaryAsync(a => a.Uuid, ct);
        var ids = accounts.Values.Select(a => a.Id).ToList();
        var existing = await _db.ExternalSystemMappings.IgnoreQueryFilters()
            .Where(m => m.OrganizationId == org && m.SystemType == type && ids.Contains(m.AccountId))
            .ToDictionaryAsync(m => m.AccountId, ct);

        // Validate everything first: all or nothing (like the A42 batch saves).
        var problems = new List<string>();
        foreach (var row in rows)
        {
            if (!accounts.TryGetValue(row.AccountUuid, out var account)) { problems.Add($"Account {row.AccountUuid} not found."); continue; }
            if (row.ExternalAccountCode is null) continue;
            if (account.IsGroup) problems.Add($"Account {account.AccountCode}: only Level 4 accounts can be mapped.");
            else if (!account.IsActive) problems.Add($"Account {account.AccountCode}: mapped account must be active.");
            if (string.IsNullOrWhiteSpace(row.ExternalAccountCode) || row.ExternalAccountCode.Trim().Length > 100)
                problems.Add($"Account {account.AccountCode}: the external account code is required (at most 100 characters).");
        }
        if (problems.Count > 0) throw new BadRequestException(string.Join(" ", problems));

        var changed = 0;
        foreach (var row in rows.GroupBy(r => r.AccountUuid).Select(g => g.Last()))
        {
            var account = accounts[row.AccountUuid];
            var mapping = existing.GetValueOrDefault(account.Id);
            if (row.ExternalAccountCode is null)
            {
                if (mapping is null) continue;
                _db.ExternalSystemMappings.Remove(mapping);
                existing.Remove(account.Id);
                changed++;
                continue;
            }
            var code = row.ExternalAccountCode.Trim();
            var name = string.IsNullOrWhiteSpace(row.ExternalAccountName) ? null : FinancialPostingGateway.Truncate(row.ExternalAccountName.Trim(), 200);
            if (mapping is null)
            {
                mapping = new ExternalSystemMapping
                {
                    OrganizationId = org,
                    SystemType     = type,
                    SystemName     = DomainSystemTypes.DisplayNames.GetValueOrDefault(type, type),
                    AccountId      = account.Id,
                    SyncDirection  = SyncDirections.Outbound,
                };
                _db.ExternalSystemMappings.Add(mapping);
                existing[account.Id] = mapping;
            }
            else if (mapping.ExternalAccountCode == code && mapping.ExternalAccountName == name && mapping.IsActive) continue;
            mapping.ExternalAccountCode = code;
            mapping.ExternalAccountName = name;
            mapping.IsActive = true;
            mapping.SyncStatus = SyncStatuses.Success;
            mapping.LastSyncedAt = DateTime.UtcNow;
            mapping.SyncErrorMessage = string.IsNullOrWhiteSpace(row.Note) ? null : FinancialPostingGateway.Truncate(row.Note.Trim(), 1000);
            changed++;
        }
        await _db.SaveChangesAsync(ct);
        return changed;
    }

    private static string Normalize(string systemType)
    {
        var type = (systemType ?? string.Empty).Trim().ToUpperInvariant();
        if (!DomainSystemTypes.All.Contains(type)) throw new BadRequestException($"Unknown external system type {systemType}.");
        return type;
    }
}
