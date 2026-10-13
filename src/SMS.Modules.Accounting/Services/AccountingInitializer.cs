using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// D-15 — creates an organization's GL rows on its first access: the CoaSetupStatuses row (NotStarted) and the 16
/// AccountingDefaults rows (§8.3, unassigned). Idempotent and safe under concurrency (a lost insert race re-reads). Every
/// GL service calls <see cref="EnsureAsync"/> before reading the organization's setup; it is cached per request scope.
/// Existing default rows have their label, category, required flag, expected type, help text and order refreshed from the
/// catalog — never their assigned account.
/// </summary>
public interface IAccountingInitializer
{
    Task EnsureAsync(Guid organizationId, CancellationToken ct = default);
}

internal sealed class AccountingInitializer : IAccountingInitializer
{
    private readonly AccountingDbContext _db;
    private readonly HashSet<Guid> _ensured = [];

    public AccountingInitializer(AccountingDbContext db) => _db = db;

    public async Task EnsureAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || _ensured.Contains(organizationId)) return;

        for (var attempt = 1; ; attempt++)
        {
            var added = new List<object>();
            try
            {
                if (!await _db.CoaSetupStatuses.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == organizationId, ct))
                {
                    var status = new CoaSetupStatus { OrganizationId = organizationId, Status = SetupStatuses.NotStarted };
                    _db.CoaSetupStatuses.Add(status);
                    added.Add(status);
                }

                var rows = await _db.AccountingDefaults.IgnoreQueryFilters()
                    .Where(d => d.OrganizationId == organizationId).ToListAsync(ct);
                foreach (var def in DefaultKeys.Definitions)
                {
                    var row = rows.FirstOrDefault(r => r.DefaultKey == def.Key);
                    if (row is null)
                    {
                        row = new AccountingDefault { OrganizationId = organizationId, DefaultKey = def.Key };
                        _db.AccountingDefaults.Add(row);
                        added.Add(row);
                    }
                    if (row.DefaultLabel != def.Label) row.DefaultLabel = def.Label;
                    if (row.Category != def.Category) row.Category = def.Category;
                    if (row.IsRequired != def.IsRequired) row.IsRequired = def.IsRequired;
                    if (row.ExpectedAccountType != def.ExpectedAccountType) row.ExpectedAccountType = def.ExpectedAccountType;
                    if (row.HelpText != def.HelpText) row.HelpText = def.HelpText;
                    if (row.SortOrder != def.SortOrder) row.SortOrder = def.SortOrder;
                }

                if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync(ct);
                _ensured.Add(organizationId);
                return;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Another request created the same rows first (unique index): forget ours and read theirs.
                foreach (var entity in added) _db.Entry(entity).State = EntityState.Detached;
            }
        }
    }
}
