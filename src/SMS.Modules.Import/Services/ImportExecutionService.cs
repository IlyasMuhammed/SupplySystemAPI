using System.Text.Json;
using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Hubs;
using SMS.Modules.Import.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Import.Services;

public interface IImportExecutionService
{
    /// <summary>POST execute — runs inline up to <see cref="ImportLimits.BackgroundThreshold"/> valid rows, else enqueues the job.</summary>
    Task<ImportExecuteResult> ExecuteAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
    /// <summary>Applies an IMPORTING session's VALID rows batch by batch (the job's body).</summary>
    Task RunAsync(Guid organizationId, Guid sessionUuid, int userId, CancellationToken ct = default);
}

/// <summary>Puts a background import on Hangfire's "import" queue (a seam so tests need no Hangfire storage).</summary>
internal interface IImportJobScheduler
{
    string Enqueue(Guid organizationId, Guid sessionUuid, int userId);
    /// <summary>True while the job is enqueued, scheduled, awaiting or processing; false when it ended or is unknown to
    /// the storage; null when the storage cannot be asked.</summary>
    bool? IsAlive(string jobId);
}

internal sealed class HangfireImportJobScheduler : IImportJobScheduler
{
    private static readonly HashSet<string> LiveStates = new(StringComparer.OrdinalIgnoreCase)
        { EnqueuedState.StateName, ProcessingState.StateName, ScheduledState.StateName, AwaitingState.StateName };

    private readonly IBackgroundJobClient _jobs;
    public HangfireImportJobScheduler(IBackgroundJobClient jobs) => _jobs = jobs;

    public string Enqueue(Guid organizationId, Guid sessionUuid, int userId) =>
        _jobs.Create<ImportExecutionJob>(j => j.RunAsync(organizationId, sessionUuid, userId), new EnqueuedState(ImportLimits.Queue));

