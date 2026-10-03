using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A33 PA-04/PA-05 — fulfillment routes (BR-C1-01..09, docs/fulfillment-routes/API-CONTRACT.md §3).
/// <para>
/// Every query filters explicitly on the caller's own organization (<see cref="Own"/>): the EF tenant filter is off
/// for super admins, and another organization's route must read as absent to them too (R-11).
/// </para>
/// <para>
/// Writes that touch a default run under a per-organization application lock (the <c>TaxCodeService</c> pattern),
/// and the unique filtered index (OrganizationId, RequiresShipping) WHERE IsDefault = 1 is the backstop (R-8).
/// </para>
/// </summary>
internal sealed partial class FulfillmentRouteService : IFulfillmentRouteService
{
    internal const int CodeMaxLength            = 30;
    internal const int NameMaxLength            = 100;
    internal const int DescriptionMaxLength     = 500;
    internal const int StepDescriptionMaxLength = 200;
    internal const int LockTimeoutMilliseconds  = 15_000;

    [GeneratedRegex("^[A-Z0-9_]+$")]
    private static partial Regex CodePattern();

    private readonly LogisticsDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IReadOnlyList<IFulfillmentRouteUsage> _usages;

    public FulfillmentRouteService(LogisticsDbContext db, ITenantContext tenant, IEnumerable<IFulfillmentRouteUsage> usages)
    {
        _db     = db;
        _tenant = tenant;
        _usages = usages.ToList();
    }

    private Guid Org => _tenant.OrganizationId;

