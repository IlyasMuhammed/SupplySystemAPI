using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Material.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A34 D-20 recovery — retries the delivery hand-off of make-to-order production orders whose post-FGR call never
/// settled: only orders still carrying <c>DeliveryCreationPendingSince</c>, at least <see cref="SweepDelay"/> old (so it
/// never races the FGR's own call), oldest first. The hand-off is idempotent (the D-20 formula), so a retry, a double
/// run or a race with "Create delivery now" creates nothing twice.
/// <para>No user and no ambient tenant: the hand-off works in each order's own organization, and each organization's
/// orders run under a <see cref="HangfireTenantScope"/> (the SaleOrderDeliverySweepJob pattern). An order that still
/// fails goes to the back of the queue, so it cannot hold the head of every run.</para>
/// </summary>
internal sealed class ProductionDeliverySweepJob
{
    internal const string RecurringJobId = "production-delivery-creation-sweep";
    internal const string Cron = "*/15 * * * *";
    internal static readonly TimeSpan SweepDelay = TimeSpan.FromMinutes(10);

    /// <summary>Raised against the system, not a person.</summary>
    internal const int SystemUserId = 0;

    /// <summary>Orders per run; the rest wait for the next one (oldest first). Settable for tests.</summary>
    internal int BatchSize { get; set; } = 200;

    private readonly MaterialDbContext          _db;
    private readonly IProductionDeliveryHandoff _handoff;
    private readonly ILogger                    _log;
    private readonly IModuleGate?               _gate;

    public ProductionDeliverySweepJob(
        MaterialDbContext db, IProductionDeliveryHandoff handoff, ILogger<ProductionDeliverySweepJob>? log = null,
        IModuleGate? gate = null)
    {
        _gate    = gate;
        _db      = db;
        _handoff = handoff;
        _log     = log ?? (ILogger)NullLogger.Instance;
    }

    /// <returns>How many orders are no longer pending after this run.</returns>
    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync()
    {
        var cutoff = DateTime.UtcNow - SweepDelay;
        var pending = _db.ProductionOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.DeliveryCreationPendingSince != null && p.DeliveryCreationPendingSince <= cutoff);

        // A37 D-9 — organizations without MODULE_MANUFACTURING (grace counts as off) are left out, still pending; they
        // resume when it is back. Excluded in the query so they cannot fill the batch.
        var skipped = _gate is null ? [] : (await _gate.SkippedAmongAsync(
            await pending.Select(p => p.OrganizationId).Distinct().ToListAsync(),
            ModuleCodes.Manufacturing, _log, nameof(ProductionDeliverySweepJob))).ToList();

        var due = await pending.Where(p => !skipped.Contains(p.OrganizationId))
            .OrderBy(p => p.DeliveryCreationPendingSince)
            .Select(p => new { p.OrganizationId, p.UUID })
            .Take(BatchSize)
            .ToListAsync();

        var done = 0;
        foreach (var org in due.GroupBy(p => p.OrganizationId))
        {
            HangfireTenantScope.OrganizationId = org.Key;
            try
            {
                foreach (var item in org)
                {
                    var result = await _handoff.RunAsync(item.UUID, SystemUserId);
                    if (!result.Failed)
                    {
                        done++;
                        continue;
                    }

                    // Still failing: to the back of the queue.
                    try
                    {
                        var po = await _db.ProductionOrders.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(p => p.UUID == item.UUID && p.OrganizationId == org.Key);
                        if (po?.DeliveryCreationPendingSince is not null)
                        {
                            po.DeliveryCreationPendingSince = DateTime.UtcNow;
                            await _db.SaveChangesAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Production delivery sweep: could not requeue {ProductionOrderUuid}.", item.UUID);
                        _db.ChangeTracker.Clear();
                    }
                }
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        if (due.Count > 0)
            _log.LogInformation("Production delivery sweep: {Done} of {Due} pending production order(s) settled.", done, due.Count);
        return done;
    }
}
