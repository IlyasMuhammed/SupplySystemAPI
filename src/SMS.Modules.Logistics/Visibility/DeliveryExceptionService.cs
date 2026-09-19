using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Visibility;

/// <summary>
/// What has gone wrong with a movement, named and owned until it is settled.
/// <para>
/// <b>This is the work, not the news.</b> A carrier scan saying "customs hold" is news; an
/// exception is the piece of work that news creates. Keeping them apart is what lets the carrier's
/// exact words survive alongside a resolution it never gave.
/// </para>
/// </summary>
public interface IDeliveryExceptionService
{
    Task<Guid?> RaiseAsync(
        Guid consignmentUuid, RaiseExceptionRequest req, int userId, CancellationToken ct = default);

    Task<DeliveryExceptionModel?> GetAsync(Guid uuid, CancellationToken ct = default);

    Task<PaginatedResponse<DeliveryExceptionModel>> GetQueueAsync(
        ExceptionFilter filter, CancellationToken ct = default);

    Task<ExceptionSummaryModel> GetSummaryAsync(CancellationToken ct = default);

    Task<bool> PatchAsync(Guid uuid, PatchExceptionRequest req, int userId, CancellationToken ct = default);

    Task<bool> ResolveAsync(
        Guid uuid, ResolveExceptionRequest req, int userId, CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid uuid, WithdrawExceptionRequest req, int userId, CancellationToken ct = default);

    /// <summary>
    /// Raises exceptions for carrier events that plainly say what went wrong. What a job runs.
    /// </summary>
    Task<int> SweepFromTrackingAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Raises an exception for each consignment T-40's stuck detection has flagged and nobody owns.
    /// The other half of the same job.
    /// </summary>
    Task<int> SweepStuckAsync(int userId, CancellationToken ct = default);
}

internal sealed class DeliveryExceptionService : IDeliveryExceptionService
{
    private static readonly string Open      = LogisticsCode.Of(ExceptionStatus.Open);
    private static readonly string Waiting   = LogisticsCode.Of(ExceptionStatus.Waiting);
    private static readonly string Resolved  = LogisticsCode.Of(ExceptionStatus.Resolved);
    private static readonly string Withdrawn = LogisticsCode.Of(ExceptionStatus.Withdrawn);

    /// <summary>
    /// The carrier milestones that say plainly what went wrong.
    /// <para>
    /// <b>The generic <c>EXCEPTION</c> milestone is deliberately absent.</b> It means only that
    /// something is wrong, and picking a type for it would be a guess dressed as a record — the
    /// carrier's own status text is a hint, not a fact. Those consignments are reported as needing
    /// somebody instead, which is what the summary's <c>Warnings</c> are for.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, DeliveryExceptionType> FromMilestone = new()
    {
        [LogisticsCode.Of(TrackingMilestone.CustomsHold)]        = DeliveryExceptionType.CustomsHold,
        [LogisticsCode.Of(TrackingMilestone.DeliveryAttempted)]  = DeliveryExceptionType.ConsigneeUnreachable,
        [LogisticsCode.Of(TrackingMilestone.ReturnInitiated)]    = DeliveryExceptionType.Refused
    };

    private readonly LogisticsDbContext _db;
    public DeliveryExceptionService(LogisticsDbContext db) => _db = db;

    // ── Raising ───────────────────────────────────────────────────────────────