    private IQueryable<FulfillmentRoute> Own() =>
        _db.FulfillmentRoutes.IgnoreQueryFilters().Where(r => r.OrganizationId == Org).Include(r => r.Steps);

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FulfillmentRouteModel>> GetListAsync(bool includeInactive)
    {
        var query = Own().AsNoTracking();
        if (!includeInactive) query = query.Where(r => r.IsActive);

        var rows = await query.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Code).ToListAsync();
        return rows.Select(ToModel).ToList();
    }

    public async Task<FulfillmentRouteModel?> GetByUuidAsync(Guid uuid)
    {
        var row = await Own().AsNoTracking().FirstOrDefaultAsync(r => r.UUID == uuid);
        return row is null ? null : ToModel(row);
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public async Task<FulfillmentRouteModel> CreateAsync(CreateFulfillmentRouteRequest req, int userId)
    {
        var code        = NormalizeCode(req.Code);
        var name        = NormalizeName(req.Name);
        var description = NormalizeDescription(req.Description);
        var steps       = ValidateSteps(req.Steps);

        // BR-C1-01 — a clear message here; the unique index (OrganizationId, Code) holds under a race.
        if (await Own().AnyAsync(r => r.Code == code))
            throw DuplicateCode(code);

        var displayOrder = req.DisplayOrder
            ?? ((await Own().Select(r => (int?)r.DisplayOrder).MaxAsync()) ?? 0) + 10;

        var row = new FulfillmentRoute
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = Org,
            Code           = code,
            Name           = name,
            Description    = description,
            IsActive       = true,
            IsSystem       = false,
            IsDefault      = false,
            DisplayOrder   = displayOrder,
            CreatedBy      = userId,
            CreatedDate    = DateTime.UtcNow
        };
        ReplaceSteps(row, steps);
        _db.FulfillmentRoutes.Add(row);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            throw DuplicateCode(code);   // lost the race to another request creating the same code
        }

        return ToModel(row);
    }

    public async Task<FulfillmentRouteModel?> UpdateAsync(Guid uuid, UpdateFulfillmentRouteRequest req, int userId)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
        if (row is null) return null;

        var name        = NormalizeName(req.Name);
        var description = NormalizeDescription(req.Description);

        if (req.Steps is not null)
        {
            var steps   = ValidateSteps(req.Steps);
            var current = row.Steps.OrderBy(s => s.StepOrder).ToList();
            var changed = steps.Count != current.Count
                       || steps.Where((s, i) => s.StepCode    != current[i].StepCode
                                             || s.IsMandatory != current[i].IsMandatory
                                             || s.Description != current[i].Description).Any();

            if (changed)
            {
                // D-10 — a system route's steps are locked; its name, description and order are not.
                if (row.IsSystem)
                    throw new BadRequestException($"'{row.Code}' is a system route: its steps can't be changed. Create a custom route instead.");

                var requiresShipping = steps.Any(s => s.StepCode == FulfillmentStepCode.Ship);
                if (row.IsDefault && requiresShipping != row.RequiresShipping)
                    throw new ConflictException(
                        $"'{row.Code}' is the default route for {ClassName(row.RequiresShipping)} orders, and " +
                        $"{(requiresShipping ? "adding" : "removing")} the SHIP step would change that. " +
                        "Make another route the default first, or clear the default.");

                _db.FulfillmentRouteSteps.RemoveRange(row.Steps);
                row.Steps.Clear();
                ReplaceSteps(row, steps);
            }
        }

        row.Name         = name;
        row.Description  = description;
        row.DisplayOrder = req.DisplayOrder;
        row.ModifiedBy   = userId;
        row.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return ToModel(row);
    }

    public async Task<FulfillmentRouteModel?> SetActiveAsync(Guid uuid, bool isActive, int userId)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
        if (row is null) return null;
        if (row.IsActive == isActive) return ToModel(row);

        if (!isActive)
        {
            if (row.IsDefault)
                throw new ConflictException(
                    $"'{row.Code}' is the default route for {ClassName(row.RequiresShipping)} orders and can't be deactivated. " +
                    "Make another route the default first, or clear the default.");

            // BR-C1-07 — not while active variants or open sale order lines still point at it.
            var usage = await UsageOfAsync(row.UUID);
            if (usage.Count > 0)
                throw new ConflictException($"'{row.Code}' is still used by {Describe(usage)}. Reassign them first.");
        }

        row.IsActive     = isActive;
        row.ModifiedBy   = userId;
        row.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return ToModel(row);
    }

    public Task<FulfillmentRouteModel?> SetDefaultAsync(Guid uuid, int userId) =>
        OneDefaultChangeAtATimeAsync(async () =>
        {
            var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
            if (row is null) return null;
            if (row.IsDefault) return ToModel(row);

            if (!row.IsActive)
                throw new BadRequestException($"'{row.Code}' is inactive. Activate it before making it a default.");

            // T-C1-07 — the previous default of the same class (SHIP / SELF_PICKUP orders, L-1) gives way. Saved first,
            // so the unique filtered index never sees two defaults at once.
            var previous = await _db.FulfillmentRoutes.IgnoreQueryFilters()
                .Where(r => r.OrganizationId == Org && r.IsDefault && r.RequiresShipping == row.RequiresShipping && r.Id != row.Id)
                .ToListAsync();
            foreach (var p in previous)
            {
                p.IsDefault    = false;
                p.ModifiedBy   = userId;
                p.ModifiedDate = DateTime.UtcNow;
            }
            if (previous.Count > 0) await _db.SaveChangesAsync();

            row.IsDefault    = true;
            row.ModifiedBy   = userId;
            row.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return ToModel(row);
        });

    public Task<FulfillmentRouteModel?> ClearDefaultAsync(Guid uuid, int userId) =>
        OneDefaultChangeAtATimeAsync(async () =>
        {
            var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
            if (row is null) return null;
            if (!row.IsDefault) return ToModel(row);

            row.IsDefault    = false;
            row.ModifiedBy   = userId;
            row.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return ToModel(row);
        });

    public async Task<bool> DeleteAsync(Guid uuid, int userId)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
        if (row is null) return false;

        // BR-C1-06.
        if (row.IsSystem)
            throw new ConflictException($"System routes can't be deleted. Deactivate '{row.Code}' instead.");

        if (row.IsDefault)
            throw new ConflictException(
                $"'{row.Code}' is the default route for {ClassName(row.RequiresShipping)} orders and can't be deleted. " +
                "Make another route the default first, or clear the default.");

        var usage = await UsageOfAsync(row.UUID);
        if (usage.Count > 0)
            throw new ConflictException($"'{row.Code}' is still used by {Describe(usage)}. Deactivate it instead.");

        // L-6 — history keeps its route. Checked across the whole table on purpose: a delivery can only ever carry its
        // own organization's route.
        if (await _db.DeliveryOrders.IgnoreQueryFilters().AnyAsync(d => d.FulfillmentRouteUuid == row.UUID))
            throw new ConflictException($"Deliveries have already been made on '{row.Code}'. Deactivate it instead.");

        _db.FulfillmentRoutes.Remove(row);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    internal sealed record ValidStep(string StepCode, bool IsMandatory, string? Description);

    /// <summary>BR-C1-03/04/05 and L-2. Returns the steps in step order, PICK and GOODS_ISSUE forced mandatory.</summary>
    internal static IReadOnlyList<ValidStep> ValidateSteps(IReadOnlyCollection<FulfillmentRouteStepRequest>? steps)
    {
        if (steps is null || steps.Count == 0)
            throw new BadRequestException("A route needs at least the PICK and GOODS_ISSUE steps.");

        var sorted = steps.OrderBy(s => s.StepOrder).ToList();
        var codes  = sorted.Select(s => (s.StepCode ?? string.Empty).Trim().ToUpperInvariant()).ToList();

        var unknown = codes.FirstOrDefault(c => !FulfillmentStepCode.IsKnown(c));
        if (unknown is not null)
            throw new BadRequestException(
                $"Unknown step '{unknown}'. Steps are {string.Join(", ", FulfillmentStepCode.All)}.");

        var repeated = codes.GroupBy(c => c).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
            throw new BadRequestException($"Step '{repeated.Key}' appears more than once.");

        for (var i = 0; i < sorted.Count; i++)
            if (sorted[i].StepOrder != i + 1)
                throw new BadRequestException("Step order must run 1, 2, 3 … with no gaps.");

        foreach (var required in FulfillmentStepCode.Required)
            if (!codes.Contains(required))
                throw new BadRequestException($"A route must include the {required} step.");

        if (codes[0] != FulfillmentStepCode.Pick)
            throw new BadRequestException("PICK must be the first step.");

        if (codes.Contains(FulfillmentStepCode.Ship)
            && codes.IndexOf(FulfillmentStepCode.Ship) < codes.IndexOf(FulfillmentStepCode.GoodsIssue))
            throw new BadRequestException("GOODS_ISSUE must come before SHIP.");

        // L-2 — the delivery can only move PICKED → PACKED → STAGED → GOODS_ISSUED → IN_TRANSIT.
        var ranks = codes.Select(c => FulfillmentStepCode.All.ToList().IndexOf(c)).ToList();
        for (var i = 1; i < ranks.Count; i++)
            if (ranks[i] < ranks[i - 1])
                throw new BadRequestException(
                    $"Steps must follow the order {string.Join(", ", FulfillmentStepCode.All)}: {codes[i]} can't come after {codes[i - 1]}.");

        return sorted.Select((s, i) =>
        {
            var description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description.Trim();
            if (description?.Length > StepDescriptionMaxLength)
                throw new BadRequestException($"A step's description can be at most {StepDescriptionMaxLength} characters.");
            return new ValidStep(codes[i], s.IsMandatory || FulfillmentStepCode.Required.Contains(codes[i]), description);
        }).ToList();
    }

    /// <summary>Writes the steps 1..n and derives the flags from them (BR-C1-09).</summary>
    private static void ReplaceSteps(FulfillmentRoute row, IReadOnlyList<ValidStep> steps)
    {
        for (var i = 0; i < steps.Count; i++)
            row.Steps.Add(new FulfillmentRouteStep
            {
                OrganizationId = row.OrganizationId,
                StepCode       = steps[i].StepCode,
                StepOrder      = i + 1,
                IsMandatory    = steps[i].IsMandatory,
                Description    = steps[i].Description
            });

        row.RequiresPacking  = steps.Any(s => s.StepCode == FulfillmentStepCode.Pack);
        row.RequiresShipping = steps.Any(s => s.StepCode == FulfillmentStepCode.Ship);
    }

    private async Task<IReadOnlyList<FulfillmentRouteUsageCount>> UsageOfAsync(Guid routeUuid)
    {
        var counts = new List<FulfillmentRouteUsageCount>();
        foreach (var usage in _usages)
        {
            var count = await usage.CountUsageAsync(Org, routeUuid);
            if (count.Count > 0) counts.Add(count);
        }
        return counts;
    }

    private static string Describe(IReadOnlyList<FulfillmentRouteUsageCount> usage) =>
        string.Join(" and ", usage.Select(u => $"{u.Count} {u.Description}"));

    private static string ClassName(bool requiresShipping) => requiresShipping ? "SHIP" : "SELF_PICKUP";

    private static ConflictException DuplicateCode(string code) =>
        new($"Fulfillment route code '{code}' already exists in this organization. Codes must be unique.");

    private static string NormalizeCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            throw new BadRequestException("A route needs a code.");
        if (normalized.Length > CodeMaxLength)
            throw new BadRequestException($"A route code can be at most {CodeMaxLength} characters; '{normalized}' has {normalized.Length}.");
        if (!CodePattern().IsMatch(normalized))
            throw new BadRequestException($"A route code can use only letters, digits and underscores; '{normalized}' does not.");
        return normalized;
    }

    private static string NormalizeName(string? name)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0)
            throw new BadRequestException("A route needs a name.");
        if (normalized.Length > NameMaxLength)
            throw new BadRequestException($"A route's name can be at most {NameMaxLength} characters.");
        return normalized;
    }

    private static string? NormalizeDescription(string? description)
    {
        var normalized = description?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > DescriptionMaxLength)
            throw new BadRequestException($"A route's description can be at most {DescriptionMaxLength} characters.");
        return normalized;
    }

    // ── Serializing default changes per organization ─────────────────────────

    internal static string LockResource(Guid org) => $"logistics.fulfillment_routes/{org:N}/default";

    private async Task<T> OneDefaultChangeAtATimeAsync<T>(Func<Task<T>> work)
    {
        if (!_db.Database.IsRelational())
            return await work();

        if (_db.Database.CurrentTransaction is not null)
        {
            await LockAsync();
            return await work();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt  = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();

            await using var transaction = await _db.Database.BeginTransactionAsync();
            await LockAsync();
            var result = await work();
            await transaction.CommitAsync();
            return result;
        });
    }

    private async Task LockAsync()
    {
        var outcome = new SqlParameter("@outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await _db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; "
          + "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; "
          + "SET @outcome = @result;",
            [
                new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource(Org) },
                new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMilliseconds },
                outcome
            ]);

        if (outcome.Value is not int granted || granted < 0)
            throw new ConflictException(
                "Someone else is changing this organization's default routes right now, so nothing was saved. Try again in a moment.");
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    internal static FulfillmentRouteModel ToModel(FulfillmentRoute r)
    {
        var steps = r.Steps.OrderBy(s => s.StepOrder).ToList();
        var codes = steps.Select(s => s.StepCode).ToList();
        return new FulfillmentRouteModel
        {
            Uuid             = r.UUID,
            Code             = r.Code,
            Name             = r.Name,
            Description      = r.Description,
            IsDefault        = r.IsDefault,
            IsActive         = r.IsActive,
            IsSystem         = r.IsSystem,
            RequiresPacking  = r.RequiresPacking,
            RequiresShipping = r.RequiresShipping,
            DisplayOrder     = r.DisplayOrder,
            Steps = steps.Select(s => new FulfillmentRouteStepModel
            {
                StepCode    = s.StepCode,
                Label       = FulfillmentRouteText.StepLabel(s.StepCode),
                StepOrder   = s.StepOrder,
                IsMandatory = s.IsMandatory,
                Description = s.Description
            }).ToList(),
            StepsText    = string.Join(" → ", codes.Select(FulfillmentRouteText.StepLabel)),
            StatusPath   = FulfillmentRouteStatusMap.StatusPath(codes).ToList(),
            CreatedDate  = r.CreatedDate,
            ModifiedDate = r.ModifiedDate
        };
    }

    internal static FulfillmentRouteSummary ToSummary(FulfillmentRoute r) => new(
        r.UUID, r.Code, r.Name, r.IsActive, r.IsDefault, r.IsSystem, r.RequiresPacking, r.RequiresShipping,
        r.OrderedStepCodes());
}

