using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Onboarding.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Onboarding.Checklist;

/// <summary>
/// A50 D-28: every 10 minutes, asks the signals about the open steps of each organization whose checklist is still shown
/// (not dismissed, a step still open). There is no ambient tenant in a job, so each organization runs in its own DI scope under
/// a <see cref="HangfireTenantScope"/> (the ProductionDeliverySweepJob pattern), because the signals read "the ambient
/// organization". One organization failing is logged and does not stop the rest.
/// </summary>
internal sealed class OnboardingChecklistJob
{
    internal const string RecurringJobId = "onboarding-checklist-sweep";
    internal const string Cron = "*/10 * * * *";

    /// <summary>Organizations per run. The checklist is short-lived, so this is generous.</summary>
    internal int BatchSize { get; set; } = 500;

    private readonly OnboardingDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly IModuleGate? _gate;
    private readonly ILogger _log;

    public OnboardingChecklistJob(OnboardingDbContext db, IServiceScopeFactory scopes, IModuleGate? gate = null,
        ILogger<OnboardingChecklistJob>? log = null)
    {
        _db = db;
        _scopes = scopes;
        _gate = gate;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Registers the recurring job. Called by UseOnboardingModule after migrating.</summary>
    public static void Schedule() =>
        RecurringJob.AddOrUpdate<OnboardingChecklistJob>(RecurringJobId, job => job.RunAsync(), Cron);

    /// <returns>How many steps were ticked in this run.</returns>
    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync()
    {
        var orgs = await _db.OnboardingChecklists.IgnoreQueryFilters().AsNoTracking()
            .Where(c => !c.Dismissed && c.Steps.Any(s => s.CompletedAt == null))
            .OrderBy(c => c.CreatedAt).Select(c => c.OrganizationId).Take(BatchSize).ToListAsync();
        if (orgs.Count == 0) return 0;

        var skipped = await _gate.SkippedAmongAsync(orgs, ModuleCodes.Onboarding, _log, nameof(OnboardingChecklistJob));
        var ticked = 0;
        foreach (var org in orgs.Where(o => !skipped.Contains(o)))
        {
            var previous = HangfireTenantScope.OrganizationId;
            HangfireTenantScope.OrganizationId = org;
            try
            {
                using var scope = _scopes.CreateScope();
                ticked += await scope.ServiceProvider.GetRequiredService<IOnboardingChecklistService>().EvaluateAsync(org);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Onboarding checklist sweep failed for organization {OrganizationId}.", org);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = previous;
            }
        }

        if (ticked > 0) _log.LogInformation("Onboarding checklist sweep: {Ticked} step(s) completed across {Orgs} organization(s).", ticked, orgs.Count);
        return ticked;
    }
}
