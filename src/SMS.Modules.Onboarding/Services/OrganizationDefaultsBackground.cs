using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Modules.Onboarding.Models;
using SMS.Modules.Onboarding.Services.Defaults;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Services;

// A51 P7 (A51-D7) — the default-data apply of a freshly created organization runs in the background: the create call returns
// at once and the super admin polls GET api/system/organizations/{id}/defaults-status. The queue and the tracker are in
// memory (one API process): a restart loses a queued job and forgets a running one, but the work is idempotent and the
// durable state (OrganizationSetups + one TenantSetupLogs row per finished category) lets the status endpoint and the
// organization admin's wizard carry on from where it stopped.

/// <summary>One queued apply.</summary>
internal sealed record OrganizationDefaultsJob(Guid OrganizationId, string CountryCode, string IndustryType, int? UserId, DateTime QueuedAt);

/// <summary>The in-process queue between the create request and <see cref="OrganizationDefaultsWorker"/>.</summary>
internal sealed class OrganizationDefaultsQueue
{
    private readonly Channel<OrganizationDefaultsJob> _channel = Channel.CreateUnbounded<OrganizationDefaultsJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<OrganizationDefaultsJob> Reader => _channel.Reader;

    public bool TryEnqueue(OrganizationDefaultsJob job) => _channel.Writer.TryWrite(job);
}

/// <summary>What this process knows about each organization's background apply (cleared on restart).</summary>
internal sealed class OrganizationDefaultsTracker
{
    internal sealed class Entry
    {
        public DateTime QueuedAt { get; init; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        /// <summary>Running (queued or running) / Completed / InProgress / Failed.</summary>
        public string Status { get; set; } = OrganizationDefaultsStatuses.Running;
        public OrganizationDefaultsOutcome? Outcome { get; set; }
        public string? Message { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public Entry Queued(Guid org, DateTime queuedAt)
    {
        Prune();
        var entry = new Entry { QueuedAt = queuedAt };
        _entries[org] = entry;
        return entry;
    }

    public Entry? Get(Guid org) => _entries.GetValueOrDefault(org);

    public void Started(Guid org, DateTime at)
    {
        if (_entries.TryGetValue(org, out var e)) e.StartedAt = at;
    }

    public void Finished(Guid org, OrganizationDefaultsOutcome outcome, DateTime at)
    {
        if (!_entries.TryGetValue(org, out var e)) return;
        e.Outcome = outcome;
        e.Message = outcome.Message;
        e.FinishedAt = at;
        e.Status = outcome.Status is OrganizationDefaultsStatuses.Failed ? OrganizationDefaultsStatuses.Failed
            : outcome.Completed ? OrganizationDefaultsStatuses.Completed
            : OrganizationDefaultsStatuses.InProgress;
    }

    public void Failed(Guid org, string message, DateTime at)
    {
        if (!_entries.TryGetValue(org, out var e)) return;
        e.Message = message;
        e.FinishedAt = at;
        e.Status = OrganizationDefaultsStatuses.Failed;
    }

    /// <summary>Finished entries older than a day are forgotten (the durable rows still answer).</summary>
    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var (org, e) in _entries)
            if (e.FinishedAt is { } done && done < cutoff) _entries.TryRemove(org, out _);
    }
}

/// <summary>
/// Runs the queued applies one at a time, each in its own DI scope under a <see cref="TenantOverrideScope"/> for the new
/// organization (<see cref="OrganizationDefaultsApplier.ApplyAsync"/>). It never uses the create request's token, so a closed
/// browser tab or a cancelled request does not stop the apply; only the host shutting down does. A failure is logged and recorded.
/// </summary>
internal sealed class OrganizationDefaultsWorker : BackgroundService
{
    private readonly OrganizationDefaultsQueue _queue;
    private readonly OrganizationDefaultsTracker _tracker;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OrganizationDefaultsWorker> _log;

    public OrganizationDefaultsWorker(OrganizationDefaultsQueue queue, OrganizationDefaultsTracker tracker, IServiceScopeFactory scopes,
        ILogger<OrganizationDefaultsWorker> log)
    {
        _queue = queue;
        _tracker = tracker;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                await RunAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: a half-applied organization stays InProgress; its admin can finish the wizard (idempotent).
        }
    }

    internal async Task RunAsync(OrganizationDefaultsJob job, CancellationToken ct)
    {
        _tracker.Started(job.OrganizationId, DateTime.UtcNow);
        try
        {
            using var scope = _scopes.CreateScope();
            var applier = scope.ServiceProvider.GetRequiredService<IOrganizationDefaultsApplier>();
            // ApplyAsync never throws: a failure comes back as Status = Failed with a message.
            var outcome = await applier.ApplyAsync(job.OrganizationId, job.CountryCode, job.IndustryType, job.UserId, ct);
            _tracker.Finished(job.OrganizationId, outcome, DateTime.UtcNow);
            _log.LogInformation("Default data for organization {Organization} finished: {Status}.", job.OrganizationId, outcome.Status);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The background default-data apply for organization {Organization} failed.", job.OrganizationId);
            _tracker.Failed(job.OrganizationId, $"The default data could not be applied ({ex.Message}); the organization admin can finish in the setup wizard.", DateTime.UtcNow);
        }
    }
}

/// <summary>Combines the in-memory tracker with the durable rows (OrganizationSetups, TenantSetupLogs) into one status answer.</summary>
internal sealed class OrganizationDefaultsStatusReader : IOrganizationDefaultsStatusReader
{
    private readonly OnboardingDbContext _db;
    private readonly SetupPayloadValidator _validator;
    private readonly OrganizationDefaultsTracker _tracker;