/// <summary>
/// A33 PA-03 — the three seed routes (D-6, L-1). Matched by code: a seed that already exists — renamed, edited,
/// deactivated, its default cleared — is never touched. A seed becomes a default only if its class has none yet.
/// Ignores the tenant filter and stamps the organization it is given (R-13): a super admin provisions organizations
/// they are not in, and the startup backfill runs with no user at all.
/// </summary>
internal sealed class FulfillmentRouteSeeder : IFulfillmentRouteSeeder
{
    internal sealed record SeedRoute(string Code, string Name, string Description, int DisplayOrder, bool Default, string[] Steps);

    internal static readonly IReadOnlyList<SeedRoute> SeedRoutes =
    [
        new("PICK_ONLY", "Pick Only", "Customer collects from the warehouse; no packing or shipping.", 10, true,
            [FulfillmentStepCode.Pick, FulfillmentStepCode.GoodsIssue]),
        new("PICK_AND_SHIP", "Pick & Ship", "Standard items that ship without packing.", 20, true,
            [FulfillmentStepCode.Pick, FulfillmentStepCode.GoodsIssue, FulfillmentStepCode.Ship]),
        new("PICK_PACK_SHIP", "Pick, Pack & Ship", "Items packed into boxes or pallets before shipment.", 30, false,
            [FulfillmentStepCode.Pick, FulfillmentStepCode.Pack, FulfillmentStepCode.GoodsIssue, FulfillmentStepCode.Ship]),
    ];

