using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Data;

namespace SMS.Modules.Integration.Jobs;

/// <summary>Daily: deletes sync-log rows older than <c>SyncLogRetentionDays</c> (90 by default), every organization.</summary>
internal sealed class SyncLogRetentionJob
{
    public const string RecurringJobId = "integration-synclog-retention";

    private const int BatchSize = 5000;

    private readonly IntegrationDbContext          _db;
    private readonly IntegrationJobOptions         _options;
    private readonly TimeProvider                  _clock;
    private readonly ILogger<SyncLogRetentionJob>  _logger;

    public SyncLogRetentionJob(
        IntegrationDbContext db, IOptions<IntegrationJobOptions> options, TimeProvider clock, ILogger<SyncLogRetentionJob> logger)
    {
        _db      = db;
        _options = options.Value;
        _clock   = clock;
        _logger  = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync()
    {
        var deleted = await PurgeAsync();
        if (deleted > 0) _logger.LogInformation("QuickBooks sync log retention: {Deleted} row(s) deleted.", deleted);
    }

    internal async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, _options.SyncLogRetentionDays));
        var total  = 0;

        if (_db.Database.IsRelational())
        {
            // In batches, so one purge never holds a long lock on the log.
            while (true)
            {
                var ids = await _db.SyncLog.IgnoreQueryFilters()
                    .Where(l => l.CreatedAt < cutoff)
                    .OrderBy(l => l.Id)
                    .Select(l => l.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct);
                if (ids.Count == 0) break;

                total += await _db.SyncLog.IgnoreQueryFilters().Where(l => ids.Contains(l.Id)).ExecuteDeleteAsync(ct);
                if (ids.Count < BatchSize) break;
            }

            return total;
        }

        var old = await _db.SyncLog.IgnoreQueryFilters().Where(l => l.CreatedAt < cutoff).ToListAsync(ct);
        _db.SyncLog.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
        return old.Count;
    }
}