    public bool? IsAlive(string jobId)
    {
        try
        {
            using var connection = JobStorage.Current.GetConnection();
            var state = connection.GetStateData(jobId);
            return state is not null && LiveStates.Contains(state.Name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// A40 D-38 — every 10 minutes and once at startup: an IMPORTING session with no batch saved for
/// <see cref="ImportLimits.StaleAfter"/> (its ModifiedAt, stamped by every batch save) and no live Hangfire job becomes
/// FAILED with <see cref="ImportMessages.Interrupted"/>, which frees the one-running-import-per-entity index. Rows already
/// marked keep their outcome; the rows still VALID are re-validated and re-run (D-39). Returns the sessions recovered.
/// </summary>
internal sealed class ImportRecoveryJob
{
    internal const string RecurringJobId = "import-interrupted-recovery";
    internal const string Cron = "*/10 * * * *";

    private readonly ImportDbContext _db;
    private readonly IImportJobScheduler? _scheduler;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public ImportRecoveryJob(ImportDbContext db, IImportJobScheduler? scheduler = null, TimeProvider? clock = null,
        ILogger<ImportRecoveryJob>? log = null)
    {
        _db = db; _scheduler = scheduler; _clock = clock ?? TimeProvider.System;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(600)]
    public async Task<int> RunAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var cutoff = now - ImportLimits.StaleAfter;
        var stuck = await _db.Sessions.IgnoreQueryFilters()
            .Where(s => s.Status == ImportSessionStatus.Importing && s.ModifiedAt < cutoff).ToListAsync();
        var recovered = 0;
        foreach (var session in stuck)
        {
            // A queued or running job is alive (a Hangfire backlog is not an interruption); storage unreachable → wait.
            if (session.JobId is not null && _scheduler?.IsAlive(session.JobId) is not false) continue;
            MarkInterrupted(session, now);
            recovered++;
        }
        if (recovered > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogWarning("Import recovery: {Count} interrupted session(s) marked FAILED.", recovered);
        }
        await CancelAbandonedAsync(now);
        return recovered;
    }

    /// <summary>D-40 — an UPLOADED session untouched for <see cref="ImportLimits.AbandonedAfter"/> (e.g. the user picked
    /// another sheet, which re-uploads) becomes CANCELLED. It never blocked anything — only IMPORTING does; this keeps the
    /// session list clean. Returns the sessions cancelled.</summary>
    internal async Task<int> CancelAbandonedAsync(DateTime now)
    {
        var cutoff = now - ImportLimits.AbandonedAfter;
        var abandoned = await _db.Sessions.IgnoreQueryFilters()
            .Where(s => s.Status == ImportSessionStatus.Uploaded && s.ModifiedAt < cutoff).ToListAsync();
        foreach (var session in abandoned)
        {
            session.Status = ImportSessionStatus.Cancelled;
            session.ErrorMessage = ImportMessages.Abandoned;
        }
        if (abandoned.Count > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogInformation("Import recovery: {Count} abandoned upload(s) cancelled.", abandoned.Count);
        }
        return abandoned.Count;
    }

    internal static void MarkInterrupted(ImportSession session, DateTime now)
    {
        session.Status = ImportSessionStatus.Failed;
        session.ErrorMessage = ImportMessages.Interrupted;
        session.ImportCompletedAt = now;
    }
}

/// <summary>A40 C8 — the background import (queue "import", never retried automatically, in the org's tenant scope).</summary>
internal sealed class ImportExecutionJob
{
    private readonly IImportExecutionService _execution;
    public ImportExecutionJob(IImportExecutionService execution) => _execution = execution;

    [AutomaticRetry(Attempts = 0)]
    [Queue(ImportLimits.Queue)]
    public async Task RunAsync(Guid organizationId, Guid sessionUuid, int userId)
    {
        HangfireTenantScope.OrganizationId = organizationId;
        try
        {
            await _execution.RunAsync(organizationId, sessionUuid, userId);
        }
        finally
        {
            HangfireTenantScope.OrganizationId = null;
        }
    }
}

/// <summary>
/// A40 C8 — execution: only VALID rows, in batches of 100 handed to the target's ApplyAsync (one transaction of the owning
/// module per batch; a batch that throws is marked ERROR / IMPORT_ERROR with the message and the import continues),
/// duplicate strategies (SKIP marks matched rows SKIPPED, UPDATE updates them, CREATE_NEW always creates), external
/// references registered per created/updated row, counters and hub progress after every batch, cancel checked before
/// each batch. Final status COMPLETED / COMPLETED_WITH_ERRORS / FAILED.
/// </summary>
internal sealed class ImportExecutionService : IImportExecutionService
{
    private readonly ImportDbContext _db;
    private readonly ImportTargetRegistry _registry;
    private readonly ITenantContext _tenant;
    private readonly ImportOpeningGate _opening;
    private readonly IImportSessionService _sessions;
    private readonly IImportJobScheduler? _scheduler;
    private readonly IImportHubNotifier? _hub;
    private readonly ILogger _log;

    public ImportExecutionService(ImportDbContext db, ImportTargetRegistry registry, ITenantContext tenant, ImportOpeningGate opening,
        IImportSessionService sessions, IImportJobScheduler? scheduler = null, IImportHubNotifier? hub = null,
        ILogger<ImportExecutionService>? log = null)
    {
        _db = db; _registry = registry; _tenant = tenant; _opening = opening; _sessions = sessions; _scheduler = scheduler; _hub = hub;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task<ImportExecuteResult> ExecuteAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        await _sessions.GetAsync(uuid, caller, ct); // visibility (own / IMPORT_MANAGE)
        var session = await _db.Sessions.IgnoreQueryFilters().FirstAsync(s => s.Uuid == uuid && s.OrganizationId == org, ct);
        // An interrupted (FAILED) or cancelled import is re-validated before the remaining rows run (D-39).
        if (session.Status is ImportSessionStatus.Uploaded or ImportSessionStatus.Mapping or ImportSessionStatus.Validating
            or ImportSessionStatus.Failed or ImportSessionStatus.Cancelled)
            throw new BadRequestException(ImportMessages.ValidateFirst);
        if (session.Status != ImportSessionStatus.Validated) throw new BadRequestException(ImportMessages.NotRunnable);
        var entry = _registry.Get(session.EntityType);
        await _opening.EnsureAllowedAsync(org, entry.Definition, caller, ct);
        if (session.ValidRows == 0) throw new BadRequestException(ImportMessages.NoValidRows);
        if (await _db.Sessions.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.EntityType == session.EntityType
                                                                  && s.Status == ImportSessionStatus.Importing && s.Id != session.Id, ct))
            throw new BadRequestException(ImportMessages.AlreadyRunning(entry.Definition.Name));

        session.Status = ImportSessionStatus.Importing;
        session.ImportStartedAt = DateTime.UtcNow;
        session.ImportCompletedAt = null;
        session.ExecutedBy = caller.UserId;
        session.ProcessedRows = 0;
        session.RowsToProcess = session.ValidRows;
        session.ErrorMessage = null;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The filtered unique index (one IMPORTING session per org + entity type) lost a race.
            throw new BadRequestException(ImportMessages.AlreadyRunning(entry.Definition.Name));
        }

        var background = session.ValidRows > ImportLimits.BackgroundThreshold && _scheduler is not null;
        string? jobId = null;
        if (background)
        {
            jobId = _scheduler!.Enqueue(org, session.Uuid, caller.UserId);
            session.JobId = jobId;
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            await RunAsync(org, session.Uuid, caller.UserId, ct);
        }

        _db.ChangeTracker.Clear();
        return new ImportExecuteResult { IsBackground = background, JobId = jobId, Session = await _sessions.GetAsync(uuid, caller, ct) };
    }

    public async Task RunAsync(Guid organizationId, Guid sessionUuid, int userId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Uuid == sessionUuid && s.OrganizationId == organizationId, ct);
        if (session is null || session.Status != ImportSessionStatus.Importing) return;

        try
        {
            var entry = _registry.Get(session.EntityType);
            var ids = await _db.Rows.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.SessionId == session.Id && r.Status == ImportRowStatus.Valid)
                .OrderBy(r => r.RowNumber).Select(r => r.Id).ToListAsync(ct);
            session.RowsToProcess = ids.Count;
            var recipients = new[] { session.CreatedBy, userId };

            foreach (var batchIds in ids.Chunk(ImportLimits.BatchSize))
            {
                var status = await _db.Sessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == session.Id)
                    .Select(s => s.Status).FirstAsync(ct);
                // Cancelled, or FAILED by recovery / a manager's forced cancel: no further batch.
                if (status != ImportSessionStatus.Importing) break;

                await ApplyBatchAsync(session, entry, batchIds, organizationId, userId, ct);
                session.ProcessedRows += batchIds.Length;
                await SaveKeepingCancelAsync(session, ct);
                if (_hub is not null)
                    await _hub.ProgressAsync(organizationId, recipients,
                        new ImportProgressModel(session.Uuid, session.ProcessedRows, session.RowsToProcess, session.Status));
            }

