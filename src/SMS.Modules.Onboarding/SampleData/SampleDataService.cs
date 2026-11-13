using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.SampleData;

/// <summary>A50 D-27 / FSD §4.5: preview, load, cleanup and the "Demo" badge refs, for the ambient organization.</summary>
public interface ISampleDataService
{
    Task<SampleDataStatusModel> GetStatusAsync(CancellationToken ct = default);
    Task<SampleDataLoadResult> LoadAsync(int? userId, CancellationToken ct = default);
    Task<SampleDataCleanupResult> CleanupAsync(int? userId, CancellationToken ct = default);
    Task<bool> HasDemoDataAsync(CancellationToken ct = default);
    Task<List<string>> GetRefsAsync(string? entityType, CancellationToken ct = default);
    /// <summary>Demo record ids that parse as Guids, for the onboarding signals (BR-50-014: demo records never tick a step).</summary>
    Task<IReadOnlySet<Guid>> GetDemoEntityIdsAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// D-73: set while a load runs (an AsyncLocal, so it covers exactly the contributors' own calls). The "onboarding"
/// entity sync target reads it: a demo customer / vendor / product saved during a load is pushed before its DemoRecords
/// row exists, and must not tick a checklist step.
/// </summary>
internal static class SampleDataScope
{
    private static readonly AsyncLocal<bool> _loading = new();
    public static bool IsLoading { get => _loading.Value; set => _loading.Value = value; }
}

/// <summary>
/// Orchestrates the registered <see cref="ISampleDataContributor"/>s (D-27): each creates its own records through its
/// module's own services, so every normal rule applies. Provenance goes to onboarding.DemoRecords, saved after each
/// contributor so a later failure never orphans what was made. Cleanup runs the contributors in reverse order, and a
/// record a contributor keeps (real data uses it) keeps its DemoRecords row and is reported.
/// <para>Contributors are expected not to throw for data problems: they skip and report a <see cref="DemoEntityTypes.Note"/>
/// ref. One that throws stops the load; what earlier contributors made stays recorded, so a cleanup removes it.</para>
/// </summary>
internal sealed class SampleDataService : ISampleDataService
{
    internal const string AlreadyLoadedMessage = "Sample data is already loaded. Remove it first.";
    internal const string LegacyMessage = "Sample data is for new organizations only. This organization already has its own data.";
    internal const string NothingToLoadMessage = "None of the modules that provide sample data is enabled.";

    private readonly OnboardingDbContext _db;
    private readonly IReadOnlyList<ISampleDataContributor> _contributors;
    private readonly IModuleGate? _gate;
    private readonly ILogger _log;

