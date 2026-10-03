using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Jobs;

/// <summary>
/// Hourly: for each usable connection, asks every registered source for records of each enabled
/// kind changed since the last complete run (<see cref="IQuickBooksSource.PushAllAsync"/>). This is
/// what catches anything SCM failed to send on its own — a gateway call must never fail SCM's
/// operation, so a missed one is expected, and repaired here.
/// <para>
/// The "since" point advances only when every source answered, and is the <i>start</i> of the run, so
/// a change made during the run is picked up next time. A first run starts from when the company was
/// connected — the initial backfill is an explicit admin action, not this job's.
/// </para>
/// </summary>
internal sealed class ReconciliationJob
{
    public const string RecurringJobId = "integration-reconciliation";

    private readonly ConnectionJobRunner         _runner;
    private readonly TimeProvider                _clock;
    private readonly ILogger<ReconciliationJob>  _logger;

    public ReconciliationJob(ConnectionJobRunner runner, TimeProvider clock, ILogger<ReconciliationJob> logger)
    {
        _runner = runner;
        _clock  = clock;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public Task RunAsync() => RunOnceAsync(CancellationToken.None);

    internal async Task RunOnceAsync(CancellationToken ct = default)
    {
        await _runner.ForEachAsync("QuickBooks reconciliation", usableOnly: true, async (sp, conn) =>
        {
            var startedAt = _clock.GetUtcNow().UtcDateTime;
            var db        = sp.GetRequiredService<IntegrationDbContext>();
            var accessor  = sp.GetRequiredService<IConnectionAccessor>();
            var sources   = sp.GetServices<IQuickBooksSource>().ToList();

            var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == conn.Id, ct);
            if (connection is null) return;

            var settings = await accessor.GetOrCreateSettingsAsync(connection, ct);
            var since    = settings.LastReconciledAt ?? connection.ConnectedAt ?? startedAt;
            var complete = true;
            var sent     = 0;

            // SCM's product variants carry no modified timestamp, so an edit to a variant alone is
            // invisible to "changed since". Items therefore get one full pass on the first run of each
            // UTC day — cheap, because an unchanged payload is a fingerprint no-op in the gateway.
            var fullItemPass = settings.LastReconciledAt is null || settings.LastReconciledAt.Value.Date < startedAt.Date;

            // Parties and items first, so documents sent in the same run find them.
            foreach (var kind in Enum.GetValues<SyncKind>().Where(k => Enabled(settings, k)))
            foreach (var source in sources.Where(s => s.Kinds.Contains(kind)))
            {
                try
                {
                    var kindSince = kind == SyncKind.Item && fullItemPass ? (DateTime?)null : since;
                    sent += await source.PushAllAsync(kind, kindSince, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    complete = false;
                    _logger.LogError(ex, "QuickBooks reconciliation: source {Source} failed for {Kind} (connection {ConnectionId}).",
                        source.GetType().Name, kind, connection.Id);
                }
            }

            if (!complete) return;

            // Re-read: a source's gateway calls may have reset this context's change tracker.
            var fresh = await db.Settings.FirstAsync(s => s.ConnectionId == connection.Id, ct);
            fresh.LastReconciledAt = startedAt;
            await db.SaveChangesAsync(ct);

            if (sent > 0)
                _logger.LogInformation("QuickBooks reconciliation for connection {ConnectionId}: {Sent} record(s) re-sent since {Since:u}.",
                    connection.Id, sent, since);
        }, ct);
    }

    private static bool Enabled(IntegrationSettings s, SyncKind kind) => kind switch
    {
        SyncKind.Customer     => s.AutoPushCustomers,
        SyncKind.Vendor       => s.AutoPushVendors,
        SyncKind.Item         => s.AutoPushItems,
        SyncKind.SalesInvoice => s.AutoPushSalesInvoices,
        SyncKind.Bill         => s.AutoPushBills,
        _                     => false
    };
}
