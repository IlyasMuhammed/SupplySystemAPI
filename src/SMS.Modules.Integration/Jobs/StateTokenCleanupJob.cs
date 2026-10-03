using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Jobs;

internal sealed record StateCleanupSummary(int TokensDeleted, int ConnectionsReset);

/// <summary>
/// Daily housekeeping for the connect flow: deletes OAuth state tokens that were used or expired more
/// than a day ago (they have no use left, and a day's grace keeps them for anyone investigating a
/// failed connect), and returns connections stuck in Connecting — a consent that was abandoned — to
/// NotConnected once no live state token is left that could finish them.
/// </summary>
internal sealed class StateTokenCleanupJob
{
    internal const string RecurringJobId = "integration-state-token-cleanup";
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    private readonly IntegrationDbContext          _db;
    private readonly ILogger<StateTokenCleanupJob> _logger;

    public StateTokenCleanupJob(IntegrationDbContext db, ILogger<StateTokenCleanupJob> logger)
    {
        _db     = db;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync()
    {
        var summary = await CleanAsync();
        if (summary.TokensDeleted > 0 || summary.ConnectionsReset > 0)
            _logger.LogInformation("QuickBooks state cleanup: {Deleted} state token(s) deleted, {Reset} abandoned connect(s) reset.",
                summary.TokensDeleted, summary.ConnectionsReset);
    }

    internal async Task<StateCleanupSummary> CleanAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now    = utcNow ?? DateTime.UtcNow;
        var cutoff = now - Retention;

        // Every organization, by hand.
        var stale = _db.OAuthStateTokens
            .IgnoreQueryFilters()
            .Where(t => (t.UsedAt != null && t.UsedAt < cutoff) || t.ExpiresAt < cutoff);

        int deleted;
        if (_db.Database.IsRelational())
        {
            deleted = await stale.ExecuteDeleteAsync(ct);
        }
        else
        {
            // The in-memory provider used by unit tests cannot run set-based deletes.
            var rows = await stale.ToListAsync(ct);
            _db.OAuthStateTokens.RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
            deleted = rows.Count;
        }

        var abandoned = await _db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.Status == ConnectionStatus.Connecting
                     && !_db.OAuthStateTokens.IgnoreQueryFilters()
                            .Any(t => t.OrganizationId == c.OrganizationId && t.UsedAt == null && t.ExpiresAt > now))
            .ToListAsync(ct);

        var reset = 0;
        foreach (var connection in abandoned)
        {
            connection.Status       = ConnectionStatus.NotConnected;
            connection.ModifiedDate = now;
            try
            {
                await _db.SaveChangesAsync(ct);
                reset++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone was finishing that connect at this very moment — leave it to them.
                _db.Entry(connection).State = EntityState.Detached;
            }
        }

        return new StateCleanupSummary(deleted, reset);
    }
}