    public async Task<Guid?> RaiseAsync(
        Guid consignmentUuid, RaiseExceptionRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        if (!LogisticsCode.TryParse<DeliveryExceptionType>(req.ExceptionType, out var type))
            throw new BadRequestException(
                $"'{req.ExceptionType}' is not an exception type. Valid values: "
              + $"{string.Join(", ", LogisticsCode.Codes<DeliveryExceptionType>())}.");

        var severity = Severity(req.Severity);

        var description = Trim(req.Description)
            ?? throw new BadRequestException(
                "Say what has gone wrong. An exception nobody can restate is one nobody can act on.");

        var now = DateTime.UtcNow;

        var exception = new DeliveryException
        {
            UUID             = Guid.NewGuid(),
            OrganizationId   = consignment.OrganizationId,
            ConsignmentId    = consignment.Id,
            CarrierId        = consignment.CarrierId,
            CarrierName      = consignment.CarrierName ?? consignment.Carrier?.Name,
            ExceptionType    = LogisticsCode.Of(type),
            Severity         = LogisticsCode.Of(severity),
            Status           = Open,
            Source           = LogisticsCode.Of(ExceptionSource.Manual),
            Description      = description,
            OccurredAt       = req.OccurredAt ?? now,
            AssignedToUserId = req.AssignToUserId,
            AssignedAt       = req.AssignToUserId is null ? null : now,
            CreatedBy        = userId,
            CreatedDate      = now
        };

        _db.DeliveryExceptions.Add(exception);
        await _db.SaveChangesAsync(ct);

        return exception.UUID;
    }

    // ── From the carrier's own events ─────────────────────────────────────────

    public async Task<int> SweepFromTrackingAsync(int userId, CancellationToken ct = default)
    {
        var milestones = FromMilestone.Keys.ToList();

        // Already raised from an event. The unique index guards the race; this keeps the sweep
        // from doing pointless work every ten minutes.
        var seen = await _db.DeliveryExceptions.AsNoTracking()
            .Where(e => !e.IsDelete && e.TrackingEventId != null)
            .Select(e => e.TrackingEventId!.Value)
            .ToListAsync(ct);

        var events = await _db.ConsignmentTrackingEvents.AsNoTracking()
            .Include(e => e.Consignment).ThenInclude(c => c.Carrier)
            .Where(e => milestones.Contains(e.Milestone) && !seen.Contains(e.Id))
            .ToListAsync(ct);

        if (events.Count == 0) return 0;

        var now = DateTime.UtcNow;

        foreach (var evt in events)
        {
            var type = FromMilestone[evt.Milestone];

            _db.DeliveryExceptions.Add(new DeliveryException
            {
                UUID            = Guid.NewGuid(),
                OrganizationId  = evt.Consignment.OrganizationId,
                ConsignmentId   = evt.ConsignmentId,
                CarrierId       = evt.Consignment.CarrierId,
                CarrierName     = evt.Consignment.CarrierName ?? evt.Consignment.Carrier?.Name,
                ExceptionType   = LogisticsCode.Of(type),
                // A customs hold stops the goods and a failed attempt does not, so they do not
                // arrive equal.
                Severity        = LogisticsCode.Of(
                    type == DeliveryExceptionType.CustomsHold
                        ? ExceptionSeverity.Critical
                        : ExceptionSeverity.Normal),
                Status          = Open,
                Source          = LogisticsCode.Of(ExceptionSource.Carrier),
                // The carrier's own words, kept verbatim. A paraphrase is what gets argued with.
                Description     = Clip(evt.Description ?? evt.CarrierStatus ?? evt.Milestone, 1000),
                TrackingEventId = evt.Id,
                OccurredAt      = evt.OccurredAt,
                CreatedBy       = userId,
                CreatedDate     = now
            });
        }

        await _db.SaveChangesAsync(ct);

        return events.Count;
    }

    // ── What has gone quiet ───────────────────────────────────────────────────

    /// <summary>
    /// A consignment T-40 flagged as stuck is a problem nobody has been given. Until this existed,
    /// <c>StuckSince</c> was a column two screens could show and nothing could act on.
    /// </summary>
    public async Task<int> SweepStuckAsync(int userId, CancellationToken ct = default)
    {
        var system = LogisticsCode.Of(ExceptionSource.System);

        var stuck = await _db.Consignments.AsNoTracking()
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete && c.StuckSince != null)
            .ToListAsync(ct);

        if (stuck.Count == 0) return 0;

        var ids = stuck.Select(c => c.Id).ToList();