            var finalStatus = await _db.Sessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == session.Id)
                .Select(s => s.Status).FirstAsync(ct);
            if (finalStatus == ImportSessionStatus.Importing)
            {
                var applied = session.ImportedRows + session.UpdatedRows + session.SkippedRows;
                session.Status = session.ErrorRows == 0 && session.InvalidRows == 0 ? ImportSessionStatus.Completed
                    : applied == 0 ? ImportSessionStatus.Failed
                    : ImportSessionStatus.CompletedWithErrors;
                if (session.Status == ImportSessionStatus.Failed) session.ErrorMessage ??= "No row could be imported.";
            }
            else session.Status = finalStatus; // keep CANCELLED / FAILED (the message recovery wrote stays)
            session.ImportCompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            if (_hub is not null)
                await _hub.ProgressAsync(organizationId, recipients,
                    new ImportProgressModel(session.Uuid, session.ProcessedRows, session.RowsToProcess, session.Status));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Import session {Session} failed.", sessionUuid);
            _db.ChangeTracker.Clear();
            var failed = await _db.Sessions.IgnoreQueryFilters().FirstAsync(s => s.Id == session.Id, CancellationToken.None);
            failed.Status = ImportSessionStatus.Failed;
            failed.ErrorMessage = Truncate(ex.Message, 2000);
            failed.ImportCompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>D-39 — commits the batch's row outcomes and the counters before the next batch starts, without overwriting a
    /// CANCELLED / FAILED written meanwhile by cancel or recovery.</summary>
    private async Task SaveKeepingCancelAsync(ImportSession session, CancellationToken ct)
    {
        var current = await _db.Sessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.Id == session.Id).Select(s => s.Status).FirstAsync(ct);
        if (current != ImportSessionStatus.Importing) session.Status = current;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyBatchAsync(ImportSession session, ImportTargetEntry entry, long[] batchIds, Guid org, int userId, CancellationToken ct)
    {
        var rows = await _db.Rows.IgnoreQueryFilters().Where(r => batchIds.Contains(r.Id)).OrderBy(r => r.RowNumber).ToListAsync(ct);
        var strategy = session.DuplicateStrategy;
        var toApply = new List<(ImportRow Row, ImportApplyRow Apply)>();

        foreach (var row in rows)
        {
            if (row.ExistingEntityUuid is { } existing && strategy == ImportDuplicateStrategy.Skip)
            {
                row.Status = ImportRowStatus.Skipped;
                row.EntityUuid = existing;
                session.SkippedRows++;
                continue;
            }
            var update = row.ExistingEntityUuid is not null && strategy == ImportDuplicateStrategy.Update;
            var values = (row.ValuesJson is null ? new Dictionary<string, string?>()
                    : JsonSerializer.Deserialize<Dictionary<string, string?>>(row.ValuesJson, ImportSessionService.Json) ?? new())
                .Where(kv => !kv.Key.Equals(ImportLimits.ExternalIdField, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            toApply.Add((row, new ImportApplyRow(row.RowNumber, values, update ? "UPDATE" : "CREATE", update ? row.ExistingEntityUuid : null)));
        }
        if (toApply.Count == 0) return;

        IReadOnlyList<ImportRowOutcome> outcomes;
        try
        {
            outcomes = await entry.Target.ApplyAsync(org, userId, toApply.Select(a => a.Apply).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // IMP-EXC-02 — the target rolled the whole batch back. A target names the failing row as "Row n: …": that row
            // carries the message, the others "Batch rolled back because of row n."; without a row number every row
            // carries the message.
            var failing = FailingRow(ex.Message);
            if (failing is not null && toApply.All(a => a.Row.RowNumber != failing)) failing = null;
            foreach (var (row, _) in toApply)
            {
                var message = failing is null || row.RowNumber == failing
                    ? Truncate(ex.Message, 1000)
                    : $"Batch rolled back because of row {failing}.";
                row.Status = ImportRowStatus.Error;
                row.ErrorsJson = JsonSerializer.Serialize(new[] { new ImportFieldError(string.Empty, ImportErrorCodes.ImportError, message, null) },
                    ImportSessionService.Json);
            }
            session.ErrorRows += toApply.Count;
            _log.LogWarning(ex, "Import session {Session}: a batch of {Count} rows failed.", session.Uuid, toApply.Count);
            return;
        }

        var byRow = outcomes.GroupBy(o => o.RowNumber).ToDictionary(g => g.Key, g => g.First());
        var done = new List<ImportRow>();
        foreach (var (row, _) in toApply)
        {
            if (!byRow.TryGetValue(row.RowNumber, out var outcome))
            {
                row.Status = ImportRowStatus.Error;
                row.ErrorsJson = JsonSerializer.Serialize(new[] { new ImportFieldError(string.Empty, ImportErrorCodes.ImportError,
                    "The row was not imported (no outcome was returned).", null) }, ImportSessionService.Json);
                session.ErrorRows++;
                continue;
            }
            row.EntityUuid = outcome.EntityUuid;
            if (outcome.Status.Equals(ImportRowStatus.Skipped, StringComparison.OrdinalIgnoreCase))
            {
                // The target declined the row (e.g. UNIT: an UPDATE of a global system unit) — no reference registered.
                row.Status = ImportRowStatus.Skipped;
                session.SkippedRows++;
                continue;
            }
            var updated = outcome.Status.Equals(ImportRowStatus.Updated, StringComparison.OrdinalIgnoreCase);
            row.Status = updated ? ImportRowStatus.Updated : ImportRowStatus.Imported;
            if (updated) session.UpdatedRows++; else session.ImportedRows++;
            done.Add(row);
        }
        await RegisterExternalReferencesAsync(org, session, done, ct);
    }

    /// <summary>M040.7 — upsert (org, entity, externalId) → entity uuid; an entity keeps one external id (the latest).</summary>
    private async Task RegisterExternalReferencesAsync(Guid org, ImportSession session, List<ImportRow> rows, CancellationToken ct)
    {
        var withExt = rows.Where(r => !string.IsNullOrWhiteSpace(r.ExternalId) && r.EntityUuid is not null).ToList();
        if (withExt.Count == 0) return;
        var extIds = withExt.Select(r => r.ExternalId!).ToList();
        var uuids = withExt.Select(r => r.EntityUuid!.Value).ToList();
        var refs = await _db.ExternalReferences.IgnoreQueryFilters()
            .Where(x => x.OrganizationId == org && x.EntityType == session.EntityType && (extIds.Contains(x.ExternalId) || uuids.Contains(x.EntityUuid)))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var row in withExt)
        {
            var ext = row.ExternalId!;
            var uuid = row.EntityUuid!.Value;
            var byExt = refs.FirstOrDefault(x => x.ExternalId.Equals(ext, StringComparison.OrdinalIgnoreCase));
            var byEntity = refs.FirstOrDefault(x => x.EntityUuid == uuid);
            if (byExt is not null)
            {
                if (byEntity is not null && byEntity != byExt)
                {
                    _db.ExternalReferences.Remove(byEntity);
                    refs.Remove(byEntity);
                }
                byExt.EntityUuid = uuid;
                byExt.LastSessionUuid = session.Uuid;
            }
            else if (byEntity is not null)
            {
                byEntity.ExternalId = ext;
                byEntity.LastSessionUuid = session.Uuid;
            }
            else
            {
                var created = new ImportExternalReference
                {
                    OrganizationId = org, EntityType = session.EntityType, ExternalId = ext, EntityUuid = uuid,
                    LastSessionUuid = session.Uuid, CreatedAt = now
                };
                _db.ExternalReferences.Add(created);
                refs.Add(created);
            }
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static readonly System.Text.RegularExpressions.Regex RowPrefix =
        new(@"^\s*Row\s+(\d+)\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The row number of a target's "Row n: …" refusal, or null.</summary>
    internal static int? FailingRow(string? message) =>
        message is not null && RowPrefix.Match(message) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
}