    public SampleDataService(OnboardingDbContext db, IEnumerable<ISampleDataContributor> contributors, IModuleGate? gate = null,
        ILogger<SampleDataService>? log = null)
    {
        _db = db;
        _contributors = contributors.OrderBy(c => c.Order).ThenBy(c => Name(c), StringComparer.Ordinal).ToList();
        _gate = gate;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    private Guid OrgId => _db.TenantContext.OrganizationId;

    internal static string Name(ISampleDataContributor c) => c.GetType().FullName ?? c.GetType().Name;

    private IQueryable<DemoRecord> Records() => _db.DemoRecords.IgnoreQueryFilters().Where(r => r.OrganizationId == OrgId);

    private async Task<List<ISampleDataContributor>> EnabledAsync(List<string> notes, CancellationToken ct)
    {
        var enabled = new List<ISampleDataContributor>();
        foreach (var c in _contributors)
        {
            if (c.RequiresModule is { } module && !await _gate.IsOnAsync(OrgId, module, ct))
            {
                notes.Add($"{ModuleLabel(module)} is not enabled, so its sample records are left out.");
                continue;
            }
            enabled.Add(c);
        }
        return enabled;
    }

    private static string ModuleLabel(string module) => module switch
    {
        ModuleCodes.Customers => "The Customers module",
        ModuleCodes.Suppliers => "The Suppliers module",
        ModuleCodes.Inventory => "The Inventory module",
        ModuleCodes.Demand    => "The Demand (sales and purchasing) module",
        _                     => module
    };

    public async Task<bool> HasDemoDataAsync(CancellationToken ct = default) => await Records().AnyAsync(ct);

    public async Task<SampleDataStatusModel> GetStatusAsync(CancellationToken ct = default)
    {
        var model = new SampleDataStatusModel();
        var current = await Records().GroupBy(r => r.EntityType).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        foreach (var row in current)
            if (DemoEntityTypes.KeyOf(row.Key) is { } key) model.Current[key] = row.Count;
        model.HasDemo = current.Count > 0;
        model.LoadedAt = model.HasDemo ? await Records().MinAsync(r => (DateTime?)r.CreatedAt, ct) : null;

        var enabled = await EnabledAsync(model.Notes, ct);
        foreach (var c in enabled)
        {
            try
            {
                var plan = await c.PreviewAsync(ct);
                foreach (var (type, count) in plan.Counts)
                    if (DemoEntityTypes.KeyOf(type) is { } key) model.Counts[key] += count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sample data: {Contributor} could not preview.", Name(c));
                model.Notes.Add($"{Name(c)} could not say what it would create: {ex.Message}");
            }
        }
        if (model.Counts["products"] > 0 && model.Counts["openingStock"] == 0)
            model.Notes.Add("Opening stock is skipped: the opening-stock rules need the opening balance date set in Finance settings and an active warehouse.");

        var refusal = model.HasDemo ? AlreadyLoadedMessage
            : await IsLegacyAsync(ct) ? LegacyMessage
            : enabled.Count == 0 || model.Counts.Values.All(v => v == 0) ? NothingToLoadMessage
            : null;
        model.CanLoad = refusal is null;
        model.Reason = refusal;
        return model;
    }

    /// <summary>D-74: an organization from before A50 (setup note LEGACY) has real data of its own; demo records would mix with it.</summary>
    private Task<bool> IsLegacyAsync(CancellationToken ct) =>
        _db.OrganizationSetups.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == OrgId && s.Note == SetupCodes.LegacyNote, ct);