        // One exception per stuck episode, live or closed. Keyed on the moment it went quiet
        // rather than on the consignment, because StuckSince is cleared the instant it moves
        // again (T-40: "stuck is a state, not an event") — so a second silence is a second
        // problem, and resolving the first does not make this raise it again forever.
        var already = await _db.DeliveryExceptions.AsNoTracking()
            .Where(e => !e.IsDelete && e.Source == system && ids.Contains(e.ConsignmentId))
            .Select(e => new { e.ConsignmentId, e.OccurredAt })
            .ToListAsync(ct);

        var seen = already.Select(a => (a.ConsignmentId, a.OccurredAt)).ToHashSet();

        var now = DateTime.UtcNow;
        var raised = 0;

        foreach (var c in stuck.Where(c => !seen.Contains((c.Id, c.StuckSince!.Value))))
        {
            _db.DeliveryExceptions.Add(new DeliveryException
            {
                UUID           = Guid.NewGuid(),
                OrganizationId = c.OrganizationId,
                ConsignmentId  = c.Id,
                CarrierId      = c.CarrierId,
                CarrierName    = c.CarrierName ?? c.Carrier?.Name,
                // DELAYED, not one of the seven causes that say what happened. Nothing has been
                // reported — that is the whole complaint, and naming a cause would invent one.
                ExceptionType  = LogisticsCode.Of(DeliveryExceptionType.Delayed),
                Severity       = LogisticsCode.Of(ExceptionSeverity.Normal),
                Status         = Open,
                Source         = system,
                Description    = Clip(c.StuckReason ?? "No carrier news for longer than expected.", 1000),
                OccurredAt     = c.StuckSince!.Value,
                CreatedBy      = userId,
                CreatedDate    = now
            });

            raised++;
        }

        if (raised > 0) await _db.SaveChangesAsync(ct);

        return raised;
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<DeliveryExceptionModel?> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        var exception = await Query().FirstOrDefaultAsync(e => e.UUID == uuid && !e.IsDelete, ct);

