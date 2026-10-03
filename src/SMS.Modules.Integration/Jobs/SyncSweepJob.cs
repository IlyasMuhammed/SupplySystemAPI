using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Jobs;

/// <summary>
/// Every five minutes, across every organization: ledger claims whose worker went quiet (in flight
/// past lease + grace) become Unknown — so the next attempt looks the record up before creating it —
/// and outbox entries stuck Running past the lease go back to the queue. Pure table maintenance; it
/// calls neither QuickBooks nor any source, so it runs unfiltered rather than per tenant.
/// </summary>
internal sealed class SyncSweepJob
{
    public const string RecurringJobId = "integration-sync-sweep";

    private readonly IntegrationDbContext   _db;
    private readonly ISyncLedger            _ledger;
    private readonly IntegrationJobOptions  _options;
    private readonly TimeProvider           _clock;
    private readonly ILogger<SyncSweepJob>  _logger;

    public SyncSweepJob(
        IntegrationDbContext db, ISyncLedger ledger, IOptions<IntegrationJobOptions> options, TimeProvider clock,
        ILogger<SyncSweepJob> logger)
    {
        _db      = db;
        _ledger  = ledger;
        _options = options.Value;
        _clock   = clock;
        _logger  = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public async Task RunAsync()
    {
        var (expired, requeued) = await SweepAsync();
        if (expired > 0 || requeued > 0)
            _logger.LogInformation("QuickBooks sweep: {Expired} claim(s) marked unknown, {Requeued} stuck entr(ies) re-queued.", expired, requeued);
    }

    internal async Task<(int expired, int requeued)> SweepAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var expired = await _ledger.ExpireStaleLeasesAsync(now, ct);

        var cutoff = now - TimeSpan.FromMinutes(Math.Max(1, _options.LeaseMinutes) + Math.Max(0, _options.LeaseGraceMinutes));
        var stuck = await _db.Outbox
            .IgnoreQueryFilters()
            .Where(e => e.Status == OutboxStatus.Running && e.NextAttemptAt < cutoff)
            .ToListAsync(ct);

        foreach (var entry in stuck)
        {
            // The worker died mid-run. Running it again is safe: its claim (now Unknown) makes the
            // next attempt look the record up before creating anything.
            entry.Status        = OutboxStatus.Queued;
            entry.NextAttemptAt = now;
            entry.BlockedReason = "The previous attempt stopped without finishing; re-queued by the sweep.";
        }

        await _db.SaveChangesAsync(ct);
        return (expired, stuck.Count);
    }
}
