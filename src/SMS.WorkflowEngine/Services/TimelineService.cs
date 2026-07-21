using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

internal sealed class TimelineService : ITimelineService
{
    private const int MaxAppendAttempts = 5;

    private readonly WorkflowDbContext _db;
    private readonly IUserQueryService _userQuery;
    private readonly IReadOnlyDictionary<string, ITraceIdResolver> _resolvers;

    public TimelineService(WorkflowDbContext db, IUserQueryService userQuery, IEnumerable<ITraceIdResolver> resolvers)
    {
        _db = db;
        _userQuery = userQuery;
        _resolvers = resolvers.ToDictionary(
            r => r.InterfaceCode.ToUpperInvariant(),
            StringComparer.OrdinalIgnoreCase);
    }

    // Concurrency-safe read-modify-write: two simultaneous appends to the same trace_id — whether
    // both racing to create the row for a brand-new trace_id, or both racing to extend an existing
    // row's Events array — must both succeed with no lost update. Guarded by the RowVersion
    // concurrency token (existing row) and the unique index on TraceId (first insert); either
    // conflict surfaces as DbUpdateException, which we retry against a freshly re-read row.
    public async Task AppendEventAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType = null, string? chainRootRef = null)
    {
        for (var attempt = 1; attempt <= MaxAppendAttempts; attempt++)
        {
            try
            {
                var now      = DateTime.UtcNow;
                var existing = await _db.DocumentTimelines.FirstOrDefaultAsync(t => t.TraceId == traceId);

                if (existing is null)
                {
                    var events = new List<TimelineEvent> { newEvent };
                    _db.DocumentTimelines.Add(new DocumentTimeline
                    {
                        TraceId       = traceId,
                        Events        = JsonSerializer.Serialize(events),
                        ChainRootType = chainRootType,
                        ChainRootRef  = chainRootRef,
                        FirstEventAt  = now,
                        LastEventAt   = now,
                        CreatedAt     = now
                    });
                }
                else
                {
                    var events = JsonSerializer.Deserialize<List<TimelineEvent>>(existing.Events) ?? [];
                    events.Add(newEvent);
                    existing.Events      = JsonSerializer.Serialize(events);
                    existing.LastEventAt = now;
                    // FirstEventAt and ChainRootType/Ref are set once at creation and never revisited.
                }

                await _db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException) when (attempt < MaxAppendAttempts)
            {
                // Another writer committed first (RowVersion mismatch on update, or unique-index
                // conflict on insert). Drop tracked state and retry against the now-current row.
                _db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<TimelineEvent>> GetTimelineAsync(Guid traceId)
    {
        var timeline = await _db.DocumentTimelines.AsNoTracking().FirstOrDefaultAsync(t => t.TraceId == traceId);
        if (timeline is null) return [];

        var events = JsonSerializer.Deserialize<List<TimelineEvent>>(timeline.Events) ?? [];
        return events.OrderBy(e => e.OccurredAt).ToList();
    }

    public async Task<TimelineDetail?> GetTimelineDetailAsync(Guid traceId)
    {
        var timeline = await _db.DocumentTimelines.AsNoTracking().FirstOrDefaultAsync(t => t.TraceId == traceId);
        if (timeline is null) return null;

        var events = (JsonSerializer.Deserialize<List<TimelineEvent>>(timeline.Events) ?? [])
            .OrderBy(e => e.OccurredAt)
            .ToList();

        var userIds = events.Where(e => e.PerformedBy.HasValue).Select(e => e.PerformedBy!.Value).Distinct().ToList();
        var names   = userIds.Count > 0
            ? (await _userQuery.GetUsersAsync(userIds)).ToDictionary(u => u.UserId, u => u.DisplayName)
            : new Dictionary<int, string>();

        return new TimelineDetail
        {
            TraceId       = timeline.TraceId,
            ChainRootType = timeline.ChainRootType,
            ChainRootRef  = timeline.ChainRootRef,
            FirstEventAt  = timeline.FirstEventAt,
            LastEventAt   = timeline.LastEventAt,
            Events        = events.Select(e =>
            {
                var name = e.PerformedBy.HasValue && names.TryGetValue(e.PerformedBy.Value, out var n) ? n : null;
                return new TimelineEventView(
                    e.EventType, e.InterfaceCode, e.DocumentId, e.DocumentNumber, e.OccurredAt,
                    e.PerformedBy, name, ScrubRawUserId(e.Notes, e.PerformedBy, name));
            }).ToList()
        };
    }

    // Some older stored events (pre-dating name resolution) baked a raw "user {id}" reference
    // straight into Notes text. Scrub it here at read time — rather than backfilling every stored
    // JSON blob — since the actor is already shown separately once PerformedByName is resolved.
    private static string? ScrubRawUserId(string? notes, int? performedBy, string? performedByName)
    {
        if (notes is null || performedBy is null || performedByName is null) return notes;
        return notes.Replace($"user {performedBy}", performedByName);
    }

    public Task<Guid?> ResolveTraceIdAsync(string interfaceCode, Guid documentId)
    {
        return _resolvers.TryGetValue(interfaceCode.ToUpperInvariant(), out var resolver)
            ? resolver.ResolveTraceIdAsync(documentId)
            : Task.FromResult<Guid?>(null);
    }
}