        return exception is null ? null : ToModel(exception);
    }

    public async Task<PaginatedResponse<DeliveryExceptionModel>> GetQueueAsync(
        ExceptionFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var q = Query().Where(e => !e.IsDelete);

        // The default is everything still needing work. A queue defaulting to everything is a
        // queue nobody works.
        q = string.IsNullOrWhiteSpace(filter.Status)
            ? q.Where(e => e.Status == Open || e.Status == Waiting)
            : q.Where(e => e.Status == filter.Status);

        if (filter.CarrierUuid     is { } carrier)     q = q.Where(e => e.Carrier!.UUID == carrier);
        if (filter.ConsignmentUuid is { } consignment) q = q.Where(e => e.Consignment.UUID == consignment);

        if (!string.IsNullOrWhiteSpace(filter.ExceptionType))
            q = q.Where(e => e.ExceptionType == filter.ExceptionType);

        if (!string.IsNullOrWhiteSpace(filter.Severity))
            q = q.Where(e => e.Severity == filter.Severity);

        if (filter.Unassigned is true)  q = q.Where(e => e.AssignedToUserId == null);
        if (filter.Unassigned is false) q = q.Where(e => e.AssignedToUserId != null);

        var page     = filter.Page     < 1 ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var total = await q.CountAsync(ct);

        var rows = await q.ToListAsync(ct);

        // Critical first, then oldest. Sorted in memory because severity is a code rather than a
        // rank, and translating it in SQL would mean a CASE nobody could read.
        var ordered = rows
            .OrderBy(e => Rank(e.Severity))
            .ThenBy(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PaginatedResponse<DeliveryExceptionModel>
        {
            Data         = [.. ordered.Select(ToModel)],
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<ExceptionSummaryModel> GetSummaryAsync(CancellationToken ct = default)
    {
        var live = await _db.DeliveryExceptions.AsNoTracking()
            .Where(e => !e.IsDelete && (e.Status == Open || e.Status == Waiting))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        var summary = new ExceptionSummaryModel
        {
            Open       = live.Count(e => e.Status == Open),
            Waiting    = live.Count(e => e.Status == Waiting),
            Critical   = live.Count(e => e.Severity == LogisticsCode.Of(ExceptionSeverity.Critical)),
            Unassigned = live.Count(e => e.AssignedToUserId is null),
            ByType =
            [
                .. live.GroupBy(e => e.ExceptionType)
                       .Select(g => new ExceptionTypeCountModel
                       {
                           ExceptionType = g.Key,
                           Open          = g.Count(),
                           Critical      = g.Count(e => e.Severity == LogisticsCode.Of(ExceptionSeverity.Critical)),
                           Unassigned    = g.Count(e => e.AssignedToUserId is null),
                           OldestHours   = Math.Round((now - g.Min(e => e.OccurredAt)).TotalHours, 1)
                       })
                       .OrderByDescending(t => t.Critical)
                       .ThenByDescending(t => t.Open)
            ]
        };

        if (summary.Unassigned > 0)
            summary.Warnings.Add(
                $"{summary.Unassigned} exception(s) belong to nobody. Those are the ones that sit "
              + "untouched for a week.");

        await AddUnrecordedAsync(summary, ct);

        return summary;
    }

    /// <summary>
    /// Consignments the carrier has put in EXCEPTION with nothing recorded against them.
    /// <para>
    /// The generic milestone says only that something is wrong, so nothing auto-raises from it — a
    /// guessed type is worse than none. Reporting the count puts a person on it instead.
    /// </para>
    /// </summary>
    private async Task AddUnrecordedAsync(ExceptionSummaryModel summary, CancellationToken ct)
    {
        var withException = await _db.DeliveryExceptions.AsNoTracking()
            .Where(e => !e.IsDelete && (e.Status == Open || e.Status == Waiting))
            .Select(e => e.ConsignmentId)
            .ToListAsync(ct);

        var unrecorded = await _db.Consignments.AsNoTracking()
            .CountAsync(c => !c.IsDelete
                          && c.Status == LogisticsCode.Of(ShipmentStatus.Exception)
                          && !withException.Contains(c.Id), ct);

        if (unrecorded > 0)
            summary.Warnings.Add(
                $"{unrecorded} consignment(s) are in exception with nothing recorded against them. "
              + "The carrier said something was wrong without saying what, so somebody has to name it.");
    }

    // ── Working one ───────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(
        Guid uuid, PatchExceptionRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var exception = await _db.DeliveryExceptions
            .FirstOrDefaultAsync(e => e.UUID == uuid && !e.IsDelete, ct);

        if (exception is null) return false;

        EnsureLive(exception);

        if (req.Severity is not null)
            exception.Severity = LogisticsCode.Of(Severity(req.Severity));

        if (req.Description is not null)
            exception.Description = Trim(req.Description)
                ?? throw new BadRequestException("A description cannot be blank.");

        if (req.Status is not null)
        {
            if (req.Status != Open && req.Status != Waiting)
                throw new BadRequestException(
                    $"'{req.Status}' cannot be set here. Use {Open} or {Waiting} — resolving and "
                  + "withdrawing each need their own reason, so they have their own operations.");

            exception.Status = req.Status;
        }

        var now = DateTime.UtcNow;

        if (req.ClearAssignee)
        {
            exception.AssignedToUserId = null;
            exception.AssignedAt       = null;
        }
        else if (req.AssignToUserId is { } assignee)
        {
            exception.AssignedToUserId = assignee;
            exception.AssignedAt       = now;
        }

        exception.ModifiedBy   = userId;
        exception.ModifiedDate = now;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ResolveAsync(
        Guid uuid, ResolveExceptionRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var exception = await _db.DeliveryExceptions
            .FirstOrDefaultAsync(e => e.UUID == uuid && !e.IsDelete, ct);

        if (exception is null) return false;

        EnsureLive(exception);

        var resolution = Trim(req.Resolution)
            ?? throw new BadRequestException(
                "Say how it was settled. An exception that closes without a reason teaches nobody "
              + "anything, and the next one will be worked from scratch.");

        Close(exception, Resolved, resolution, userId);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> WithdrawAsync(
        Guid uuid, WithdrawExceptionRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var exception = await _db.DeliveryExceptions
            .FirstOrDefaultAsync(e => e.UUID == uuid && !e.IsDelete, ct);

        if (exception is null) return false;

        EnsureLive(exception);

        var reason = Trim(req.Reason)
            ?? throw new BadRequestException("Say why this should not have been raised.");

        // Distinct from resolved on purpose: counting a mistaken exception as one that was fixed
        // flatters every figure a scorecard produces.
        Close(exception, Withdrawn, reason, userId);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void Close(DeliveryException exception, string status, string note, int userId)
    {
        var now = DateTime.UtcNow;

        exception.Status       = status;
        exception.Resolution   = note;
        exception.ResolvedAt   = now;
        exception.ResolvedBy   = userId;
        exception.ModifiedBy   = userId;
        exception.ModifiedDate = now;
    }

    private static void EnsureLive(DeliveryException exception)
    {
        if (exception.Status == Resolved || exception.Status == Withdrawn)
            throw new ConflictException(
                $"This exception is {exception.Status}. Raise a new one rather than reopening it — "
              + "what happened twice is two things, and a scorecard counting one would be wrong.");
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private IQueryable<DeliveryException> Query() =>
        _db.DeliveryExceptions.AsNoTracking()
            .Include(e => e.Consignment)
            .Include(e => e.Carrier)
            .Include(e => e.TrackingEvent);

    private static int Rank(string severity) =>
        severity == LogisticsCode.Of(ExceptionSeverity.Critical) ? 0
      : severity == LogisticsCode.Of(ExceptionSeverity.Normal)   ? 1
      : 2;

    private static ExceptionSeverity Severity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ExceptionSeverity.Normal;

        if (!LogisticsCode.TryParse<ExceptionSeverity>(value.Trim(), out var severity))
            throw new BadRequestException(
                $"'{value}' is not a severity. Valid values: "
              + $"{string.Join(", ", LogisticsCode.Codes<ExceptionSeverity>())}.");

        return severity;
    }

    private static DeliveryExceptionModel ToModel(DeliveryException e)
    {
        var now = DateTime.UtcNow;
        var ended = e.ResolvedAt ?? now;

        var model = new DeliveryExceptionModel
        {
            UUID              = e.UUID,
            ConsignmentUuid   = e.Consignment.UUID,
            ConsignmentNumber = e.Consignment.ConsignmentNumber,
            ConsignmentStatus = e.Consignment.Status,
            MasterAwb         = e.Consignment.MasterAwb,
            CarrierUuid       = e.Carrier?.UUID,
            CarrierName       = e.CarrierName ?? e.Carrier?.Name,
            ExceptionType     = e.ExceptionType,
            Severity          = e.Severity,
            Status            = e.Status,
            Source            = e.Source,
            Description       = e.Description,
            OccurredAt        = e.OccurredAt,
            OpenForHours      = Math.Round((ended - e.OccurredAt).TotalHours, 1),
            AssignedToUserId  = e.AssignedToUserId,
            AssignedAt        = e.AssignedAt,
            ResolvedAt        = e.ResolvedAt,
            Resolution        = e.Resolution,
            CarrierStatus     = e.TrackingEvent?.CarrierStatus
        };

        if (e.AssignedToUserId is null && (e.Status == Open || e.Status == Waiting))
            model.Warnings.Add("Nobody owns this.");

        // A resolved exception on a consignment the carrier still says is in exception has been
        // closed on our side and not on theirs.
        if (e.Status == Resolved
         && e.Consignment.Status == LogisticsCode.Of(ShipmentStatus.Exception))
            model.Warnings.Add(
                "This was settled here, and the consignment is still in exception with the carrier.");

        return model;
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
