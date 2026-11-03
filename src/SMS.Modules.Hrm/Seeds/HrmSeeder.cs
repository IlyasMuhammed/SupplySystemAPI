using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Services;

/// <summary>
/// Per-organization defaults (D-9): settings row, leave types, Pakistan holidays (moon-dependent dates flagged IsEstimated), salary
/// components, the Standard Monthly structure, Pakistan tax + statutory data (ALWAYS unverified, G-21), expense categories, letter
/// templates, and the HRM workflow definitions. Runs on first HRM use in an organization, insert-only by natural key, so an edited or
/// deleted-by-hand row is not fought over beyond the first run of a seed version. Nothing here enables a module or marks anything verified.
/// </summary>
public interface IHrmSeeder
{
    Task EnsureAsync(Guid organizationId, CancellationToken ct = default);
}

internal sealed class HrmSeeder : IHrmSeeder
{
    /// <summary>Bump when a seed row is added, so organizations already seeded pick it up. 2: GRATUITY statutory rate.</summary>
    public const int CurrentVersion = 2;

    private readonly HrmDbContext _db;
    private readonly IWorkflowSeedingService? _workflows;
    private readonly ILogger<HrmSeeder>? _log;
    private readonly HashSet<Guid> _done = [];

    public HrmSeeder(HrmDbContext db, IWorkflowSeedingService? workflows = null, ILogger<HrmSeeder>? log = null)
    {
        _db = db;
        _workflows = workflows;
        _log = log;
    }

    public async Task EnsureAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || !_done.Add(organizationId)) return;

        var settings = await _db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId).Select(s => new { s.SeedVersion }).FirstOrDefaultAsync(ct);
        if (settings?.SeedVersion >= CurrentVersion) return;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.InTransactionAsync(() => SeedAsync(organizationId, ct), ct);
                break;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Another request seeded the same rows first (unique indexes): forget ours and re-check theirs.
                _db.ChangeTracker.Clear();
            }
        }

        if (_workflows is not null)
        {
            try { await _workflows.SeedHrmWorkflowsAsync(organizationId); }
            catch (Exception ex) { _log?.LogError(ex, "HRM workflow definitions could not be seeded for {Org}; submitting will say so.", organizationId); }
        }
    }

    private async Task SeedAsync(Guid org, CancellationToken ct)
    {
        var settings = await _db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OrganizationId == org, ct);
        if (settings is null)
        {
            settings = new HrmSettings { OrganizationId = org };
            _db.Settings.Add(settings);
        }

        await AddMissingAsync(_db.LeaveTypes, org, x => x.Code, HrmSeedData.LeaveTypes(org), ct);
        await AddMissingAsync(_db.Holidays, org, x => $"{x.Date:yyyyMMdd}|{x.Name}", HrmSeedData.PakistanHolidays(org), ct);
        await AddMissingAsync(_db.SalaryComponents, org, x => x.Code, HrmSeedData.Components(org), ct);
        await AddMissingAsync(_db.TaxSlabTables, org, x => x.Code, HrmSeedData.TaxTables(org), ct);
        await AddMissingAsync(_db.StatutoryRates, org, x => $"{x.RateType}|{x.EffectiveFrom:yyyyMMdd}", HrmSeedData.StatutoryRates(org), ct);
        await AddMissingAsync(_db.ExpenseCategories, org, x => x.Code, HrmSeedData.ExpenseCategories(org), ct);
        await AddMissingAsync(_db.LetterTemplates, org, x => x.Code, HrmSeedData.LetterTemplates(org), ct);
        await AddMissingAsync(_db.Shifts, org, x => x.Code, HrmSeedData.Shifts(org), ct);
        await _db.SaveChangesAsync(ct);

        // The structure references the components saved above.
        if (!await _db.SalaryStructures.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.Code == HrmSeedData.StandardStructureCode, ct))
        {
            var ids = await _db.SalaryComponents.IgnoreQueryFilters().Where(c => c.OrganizationId == org)
                .ToDictionaryAsync(c => c.Code, c => c.Id, ct);
            var structure = new SalaryStructure
            {
                OrganizationId = org, Code = HrmSeedData.StandardStructureCode, Name = "Standard Monthly", IsDefault = true,
                Description = "BASIC + HRENT + UTIL + CONV - ITAX - EOBI - PF = NET. Amounts and rates are yours to confirm."
            };
            var sequence = 10;
            foreach (var code in HrmSeedData.StandardStructureComponents)
            {
                if (!ids.TryGetValue(code, out var componentId)) continue;
                structure.Lines.Add(new SalaryStructureLine { OrganizationId = org, ComponentId = componentId, Sequence = sequence });
                sequence += 10;
            }
            _db.SalaryStructures.Add(structure);
        }

        settings.SeedVersion = CurrentVersion;
        await _db.SaveChangesAsync(ct);
    }

    private async Task AddMissingAsync<T>(DbSet<T> set, Guid org, Func<T, string> key, IReadOnlyList<T> wanted, CancellationToken ct) where T : HrmEntity
    {
        var existing = (await set.IgnoreQueryFilters().AsNoTracking().Where(x => x.OrganizationId == org).ToListAsync(ct)).Select(key).ToHashSet();
        foreach (var row in wanted)
            if (existing.Add(key(row))) set.Add(row);
    }
}