    private readonly LogisticsDbContext _db;
    public FulfillmentRouteSeeder(LogisticsDbContext db) => _db = db;

    public Task<int> EnsureSeededAsync(Guid organizationId, CancellationToken ct = default) =>
        EnsureSeededForAllAsync([organizationId], ct);

    public async Task<int> EnsureSeededForAllAsync(IReadOnlyList<Guid> organizationIds, CancellationToken ct = default)
    {
        var orgs = organizationIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (orgs.Count == 0) return 0;

        var existing = await _db.FulfillmentRoutes.IgnoreQueryFilters().AsNoTracking()
            .Where(r => orgs.Contains(r.OrganizationId))
            .Select(r => new { r.OrganizationId, r.Code, r.IsDefault, r.RequiresShipping })
            .ToListAsync(ct);

        var codes        = existing.Select(r => (r.OrganizationId, r.Code)).ToHashSet();
        var defaultTaken = existing.Where(r => r.IsDefault).Select(r => (r.OrganizationId, r.RequiresShipping)).ToHashSet();

        var added = 0;
        foreach (var org in orgs)
        {
            foreach (var seed in SeedRoutes)
            {
                if (codes.Contains((org, seed.Code))) continue;

                var requiresShipping = seed.Steps.Contains(FulfillmentStepCode.Ship);
                var isDefault        = seed.Default && defaultTaken.Add((org, requiresShipping));

                var route = new FulfillmentRoute
                {
                    UUID             = Guid.NewGuid(),
                    OrganizationId   = org,
                    Code             = seed.Code,
                    Name             = seed.Name,
                    Description      = seed.Description,
                    IsActive         = true,
                    IsSystem         = true,
                    IsDefault        = isDefault,
                    RequiresPacking  = seed.Steps.Contains(FulfillmentStepCode.Pack),
                    RequiresShipping = requiresShipping,
                    DisplayOrder     = seed.DisplayOrder,
                    CreatedDate      = DateTime.UtcNow
                };
                for (var i = 0; i < seed.Steps.Length; i++)
                    route.Steps.Add(new FulfillmentRouteStep
                    {
                        OrganizationId = org,
                        StepCode       = seed.Steps[i],
                        StepOrder      = i + 1,
                        IsMandatory    = true
                    });

                _db.FulfillmentRoutes.Add(route);
                added++;
            }
        }

        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }
}