    public OrganizationDefaultsStatusReader(OnboardingDbContext db, SetupPayloadValidator validator, OrganizationDefaultsTracker tracker)
    {
        _db = db;
        _validator = validator;
        _tracker = tracker;
    }

    public async Task<OrganizationDefaultsStatus?> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        var setup = await _db.OrganizationSetups.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        var entry = _tracker.Get(organizationId);
        if (setup is null && entry is null) return null;

        var status = new OrganizationDefaultsStatus
        {
            OrganizationId = organizationId, SetupStatus = setup?.SetupStatus, CountryCode = setup?.CountryCode, IndustryType = setup?.IndustryType,
            StartedAt = entry?.StartedAt ?? entry?.QueuedAt, FinishedAt = entry?.FinishedAt
        };

        var since = entry?.QueuedAt.AddSeconds(-2);
        var logQuery = _db.TenantSetupLogs.AsNoTracking().Where(l => l.OrganizationId == organizationId);
        if (since is { } from) logQuery = logQuery.Where(l => l.AppliedAt >= from);
        var logs = await logQuery.OrderByDescending(l => l.AppliedAt).ThenByDescending(l => l.Id).ToListAsync(ct);
        var latest = logs.GroupBy(l => l.DataCategory, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var finished = entry?.Outcome?.Categories.ToDictionary(c => c.Category, StringComparer.OrdinalIgnoreCase);

        var country = setup?.CountryCode ?? SetupCodes.Global;
        var industry = setup?.IndustryType ?? SetupCodes.Global;
        foreach (var category in _validator.KnownCategories)
        {
            // The finished outcome has the skipped counts; while running (or after a restart) the per-category log row is the progress.
            if (finished is not null && finished.TryGetValue(category, out var done)) { status.Categories.Add(done); continue; }
            status.Categories.Add(latest.TryGetValue(category, out var log) ? FromLog(category, log, country, industry) : new OrganizationDefaultsCategoryResult
            {
                Category = category, Status = "Pending"
            });
        }

        var running = entry is { Status: OrganizationDefaultsStatuses.Running };
        if (entry is not null)
        {
            status.Status = entry.Status;
            status.Message = entry.Message ?? entry.Outcome?.Message ?? (running ? "Applying the default data in the background." : null);
        }
        else
        {
            // After a restart (or for an org nobody applied in this process): what the durable rows say.
            var failed = status.Categories.Any(c => c.Status == SetupCategoryOutcomes.Failed);
            status.Status = setup!.SetupStatus switch
            {
                SetupStatuses.Completed or SetupStatuses.Skipped when !failed => OrganizationDefaultsStatuses.Completed,
                SetupStatuses.NotStarted => OrganizationDefaultsStatuses.NotStarted,
                _ => OrganizationDefaultsStatuses.InProgress
            };
            if (status.Status == OrganizationDefaultsStatuses.InProgress)
                status.Message = "The default data was only partly applied; the organization admin can finish in the setup wizard.";
        }

        var levels = status.Categories.Select(c => c.PackLevel).ToHashSet();
        string[] order = [OrganizationPackLevels.Exact, OrganizationPackLevels.Country, OrganizationPackLevels.Industry, OrganizationPackLevels.Global];
        status.PackLevel = order.FirstOrDefault(levels.Contains) ?? OrganizationPackLevels.None;
        status.FallbackUsed = status.Categories.Any(c => c.PackLevel is not (OrganizationPackLevels.Exact or OrganizationPackLevels.None));
        return status;
    }

    private static OrganizationDefaultsCategoryResult FromLog(string category, TenantSetupLog log, string country, string industry)
    {
        var skippedByModule = log.Action == SetupLogActions.Skipped && log.Message?.EndsWith("is not enabled for the organization.", StringComparison.Ordinal) == true;
        var outcome = log.Action switch
        {
            SetupLogActions.Failed => SetupCategoryOutcomes.Failed,
            SetupLogActions.Skipped => skippedByModule ? SetupCategoryOutcomes.ModuleDisabled : SetupCategoryOutcomes.Skipped,
            _ => SetupCategoryOutcomes.Applied
        };
        var hit = log.ResolvedCountryCode is null || log.ResolvedIndustryType is null
            ? null
            : new ResolvedDefault(null!, log.ResolvedCountryCode, log.ResolvedIndustryType);
        return new OrganizationDefaultsCategoryResult
        {
            Category = category, Status = outcome, Ok = SetupCategoryOutcomes.IsOk(outcome), Created = log.RecordsSeeded, Message = log.Message,
            PackLevel = OrganizationDefaultsApplier.LevelOf(hit, country, industry), ResolvedCountry = log.ResolvedCountryCode, ResolvedIndustry = log.ResolvedIndustryType
        };
    }
}

/// <summary>One query for the setup columns the organization list and detail show.</summary>
internal sealed class OrganizationSetupSummaries : IOrganizationSetupSummaries
{
    private readonly OnboardingDbContext _db;

    public OrganizationSetupSummaries(OnboardingDbContext db) => _db = db;

    public async Task<Dictionary<Guid, OrganizationSetupSummary>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct = default)
    {
        if (organizationIds.Count == 0) return new();
        var ids = organizationIds.ToList();
        var rows = await _db.OrganizationSetups.AsNoTracking().Where(s => ids.Contains(s.OrganizationId))
            .Select(s => new { s.OrganizationId, s.CountryCode, s.IndustryType, s.SetupStatus }).ToListAsync(ct);
        return rows.ToDictionary(r => r.OrganizationId, r => new OrganizationSetupSummary(r.CountryCode, r.IndustryType, r.SetupStatus));
    }
}
