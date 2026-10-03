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
/// For each usable connection:
/// <list type="number">
/// <item>Records a document needs that the gateway has never received (placeholder maps with
/// <c>RequestedAt</c> and no payload) are asked of the SCM module that owns them
/// (<see cref="IQuickBooksSource.PushAsync"/>) — at most every <see cref="RepullAfter"/> per record.
/// External callers' records are never pulled: those callers were told what to send.</item>
/// <item>Waiting documents whose dependencies are all in QuickBooks now are released.</item>
/// <item>Documents still waiting after <c>DependencyWaitDays</c> fail with DEPENDENCY_TIMEOUT.</item>
/// </list>
/// </summary>
internal sealed class DependencyJob
{
    public const string RecurringJobId = "integration-dependencies";

    /// <summary>A placeholder is asked for again at most this often.</summary>
    public static readonly TimeSpan RepullAfter = TimeSpan.FromMinutes(15);

    private const int MaxPullsPerRun = 1000;
    private const int PushChunk      = 100;

    private readonly ConnectionJobRunner     _runner;
    private readonly IntegrationJobOptions   _options;
    private readonly TimeProvider            _clock;
    private readonly ILogger<DependencyJob>  _logger;

    public DependencyJob(ConnectionJobRunner runner, IOptions<IntegrationJobOptions> options, TimeProvider clock, ILogger<DependencyJob> logger)
    {
        _runner  = runner;
        _options = options.Value;
        _clock   = clock;
        _logger  = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task RunAsync() => RunOnceAsync(CancellationToken.None);

    internal async Task RunOnceAsync(CancellationToken ct = default)
    {
        await _runner.ForEachAsync("QuickBooks dependencies", usableOnly: true, async (sp, conn) =>
        {
            var now          = _clock.GetUtcNow().UtcDateTime;
            var db           = sp.GetRequiredService<IntegrationDbContext>();
            var accessor     = sp.GetRequiredService<IConnectionAccessor>();
            var dependencies = sp.GetRequiredService<ISyncDependencies>();
            var sources      = sp.GetServices<IQuickBooksSource>().ToList();

            var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == conn.Id, ct);
            if (connection is null) return;
            var settings = await accessor.GetOrCreateSettingsAsync(connection, ct);

            await PullPlaceholdersAsync(db, sources, connection.Id, now, ct);

            // Released before timing out: an entry whose last dependency just arrived is not failed.
            await dependencies.ReleaseReadyAsync(connection.Id, settings.Mode == SyncMode.DryRun, now, ct);
            await ExpireWaitingAsync(db, connection.Id, now, ct);
        }, ct);
    }

    private async Task PullPlaceholdersAsync(
        IntegrationDbContext db, List<IQuickBooksSource> sources, int connectionId, DateTime now, CancellationToken ct)
    {
        var windowStart = now.AddDays(-Math.Max(1, _options.DependencyWaitDays));
        var repullCutoff = now - RepullAfter;

        var placeholders = await db.EntityMaps
            .Where(m => m.ConnectionId == connectionId
                     && m.SourceSystem == QuickBooksSourceSystems.Scm
                     && m.PayloadJson == null
                     && m.RequestedAt != null && m.RequestedAt >= windowStart
                     && (m.ModifiedDate == null || m.ModifiedDate <= repullCutoff))
            .OrderBy(m => m.RequestedAt)
            .Take(MaxPullsPerRun)
            .ToListAsync(ct);

        if (placeholders.Count == 0) return;

        // Stamped before asking, so a source that fails is not hammered every minute.
        foreach (var map in placeholders) map.ModifiedDate = now;
        await db.SaveChangesAsync(ct);

        foreach (var group in placeholders.GroupBy(m => m.Kind))
        {
            var ids = group.Select(m => m.ExternalId).ToList();
            var owners = sources.Where(s => s.Kinds.Contains(group.Key)).ToList();
            if (owners.Count == 0)
            {
                _logger.LogWarning("No QuickBooks source is registered for {Kind}; {Count} requested record(s) cannot be pulled.", group.Key, ids.Count);
                continue;
            }

            foreach (var source in owners)
            foreach (var chunk in ids.Chunk(PushChunk))
            {
                try
                {
                    await source.PushAsync(group.Key, chunk, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "QuickBooks source {Source} failed to push {Count} {Kind} record(s).",
                        source.GetType().Name, chunk.Length, group.Key);
                }
            }
        }
    }

    private async Task ExpireWaitingAsync(IntegrationDbContext db, int connectionId, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-Math.Max(1, _options.DependencyWaitDays));

        var expired = await db.Outbox
            .Include(e => e.EntityMap)
            .Where(e => e.ConnectionId == connectionId
                     && e.Status == OutboxStatus.WaitingOnDependency
                     && e.WaitingSince != null && e.WaitingSince <= cutoff)
            .ToListAsync(ct);

        foreach (var entry in expired)
        {
            var missing = SyncPayloads.DeserializeDependencies(entry.DependsOnJson);
            var list    = missing.Count == 0 ? "records it references" : string.Join(", ", missing.Select(d => $"{d.Kind} {d.ExternalId}"));
            var message = $"Waited {_options.DependencyWaitDays} days for {list} to reach QuickBooks, and gave up. " +
                          "Send (or fix) those records, then Retry this one.";

            entry.Status        = OutboxStatus.Failed;
            entry.CompletedAt   = now;
            entry.BlockedReason = SyncText.Clip(message, 1000);

            entry.EntityMap.State         = SyncState.Failed;
            entry.EntityMap.LastErrorCode = "DEPENDENCY_TIMEOUT";
            entry.EntityMap.LastError     = SyncText.Clip(message, 2000);
        }

        if (expired.Count > 0) await db.SaveChangesAsync(ct);
    }
}