    public async Task<SampleDataLoadResult> LoadAsync(int? userId, CancellationToken ct = default)
    {
        if (OrgId == Guid.Empty) throw new BadRequestException("There is no organization to load sample data into.");
        if (await HasDemoDataAsync(ct)) throw new ConflictException(AlreadyLoadedMessage);
        if (await IsLegacyAsync(ct)) throw new ConflictException(LegacyMessage);

        var result = new SampleDataLoadResult();
        var enabled = await EnabledAsync(result.Notes, ct);
        if (enabled.Count == 0) throw new BadRequestException(NothingToLoadMessage);

        var createdSoFar = new List<DemoRecordRef>();
        var previous = SampleDataScope.IsLoading;
        SampleDataScope.IsLoading = true;
        try
        {
            foreach (var c in enabled)
            {
                IReadOnlyList<DemoRecordRef> refs;
                try
                {
                    refs = await c.CreateAsync(createdSoFar, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Sample data: {Contributor} failed; the load stopped.", Name(c));
                    throw new UnprocessableEntityException(
                        $"Loading sample data stopped at {Describe(c)}: {ex.Message} What was already created is kept as sample data; remove it before loading again.");
                }

                var stored = 0;
                foreach (var r in refs ?? [])
                {
                    if (r.EntityType == DemoEntityTypes.Note) { result.Notes.Add(r.EntityRef); continue; }
                    if (string.IsNullOrWhiteSpace(r.EntityType) || string.IsNullOrWhiteSpace(r.EntityRef)) continue;
                    _db.DemoRecords.Add(new DemoRecord
                    {
                        OrganizationId = OrgId, EntityType = Trim(r.EntityType, 50), EntityRef = Trim(r.EntityRef, 100),
                        Contributor = Trim(Name(c), 100), CreatedAt = DateTime.UtcNow
                    });
                    createdSoFar.Add(r);
                    if (DemoEntityTypes.KeyOf(r.EntityType) is { } key) result.Created[key]++;
                    stored++;
                }
                if (stored > 0) await _db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            SampleDataScope.IsLoading = previous;
        }

        result.Total = result.Created.Values.Sum();
        _log.LogInformation("Sample data loaded for {OrganizationId} by {UserId}: {Total} records.", OrgId, userId, result.Total);
        return result;
    }

    public async Task<SampleDataCleanupResult> CleanupAsync(int? userId, CancellationToken ct = default)
    {
        var result = new SampleDataCleanupResult();
        var rows = await Records().ToListAsync(ct);
        if (rows.Count == 0) return result;

        var known = _contributors.Select(c => Trim(Name(c), 100)).ToHashSet(StringComparer.Ordinal);
        var removed = new List<DemoRecord>();

        // Reverse order: orders first, then products, then partners (FSD §7.2).
        foreach (var c in _contributors.AsEnumerable().Reverse())
        {
            var mine = rows.Where(r => r.Contributor == Trim(Name(c), 100)).ToList();
            if (mine.Count == 0) continue;

            IReadOnlyList<DemoRecordRef> kept;
            try
            {
                kept = await c.DeleteAsync(mine.Select(r => new DemoRecordRef(r.EntityType, r.EntityRef)).ToList(), ct) ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing of this contributor is known to be removed: every row stays, and the next cleanup retries it.
                _log.LogError(ex, "Sample data cleanup: {Contributor} failed.", Name(c));
                result.Notes.Add($"{Describe(c)} could not be cleaned up: {ex.Message} Its records are kept; try again.");
                foreach (var r in mine) result.Kept.Add(new KeptDemoRecordModel { EntityType = r.EntityType, EntityRef = r.EntityRef, Reason = "Cleanup failed; try again." });
                continue;
            }

            var keptSet = new HashSet<(string, string)>();
            foreach (var k in kept)
            {
                if (k.EntityType == DemoEntityTypes.Note) { result.Notes.Add(k.EntityRef); continue; }
                keptSet.Add((k.EntityType, k.EntityRef));
            }
            foreach (var r in mine)
            {
                if (keptSet.Contains((r.EntityType, r.EntityRef)))
                {
                    if (r.EntityType != DemoEntityTypes.ProductVariant)
                        result.Kept.Add(new KeptDemoRecordModel { EntityType = r.EntityType, EntityRef = r.EntityRef, Reason = "Used by real data." });
                    continue;
                }
                removed.Add(r);
                if (DemoEntityTypes.KeyOf(r.EntityType) is { } key) result.Deleted[key]++;
            }
        }

        foreach (var orphan in rows.Where(r => r.Contributor is null || !known.Contains(r.Contributor)))
        {
            result.Kept.Add(new KeptDemoRecordModel { EntityType = orphan.EntityType, EntityRef = orphan.EntityRef,
                Reason = "The module that created it is not available in this host." });
        }

        _db.DemoRecords.RemoveRange(removed);
        await _db.SaveChangesAsync(ct);
        result.Total = result.Deleted.Values.Sum();
        _log.LogInformation("Sample data cleanup for {OrganizationId} by {UserId}: {Deleted} removed, {Kept} kept.",
            OrgId, userId, result.Total, result.Kept.Count);
        return result;
    }

    public async Task<List<string>> GetRefsAsync(string? entityType, CancellationToken ct = default)
    {
        var q = Records();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var type = entityType.Trim();
            q = q.Where(r => r.EntityType == type);
        }
        return await q.OrderBy(r => r.Id).Select(r => r.EntityRef).ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> GetDemoEntityIdsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var refs = await _db.DemoRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId).Select(r => r.EntityRef).ToListAsync(ct);
        var ids = new HashSet<Guid>();
        foreach (var r in refs) if (Guid.TryParse(r, out var g)) ids.Add(g);
        return ids;
    }

    private static string Describe(ISampleDataContributor c) => c.GetType().Name.Replace("SampleDataContributor", string.Empty) switch
    {
        "" => c.GetType().Name,
        var n => n
    };

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}
