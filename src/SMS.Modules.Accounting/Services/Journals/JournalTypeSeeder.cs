using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 §4.3 / D-6 — the 7 system journal types of an organization. Seeded when the A42 setup completes (this handler),
/// by the guarded migration for organizations already set up, and on demand before any journal is written (the A42
/// opening post comes before Complete). Idempotent: a code, a prefix or the single Opening/Closing slot that is already
/// taken (by a custom type) is left alone. On SQL Server an application lock per organization serializes two first posts.
/// </summary>
internal sealed class JournalTypeSeeder : IAccountingEventHandler<CoaSetupCompletedEvent>
{
    private readonly AccountingDbContext _db;

    public JournalTypeSeeder(AccountingDbContext db) => _db = db;

    public Task HandleAsync(CoaSetupCompletedEvent evt, CancellationToken ct = default) => EnsureAsync(evt.OrganizationId, ct);

    /// <summary>Adds the missing system types (and saves them). Returns how many were added.</summary>
    public async Task<int> EnsureAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || (await MissingAsync(organizationId, ct)).Count == 0) return 0;
        var added = 0;
        await _db.InTransactionAsync(async () =>
        {
            if (_db.Database.IsRelational())
            {
                var resource = $"gltypes:{organizationId:N}";
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; IF @r < 0 THROW 51000, 'The journal type lock could not be taken.', 1;", ct);
            }
            var missing = await MissingAsync(organizationId, ct);
            foreach (var d in missing)
                _db.JournalTypes.Add(new JournalType
                {
                    OrganizationId = organizationId, Code = d.Code, Name = d.Name, Type = d.Kind, SequencePrefix = d.SequencePrefix,
                    RequiresPartner = d.RequiresPartner, AutoPost = d.AutoPost, IsSystem = true, IsActive = true, SortOrder = d.SortOrder,
                });
            added = missing.Count;
            if (added > 0) await _db.SaveChangesAsync(ct);
        }, ct);
        return added;
    }

    private async Task<List<SystemJournalTypeDefinition>> MissingAsync(Guid org, CancellationToken ct)
    {
        var existing = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == org)
            .Select(t => new { t.Code, t.SequencePrefix, t.Type }).ToListAsync(ct);
        var local = _db.JournalTypes.Local.Where(t => t.OrganizationId == org).Select(t => new { t.Code, t.SequencePrefix, t.Type });
        var all = existing.Concat(local).ToList();
        return SystemJournalTypes.Definitions
            .Where(d => all.All(t => t.Code != d.Code && t.SequencePrefix != d.SequencePrefix)
                        && !(d.Kind is JournalTypeKinds.Opening or JournalTypeKinds.Closing && all.Any(t => t.Type == d.Kind)))
            .ToList();
    }
}

/// <summary>
/// A47 D-12 — the journal type of a system-written journal (the A44 gateway, the A42 opening post, the year-end close) from
/// its EntryType: <see cref="SystemJournalTypes.ForEntryType"/>; CASH vs BANK by whether a line is on DEFAULT_CASH. A
/// deactivated target type falls back to MISC (a system journal is never refused for its type). Seeds the system types
/// first when they are missing.
/// </summary>
internal sealed class JournalTypeResolver
{
    private readonly AccountingDbContext _db;
    private readonly JournalTypeSeeder _seeder;

    public JournalTypeResolver(AccountingDbContext db, JournalTypeSeeder seeder)
    {
        _db = db;
        _seeder = seeder;
    }

    public Task EnsureSeededAsync(Guid organizationId, CancellationToken ct = default) => _seeder.EnsureAsync(organizationId, ct);

    /// <summary>The journal type of a journal of <paramref name="entryType"/> whose lines are on <paramref name="accountIds"/>.</summary>
    public async Task<JournalType> ResolveAsync(Guid organizationId, string? entryType, IEnumerable<int> accountIds, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(organizationId, ct);
        var usesCash = false;
        if (entryType is not null && SystemJournalTypes.MoneyEntryTypes.Contains(entryType))
        {
            var cash = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.OrganizationId == organizationId && d.DefaultKey == DefaultKeys.Cash).Select(d => d.AccountId).FirstOrDefaultAsync(ct);
            usesCash = cash is { } id && accountIds.Contains(id);
        }
        return await ByCodeAsync(organizationId, SystemJournalTypes.ForEntryType(entryType, usesCash), ct);
    }

    /// <summary>The system type with this code, active; else the active MISC; else any type of that kind (never null once seeded).</summary>
    public async Task<JournalType> ByCodeAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(organizationId, ct);
        var types = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == organizationId).ToListAsync(ct);
        var wanted = types.FirstOrDefault(t => t.Code == code);
        if (wanted is { IsActive: true }) return wanted;
        return types.FirstOrDefault(t => t.Code == SystemJournalTypes.Misc && t.IsActive)
               ?? types.FirstOrDefault(t => t.Type == JournalTypeKinds.Miscellaneous && t.IsActive)
               ?? wanted
               ?? types.FirstOrDefault(t => t.Code == SystemJournalTypes.Misc)
               ?? types.OrderBy(t => t.Id).FirstOrDefault()
               ?? throw new InvalidOperationException("The organization has no journal types.");
    }
}
