using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Modules.Onboarding.Models;
using SMS.Modules.Onboarding.Services.Defaults;
using SMS.Modules.Onboarding.Services.Setup;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Services;

/// <summary>A51 P3 — the Industries master as a SMS.Shared contract, so Tenancy can check a country + industry pair without a project reference.</summary>
internal sealed class IndustryCatalog : IIndustryCatalog
{
    private readonly OnboardingDbContext _db;

    public IndustryCatalog(OnboardingDbContext db) => _db = db;

    public async Task<IndustryAvailability> CheckAsync(string countryCode, string industryCode, CancellationToken ct = default)
    {
        var country = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        var code = (industryCode ?? string.Empty).Trim().ToUpperInvariant();
        var industry = await _db.Industries.AsNoTracking().Where(i => i.Code == code).Select(i => new { i.IsActive }).FirstOrDefaultAsync(ct);
        if (industry is null) return IndustryAvailability.Unknown;
        if (!industry.IsActive) return IndustryAvailability.Inactive;
        var linked = await _db.CountryIndustries.AsNoTracking().AnyAsync(c => c.CountryCode == country && c.IndustryCode == code && c.IsActive, ct);
        return linked ? IndustryAvailability.Available : IndustryAvailability.NotOfferedInCountry;
    }
}

/// <summary>
/// A51 P3 (A51-D4) — create + apply in one flow. Runs the A50 engine (<see cref="ITenantSetupService.ApplyAsync"/>, defaults only: no
/// overrides, no wizard inputs, no invites) for a freshly created organization. The engine reads "the ambient organization", so
/// the work runs in its own DI scope under a <see cref="TenantOverrideScope"/> for that organization; nothing is ever written
/// under the super admin's own organization. Never throws: a failure becomes a message on the outcome.
/// </summary>
internal sealed class OrganizationDefaultsApplier : IOrganizationDefaultsApplier
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OrganizationDefaultsApplier> _log;
    // A51 P7 — absent in a host without the background worker (unit tests): StartApplyAsync then runs the apply inline.
    private readonly OrganizationDefaultsQueue? _queue;
    private readonly OrganizationDefaultsTracker? _tracker;

    public OrganizationDefaultsApplier(IServiceScopeFactory scopes, ILogger<OrganizationDefaultsApplier>? log = null,
        OrganizationDefaultsQueue? queue = null, OrganizationDefaultsTracker? tracker = null)
    {
        _scopes = scopes;
        _log = log ?? NullLogger<OrganizationDefaultsApplier>.Instance;
        _queue = queue;
        _tracker = tracker;
    }

    public async Task<OrganizationDefaultsOutcome> StartApplyAsync(Guid organizationId, string countryCode, string industryType, int? userId, CancellationToken ct = default)
    {
        if (_queue is null || _tracker is null) return await ApplyAsync(organizationId, countryCode, industryType, userId, ct);

        var country = SetupKey.Country(countryCode);
        var industry = SetupKey.Industry(industryType);
        // The wizard opens pre-filled even if the job is lost (API restart before it runs).
        var outcome = await PrefillAsync(organizationId, country, industry, ct);
        outcome.Attempted = true;
        outcome.IndustryType = industry;

        var now = DateTime.UtcNow;
        _tracker.Queued(organizationId, now);
        if (!_queue.TryEnqueue(new OrganizationDefaultsJob(organizationId, country, industry, userId, now)))
        {
            _tracker.Failed(organizationId, "The default data could not be queued.", DateTime.UtcNow);
            outcome.Status = OrganizationDefaultsStatuses.Failed;
            outcome.Message = "The default data could not be queued; the organization admin can run the setup wizard, which is pre-filled.";
            return outcome;
        }
        outcome.Status = OrganizationDefaultsStatuses.Running;
        outcome.Message = "Applying the default data in the background; follow it with GET api/system/organizations/{id}/defaults-status.";
        return outcome;
    }

    public async Task<OrganizationDefaultsOutcome> PrefillAsync(Guid organizationId, string countryCode, string? industryType, CancellationToken ct = default)
    {
        var country = SetupKey.Country(countryCode);
        var industry = string.IsNullOrWhiteSpace(industryType) ? null : SetupKey.Industry(industryType);
        var outcome = new OrganizationDefaultsOutcome { Attempted = false, CountryCode = country, IndustryType = industry };
        try
        {
            using var scope = _scopes.CreateScope();
            using (TenantOverrideScope.Begin(organizationId))
            {
                var setup = scope.ServiceProvider.GetRequiredService<ITenantSetupService>();
                await setup.PrefillAsync(country, industry, ct);
                outcome.SetupStatus = (await setup.GetStateAsync(ct)).SetupStatus;
            }
            outcome.Message = "The setup wizard will open pre-filled with the chosen country; the organization admin picks the industry.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pre-filling the setup of organization {OrganizationId} failed.", organizationId);
            outcome.Message = $"The country could not be recorded for the setup wizard ({ex.Message}); the organization admin will choose it there.";
        }
        return outcome;
    }

    public async Task<OrganizationDefaultsOutcome> ApplyAsync(Guid organizationId, string countryCode, string industryType, int? userId, CancellationToken ct = default)
    {
        var country = SetupKey.Country(countryCode);
        var industry = SetupKey.Industry(industryType);
        var outcome = new OrganizationDefaultsOutcome { Attempted = true, CountryCode = country, IndustryType = industry };

        try
        {
            using var scope = _scopes.CreateScope();
            using (TenantOverrideScope.Begin(organizationId))
            {
                var sp = scope.ServiceProvider;
                var setup = sp.GetRequiredService<ITenantSetupService>();
                var defaults = sp.GetRequiredService<ISetupDefaultsService>();

                // The wizard opens pre-filled even if the apply below fails half way.
                await setup.PrefillAsync(country, industry, ct);
                var resolved = await defaults.ResolveAllAsync(country, industry, ct);

                var response = await setup.ApplyAsync(new SetupApplyRequest { CountryCode = country, IndustryType = industry }, userId, ct);

                foreach (var r in response.Results)
                {
                    var hit = resolved.GetValueOrDefault(r.Category);
                    var item = new OrganizationDefaultsCategoryResult
                    {
                        Category = r.Category, Status = r.Status, Ok = r.Ok, Created = r.Created, Skipped = r.Skipped,
                        Message = r.Message ?? (r.Warnings.Count > 0 ? string.Join(" ", r.Warnings) : null),
                        PackLevel = LevelOf(hit, country, industry),
                        ResolvedCountry = hit?.Country, ResolvedIndustry = hit?.Industry
                    };
                    outcome.Categories.Add(item);
                }

                outcome.SetupStatus = response.SetupStatus;
                outcome.Completed = response.Completed && response.SetupStatus == SetupStatuses.Completed;
                outcome.PackLevel = Best(outcome.Categories);
                outcome.FallbackUsed = outcome.Categories.Any(c => c.PackLevel is not (OrganizationPackLevels.Exact or OrganizationPackLevels.None));
                outcome.Status = outcome.Completed ? OrganizationDefaultsStatuses.Completed : OrganizationDefaultsStatuses.InProgress;
                outcome.Message = outcome.Completed
                    ? "Default data applied; the setup is complete."
                    : $"Default data was applied with problems ({string.Join(", ", outcome.Categories.Where(c => !c.Ok).Select(c => c.Category))}); the organization admin can finish in the setup wizard.";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applying the default data to new organization {OrganizationId} failed.", organizationId);
            outcome.Completed = false;
            outcome.Status = OrganizationDefaultsStatuses.Failed;
            outcome.Message = $"The default data could not be applied ({ex.Message}); the organization admin can run the setup wizard, which is pre-filled.";
            try
            {
                using var scope = _scopes.CreateScope();
                using (TenantOverrideScope.Begin(organizationId))
                {
                    var setup = scope.ServiceProvider.GetRequiredService<ITenantSetupService>();
                    await setup.PrefillAsync(country, industry, ct);
                    outcome.SetupStatus = (await setup.GetStateAsync(ct)).SetupStatus;
                }
            }
            catch (Exception inner)
            {
                _log.LogWarning(inner, "Recording the setup choice of organization {OrganizationId} failed too.", organizationId);
            }
        }
        return outcome;
    }

    /// <summary>Which level of the A50 fallback chain a resolved default came from, relative to the requested country + industry.</summary>
    internal static string LevelOf(ResolvedDefault? hit, string country, string industry)
    {
        if (hit is null) return OrganizationPackLevels.None;
        var c = hit.Country.Equals(country, StringComparison.OrdinalIgnoreCase);
        var i = hit.Industry.Equals(industry, StringComparison.OrdinalIgnoreCase);
        return c && i ? OrganizationPackLevels.Exact
            : c ? OrganizationPackLevels.Country
            : i ? OrganizationPackLevels.Industry
            : OrganizationPackLevels.Global;
    }

    private static string Best(IEnumerable<OrganizationDefaultsCategoryResult> categories)
    {
        string[] order = [OrganizationPackLevels.Exact, OrganizationPackLevels.Country, OrganizationPackLevels.Industry, OrganizationPackLevels.Global];
        var levels = categories.Select(c => c.PackLevel).ToHashSet();
        return order.FirstOrDefault(levels.Contains) ?? OrganizationPackLevels.None;
    }
}
