using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;

namespace SMS.Modules.Import.Services;

/// <summary>
/// A40 D-10 — daily 02:15 UTC: deletes the stored rows of sessions created more than 90 days ago (any organization); the
/// session row with its counters stays as the summary (RowsPurgedAt set). Sessions still importing are left alone.
/// Returns the number of rows deleted.
/// </summary>
internal sealed class ImportPurgeJob
{
    internal const string RecurringJobId = "import-row-purge";
    internal const string Cron = "15 2 * * *";
    private const int Chunk = 2_000;

    private readonly ImportDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public ImportPurgeJob(ImportDbContext db, TimeProvider? clock = null, ILogger<ImportPurgeJob>? log = null)
    {
        _db = db; _clock = clock ?? TimeProvider.System;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(3600)]
    public async Task<int> RunAsync()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-ImportLimits.RetentionDays);
        var sessions = await _db.Sessions.IgnoreQueryFilters()
            .Where(s => s.CreatedAt < cutoff && s.RowsPurgedAt == null && s.Status != ImportSessionStatus.Importing)
            .ToListAsync();
        var deleted = 0;
        foreach (var session in sessions)
        {
            while (true)
            {
                var batch = await _db.Rows.IgnoreQueryFilters().Where(r => r.SessionId == session.Id).OrderBy(r => r.Id).Take(Chunk).ToListAsync();
                if (batch.Count == 0) break;
                _db.Rows.RemoveRange(batch);
                await _db.SaveChangesAsync();
                deleted += batch.Count;
            }
            session.RowsPurgedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync();
        }
        if (deleted > 0) _log.LogInformation("Import purge: {Rows} row(s) of {Sessions} session(s) deleted.", deleted, sessions.Count);
        return deleted;
    }
}
