using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// A44 D-9 / D-11 — every minute: posting targets that are Failed and due (<c>NextAttemptAt</c> passed), and Pending rows
/// left behind by an interrupted dispatch (older than <see cref="StuckAfter"/>), are sent again through the gateway, which
/// applies the backoff (30 s, 2 min, 8 min, 32 min, 2 h) and dead-letters after the last retry. Per organization inside a
/// <see cref="HangfireTenantScope"/> (targets read the ambient tenant); organizations with neither FEATURE_GENERAL_LEDGER
/// nor MODULE_INTEGRATION are left out of the batch. Each organization gets a fresh DI scope, so one organization's
/// failure or tracked entities never leak into the next.
/// </summary>
internal sealed class PostingRetryJob
{
    internal const string RecurringJobId = "finance-posting-retry";
    internal const string Cron = "* * * * *";
    internal const int BatchSize = 200;
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);
    /// <summary>How long a claimed row is invisible to another run (the retry sets the real next attempt).</summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    private readonly AccountingDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly IModuleGate? _gate;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public PostingRetryJob(AccountingDbContext db, IServiceScopeFactory scopes, IModuleGate? gate = null, TimeProvider? clock = null,
        ILogger<PostingRetryJob>? log = null)
    {
        _db = db;
        _scopes = scopes;
        _gate = gate;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Number of target rows retried.</summary>
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(300)]
    public async Task<int> RunAsync()
    {
        await RefreshQuickBooksAsync();

        var now = _clock.GetUtcNow().UtcDateTime;
        var stuck = now - StuckAfter;
        var due = await _db.FinancialPostingTargets.IgnoreQueryFilters().AsNoTracking()
            .Where(t => (t.Status == PostingTargetStatuses.Failed && !t.MirroredFromTarget && (t.NextAttemptAt == null || t.NextAttemptAt <= now))
                        || (t.Status == PostingTargetStatuses.Pending && t.ModifiedAt <= stuck))
            .OrderBy(t => t.NextAttemptAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.OrganizationId })
            .Take(BatchSize * 5)
            .ToListAsync();
        if (due.Count == 0) return 0;

        var skipped = new HashSet<Guid>();
        if (_gate is not null)
            foreach (var org in due.Select(d => d.OrganizationId).Distinct())
                if (!await _gate.IsEnabledAsync(org, AccountingCodes.FeatureGeneralLedger)
                    && !await _gate.IsEnabledAsync(org, ModuleCodes.Integration))
                {
                    skipped.Add(org);
                    _log.LogDebug("{Job}: organization {OrganizationId} skipped, no posting target module is enabled.", nameof(PostingRetryJob), org);
                }

        var done = 0;
        foreach (var org in due.Where(d => !skipped.Contains(d.OrganizationId)).Take(BatchSize).GroupBy(d => d.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                done += await RetryOrganizationAsync(org.Key, org.Select(d => d.Id).ToList());
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Posting retry failed for organization {OrganizationId}.", org.Key);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
        if (done > 0) _log.LogInformation("Posting retry: {Count} target(s) retried.", done);
        return done;
    }

    /// <summary>D-49b — every run: the quickbooks rows still Queued/Held are brought up to date from the QuickBooks gateway
    /// (per organization with MODULE_INTEGRATION on, its own scope and tenant), so the summary and the retries see them.</summary>
    private async Task RefreshQuickBooksAsync()
    {
        var orgs = await _db.FinancialPostingTargets.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TargetCode == PostingTargetCodes.QuickBooks
                        && (t.Status == PostingTargetStatuses.Queued || t.Status == PostingTargetStatuses.Held || t.MirroredFromTarget))
            .Select(t => t.OrganizationId).Distinct().ToListAsync();
        foreach (var org in orgs)
        {
            if (!await _gate.IsOnAsync(org, ModuleCodes.Integration)) continue;
            HangfireTenantScope.OrganizationId = org;
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<QuickBooksStatusRefresher>().RefreshAsync(org);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "QuickBooks status refresh failed for organization {OrganizationId}.", org);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
    }

    private async Task<int> RetryOrganizationAsync(Guid org, List<int> ids)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var gateway = scope.ServiceProvider.GetRequiredService<FinancialPostingGateway>();
        var count = 0;
        foreach (var id in ids)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var stuck = now - StuckAfter;
            // REV-5 — claim the row (conditional UPDATE) so a second job run or a manual retry does not take it as well.
            if (db.Database.IsRelational())
            {
                var lease = now + Lease;
                var claimed = await db.FinancialPostingTargets.IgnoreQueryFilters()
                    .Where(t => t.Id == id && t.OrganizationId == org
                                && ((t.Status == PostingTargetStatuses.Failed && !t.MirroredFromTarget && (t.NextAttemptAt == null || t.NextAttemptAt <= now))
                                    || (t.Status == PostingTargetStatuses.Pending && t.ModifiedAt <= stuck)))
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.NextAttemptAt, lease).SetProperty(t => t.ModifiedAt, now));
                if (claimed == 0) continue;
            }
            var state = await db.FinancialPostingTargets.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == id && t.OrganizationId == org).Select(t => new { t.PostingId, t.TargetCode, t.Status })
                .FirstOrDefaultAsync();
            if (state is null || !(state.Status is PostingTargetStatuses.Failed or PostingTargetStatuses.Pending)) continue;
            await gateway.RetryAsync(state.PostingId, state.TargetCode);
            count++;
        }
        return count;
    }

    public static void Register() =>
        RecurringJob.AddOrUpdate<PostingRetryJob>(RecurringJobId, job => job.RunAsync(), Cron);
}
