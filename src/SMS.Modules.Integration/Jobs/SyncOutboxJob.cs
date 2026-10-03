using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Jobs;

/// <summary>
/// Every minute: for each usable connection, sends its due outbox entries — oldest first, one at a
/// time (QuickBooks throttles per company), at most a batch per run. A throttled or revoked
/// connection stops its own batch; the others carry on.
/// <para>
/// When a connection's settings say Live, records that only dry-ran so far are queued first, so
/// switching Dry run → Live actually sends what the dry run checked.
/// </para>
/// </summary>
internal sealed class SyncOutboxJob
{
    public const string RecurringJobId = "integration-sync-outbox";

    /// <summary>How many dry-run records are promoted to real pushes per connection per run.</summary>
    public const int PromotionBatch = 500;

    private readonly ConnectionJobRunner     _runner;
    private readonly IntegrationJobOptions   _options;
    private readonly TimeProvider            _clock;
    private readonly ILogger<SyncOutboxJob>  _logger;

    public SyncOutboxJob(ConnectionJobRunner runner, IOptions<IntegrationJobOptions> options, TimeProvider clock, ILogger<SyncOutboxJob> logger)
    {
        _runner  = runner;
        _options = options.Value;
        _clock   = clock;
        _logger  = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task RunAsync() => RunOnceAsync(CancellationToken.None);

    /// <returns>How many entries were executed.</returns>
    internal async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var executed = 0;

        await _runner.ForEachAsync("QuickBooks outbox", usableOnly: true, async (sp, conn) =>
        {
            var now      = _clock.GetUtcNow().UtcDateTime;
            var db       = sp.GetRequiredService<IntegrationDbContext>();
            var accessor = sp.GetRequiredService<IConnectionAccessor>();

            var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == conn.Id, ct);
            if (connection is null || !connection.IsUsable()) return;

            var settings = await accessor.GetOrCreateSettingsAsync(connection, ct);
            if (settings.Mode == SyncMode.Live)
            {
                var promoted = await PromoteDryRunAsync(db, sp.GetRequiredService<ISyncOutbox>(), connection.Id, now, ct);
                if (promoted > 0)
                    _logger.LogInformation("QuickBooks connection {ConnectionId} is live: {Count} dry-run record(s) queued to send.", connection.Id, promoted);
            }

            // Blocked records whose cause may have been fixed outside this module (an exchange rate entered in
            // SMS, above all): a small rotating batch, re-validated as this connection's organization. What
            // passes is queued now and so sent by this very run.
            await RevalidateBlockedAsync(sp, conn, ct);

            var due = await db.Outbox
                .Where(e => e.ConnectionId == connection.Id && e.Status == OutboxStatus.Queued && e.NextAttemptAt <= now)
                .OrderBy(e => e.NextAttemptAt)
                .ThenBy(e => e.Id)
                .Select(e => e.Id)
                .Take(Math.Max(1, _options.OutboxBatchSize))
                .ToListAsync(ct);

            foreach (var entryId in due)
            {
                SyncExecutionResult? result = null;

                // A fresh scope per entry: a clean change tracker, and one entry's failure stays its own.
                await _runner.InTenantAsync(conn.OrganizationId, async entrySp =>
                    result = await entrySp.GetRequiredService<ISyncExecutor>().ExecuteAsync(entryId, ct), ct);

                executed++;
                if (result?.StopConnection == true) break;
            }
        }, ct);

        return executed;
    }

    private async Task RevalidateBlockedAsync(IServiceProvider sp, ConnectionRef conn, CancellationToken ct)
    {
        try
        {
            var summary = await sp.GetRequiredService<IBlockedRecordRevalidator>()
                .RevalidateAsync(conn.Id, conn.OrganizationId, RevalidationTrigger.Periodic, ct);
            if (summary.Released > 0)
                _logger.LogInformation("QuickBooks connection {ConnectionId}: {Count} blocked record(s) pass validation now and were queued.",
                    conn.Id, summary.Released);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never in the way of sending what is already due.
            _logger.LogWarning(ex, "QuickBooks connection {ConnectionId}: re-validating blocked records failed.", conn.Id);
        }
    }

    /// <summary>Records that dry-ran cleanly and have not been sent → queued (with the operation they last had).</summary>
    internal static async Task<int> PromoteDryRunAsync(
        IntegrationDbContext db, ISyncOutbox outbox, int connectionId, DateTime now, CancellationToken ct)
    {
        var maps = await db.EntityMaps
            .Where(m => m.ConnectionId == connectionId && m.State == SyncState.DryRunOk && m.PayloadJson != null)
            .OrderBy(m => m.Id)
            .Take(PromotionBatch)
            .ToListAsync(ct);

        var promoted = 0;
        foreach (var map in maps)
        {
            if (await outbox.HasRunningEntryAsync(map, ct) || await outbox.GetPendingEntryAsync(map, ct) is not null) continue;

            var lastOperation = await db.Outbox.Where(e => e.EntityMapId == map.Id)
                .OrderByDescending(e => e.Id)
                .Select(e => (OutboxOperation?)e.Operation)
                .FirstOrDefaultAsync(ct) ?? OutboxOperation.Upsert;

            await outbox.EnqueueAsync(map, lastOperation, now, ct: ct);
            promoted++;
        }

        if (promoted > 0) await db.SaveChangesAsync(ct);
        return promoted;
    }
}