/// <summary>A33 PA-03 / BR-C1-08 — seeds the routes for an organization the moment Tenancy creates it.</summary>
internal sealed class FulfillmentRouteProvisioningHandler : IOrganizationProvisionedHandler
{
    private readonly IFulfillmentRouteSeeder _seeder;
    public FulfillmentRouteProvisioningHandler(IFulfillmentRouteSeeder seeder) => _seeder = seeder;

    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) =>
        _seeder.EnsureSeededAsync(organizationId, ct);
}

/// <summary>
/// A33 — <see cref="IFulfillmentRouteLookup"/> for Inventory and Demand. Reads the organization it is given and no
/// other, whatever the caller's tenant (R-12, R-13).
/// </summary>
internal sealed class FulfillmentRouteLookup : IFulfillmentRouteLookup
{
    private readonly LogisticsDbContext _db;
    public FulfillmentRouteLookup(LogisticsDbContext db) => _db = db;

    private IQueryable<FulfillmentRoute> Of(Guid organizationId) =>
        _db.FulfillmentRoutes.IgnoreQueryFilters().AsNoTracking()
           .Where(r => r.OrganizationId == organizationId)
           .Include(r => r.Steps);

    public async Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default)
    {
        var wanted = routeUuids.Where(u => u != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, FulfillmentRouteSummary>();

        var rows = await Of(organizationId).Where(r => wanted.Contains(r.UUID)).ToListAsync(ct);
        return rows.ToDictionary(r => r.UUID, FulfillmentRouteService.ToSummary);
    }

    public async Task<FulfillmentRouteDefaults> GetOrgDefaultsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var rows = await Of(organizationId).Where(r => r.IsDefault && r.IsActive).ToListAsync(ct);
        return new FulfillmentRouteDefaults(
            rows.Where(r => r.RequiresShipping).Select(FulfillmentRouteService.ToSummary).FirstOrDefault(),
            rows.Where(r => !r.RequiresShipping).Select(FulfillmentRouteService.ToSummary).FirstOrDefault());
    }

    public async Task<IReadOnlyList<FulfillmentRouteSummary>> ListActiveAsync(Guid organizationId, CancellationToken ct = default)
    {
        var rows = await Of(organizationId).Where(r => r.IsActive)
            .OrderBy(r => r.DisplayOrder).ThenBy(r => r.Code).ToListAsync(ct);
        return rows.Select(FulfillmentRouteService.ToSummary).ToList();
    }
}
