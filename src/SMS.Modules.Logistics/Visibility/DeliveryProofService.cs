using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Visibility;

/// <summary>
/// Evidence that goods reached somebody — who took them, when, and the artefacts that show it.
/// <para>
/// <b>Stored, not linked.</b> The only proof of delivery this system had was a 500-character URL on
/// the legacy shipment table pointing somewhere nobody controls (finding F47). A link rots, and a
/// proof that has rotted is worth less than no proof at all, because it was relied on.
/// </para>
/// </summary>
public interface IDeliveryProofService
{
    Task<Guid?> RecordAsync(
        Guid consignmentUuid, RecordProofRequest req, int userId, CancellationToken ct = default);

    Task<DeliveryProofModel?> GetAsync(Guid uuid, CancellationToken ct = default);

    Task<IReadOnlyList<DeliveryProofModel>> GetForConsignmentAsync(
        Guid consignmentUuid, CancellationToken ct = default);

    Task<bool> PatchAsync(Guid uuid, PatchProofRequest req, int userId, CancellationToken ct = default);

    Task<Guid?> AttachFileAsync(
        Guid proofUuid, string kind, byte[] content, string contentType, int userId,
        CancellationToken ct = default);

    Task<DeliveryProofFileContent?> GetFileAsync(Guid fileUuid, CancellationToken ct = default);

    Task<bool> RemoveFileAsync(Guid fileUuid, int userId, CancellationToken ct = default);

    /// <summary>What is delivered without evidence. The question an auditor asks first.</summary>
    Task<ProofCoverageModel> GetCoverageAsync(CancellationToken ct = default);

    /// <summary>Records a proof for each DELIVERED scan that has none. What a job runs.</summary>
    Task<int> SweepFromTrackingAsync(int userId, CancellationToken ct = default);
}

internal sealed class DeliveryProofService : IDeliveryProofService
{
    /// <summary>
    /// A signature is a few kilobytes and a doorstep photograph a couple of megabytes. Ten is
    /// generous; past it, somebody is uploading a video.
    /// </summary>
    internal const int MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>The most gaps the coverage report will list. A list nobody can read is not a report.</summary>
    internal const int MaxGapsListed = 50;

    /// <summary>
    /// Media types an artefact may be stored and served as, with their extension.
    /// <para>
    /// Deliberately excludes anything a browser renders as a page: these files are served back from
    /// our own origin, and an uploaded "signature" that is really HTML would be script running in
    /// the next viewer's session. The same reasoning as the label store, for the same reason.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> AllowedTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"]       = "png",
            ["image/jpeg"]      = "jpg",
            ["image/gif"]       = "gif",
            ["application/pdf"] = "pdf"
        };

    private static readonly string Delivered = LogisticsCode.Of(ShipmentStatus.Delivered);
    private static readonly string Manual    = LogisticsCode.Of(ProofSource.Manual);
    private static readonly string Carrier   = LogisticsCode.Of(ProofSource.Carrier);

    private readonly LogisticsDbContext        _db;
    private readonly IDeliveryProgressService? _progress;

    public DeliveryProofService(LogisticsDbContext db, IDeliveryProgressService? progress = null)
    {
        _db       = db;
        _progress = progress;
    }

    // ── Recording one by hand ─────────────────────────────────────────────────

    public async Task<Guid?> RecordAsync(
        Guid consignmentUuid, RecordProofRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        var receivedBy = Trim(req.ReceivedBy)
            ?? throw new BadRequestException(
                "Say who took the goods. A proof naming nobody is a proof of nothing, and whoever "
              + "is keying this in has the docket in front of them.");

        var now = DateTime.UtcNow;
        var deliveredAt = req.DeliveredAt ?? now;

        if (deliveredAt > now.AddMinutes(1))
            throw new BadRequestException("A delivery cannot have happened in the future.");

        if (consignment.ActualDispatchAt is { } dispatched && deliveredAt < dispatched)
            throw new BadRequestException(
                $"This says the goods arrived at {deliveredAt:u}, before they left at {dispatched:u}.");

        var stop = await ResolveStopAsync(consignment, req.ConsignmentStopUuid, ct);

        await EnsureNoProofYetAsync(consignment, stop, ct);

        var proof = new DeliveryProof
        {
            UUID              = Guid.NewGuid(),
            OrganizationId    = consignment.OrganizationId,
            ConsignmentId     = consignment.Id,
            ConsignmentStopId = stop?.Id,
            ReceivedBy        = receivedBy,
            Relationship      = Trim(req.Relationship),
            DeliveredAt       = deliveredAt,
            Location          = Trim(req.Location),
            Notes             = Trim(req.Notes),
            Source            = Manual,
            CreatedBy         = userId,
            CreatedDate       = now
        };

        _db.DeliveryProofs.Add(proof);

        MarkDelivered(consignment, deliveredAt);

        if (stop is not null) stop.ActualArrival ??= deliveredAt;

        await _db.SaveChangesAsync(ct);

        // The person who typed this in is waiting to see the delivery become DELIVERED, so it is not
        // left to the sweep. Once the proof is saved this cannot fail the call — see the interface.
        if (_progress is not null)
            await _progress.SyncAsync(consignmentUuid, userId, ct);

        return proof.UUID;
    }

    /// <summary>
    /// Moves the consignment to DELIVERED where a legal route to it exists.
    /// <para>
    /// This is the only thing that ever will for a manual carrier — nothing polls a van driver. That
    /// carrier is booked and then silent, so the consignment is BOOKED when the handover is typed in,
    /// and the machine has no edge from there to DELIVERED. The route is walked exactly as it is for
    /// an API carrier's scan that skips ahead (<see cref="Couriers.Tracking.TrackingEventRecorder"/>):
    /// the shortest legal path through the carrier-driven statuses must exist. The steps in between
    /// are not stored as statuses — nobody reported them.
    /// </para>
    /// <para>
    /// For an API carrier the scan usually gets there first, in which case the status is already
    /// DELIVERED and nothing happens: a self-transition is illegal by design, and re-stamping the
    /// arrival time would overwrite the carrier's account with ours.
    /// </para>
    /// </summary>
    private static void MarkDelivered(Consignment consignment, DateTime deliveredAt)
    {
        if (!LogisticsCode.TryParse<ShipmentStatus>(consignment.Status, out var current)) return;

        if (current == ShipmentStatus.Delivered)
        {
            consignment.ActualArrivalAt ??= deliveredAt;
            return;
        }

        // Not yet booked, or already cancelled, returned or lost: there is no route, and a proof
        // against a movement that never happened is worse than none.
        if (Couriers.Tracking.TrackingEventRecorder.PathBetween(current, ShipmentStatus.Delivered) is null)
            throw new ConflictException(
                $"A consignment that is {consignment.Status} cannot be recorded as delivered. "
              + "Book it first, or cancel or resolve it — a proof against a movement that never "
              + "happened is worse than none.");

        consignment.Status          = Delivered;
        consignment.ActualArrivalAt = deliveredAt;
    }

    private async Task<ConsignmentStop?> ResolveStopAsync(
        Consignment consignment, Guid? stopUuid, CancellationToken ct)
    {
        if (stopUuid is null) return null;

        var stop = await _db.ConsignmentStops
            .FirstOrDefaultAsync(s => s.UUID == stopUuid, ct);

        if (stop is null || stop.ConsignmentId != consignment.Id)
            throw new BadRequestException(
                "That stop is not on this consignment. A proof filed against the wrong drop proves "
              + "the wrong delivery.");

        return stop;
    }

    private async Task EnsureNoProofYetAsync(
        Consignment consignment, ConsignmentStop? stop, CancellationToken ct)
    {
        var stopId = stop?.Id;

        var exists = await _db.DeliveryProofs.AsNoTracking()
            .AnyAsync(p => !p.IsDelete
                        && p.ConsignmentId == consignment.Id
                        && p.ConsignmentStopId == stopId, ct);

        if (exists)
            throw new ConflictException(
                stop is null
                    ? "This consignment already has a proof of delivery. Amend it or attach another "
                    + "artefact to it — a second proof is a second account of one handover."
                    : $"Stop {stop.Sequence} already has a proof of delivery.");
    }

    // ── Recording what the carrier reported ───────────────────────────────────

    public async Task<int> SweepFromTrackingAsync(int userId, CancellationToken ct = default)
    {
        var delivered = LogisticsCode.Of(TrackingMilestone.Delivered);

        var seenEvents = await _db.DeliveryProofs.AsNoTracking()
            .Where(p => !p.IsDelete && p.TrackingEventId != null)
            .Select(p => p.TrackingEventId!.Value)
            .ToListAsync(ct);

        // A proof already recorded by hand wins. The person who typed it was there; the scan was
        // not, and an extra row would break the one-proof-per-drop rule for no gain.
        var covered = await _db.DeliveryProofs.AsNoTracking()
            .Where(p => !p.IsDelete && p.ConsignmentStopId == null)
            .Select(p => p.ConsignmentId)
            .ToListAsync(ct);

        var events = await _db.ConsignmentTrackingEvents.AsNoTracking()
            .Include(e => e.Consignment)
            .Where(e => e.Milestone == delivered
                     && !seenEvents.Contains(e.Id)
                     && !covered.Contains(e.ConsignmentId))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);

        if (events.Count == 0) return 0;

        var now = DateTime.UtcNow;
        var taken = new HashSet<int>();
        var recorded = 0;

        foreach (var evt in events)
        {
            // Two DELIVERED scans for one consignment — carriers do send them. The first is the
            // handover; the rest are restatements of it.
            if (!taken.Add(evt.ConsignmentId)) continue;

            _db.DeliveryProofs.Add(new DeliveryProof
            {
                UUID            = Guid.NewGuid(),
                OrganizationId  = evt.Consignment.OrganizationId,
                ConsignmentId   = evt.ConsignmentId,
                // Whatever the carrier said, including nothing. Storing a blank name is honest;
                // inventing one to satisfy a required field would not be.
                ReceivedBy      = Trim(evt.SignedBy),
                DeliveredAt     = evt.OccurredAt,
                Location        = Trim(evt.Location),
                Source          = Carrier,
                TrackingEventId = evt.Id,
                CreatedBy       = userId,
                CreatedDate     = now
            });

            recorded++;
        }

        await _db.SaveChangesAsync(ct);

        return recorded;
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<DeliveryProofModel?> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        var proof = await Query().FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete, ct);

        return proof is null ? null : ToModel(proof);
    }

    public async Task<IReadOnlyList<DeliveryProofModel>> GetForConsignmentAsync(
        Guid consignmentUuid, CancellationToken ct = default)
    {
        var proofs = await Query()
            .Where(p => !p.IsDelete && p.Consignment.UUID == consignmentUuid)
            .OrderBy(p => p.DeliveredAt)
            .ToListAsync(ct);

        return [.. proofs.Select(ToModel)];
    }

    public async Task<ProofCoverageModel> GetCoverageAsync(CancellationToken ct = default)
    {
        var delivered = await _db.Consignments.AsNoTracking()
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete && c.Status == Delivered)
            .ToListAsync(ct);

        var ids = delivered.Select(c => c.Id).ToList();

        var proofs = await _db.DeliveryProofs.AsNoTracking()
            .Include(p => p.Files.Where(f => !f.IsDelete))
            .Where(p => !p.IsDelete && ids.Contains(p.ConsignmentId))
            .ToListAsync(ct);

        var byConsignment = proofs
            .GroupBy(p => p.ConsignmentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var coverage = new ProofCoverageModel { Delivered = delivered.Count };
        var gaps = new List<ProofGapModel>();

        foreach (var c in delivered)
        {
            if (!byConsignment.TryGetValue(c.Id, out var forThis) || forThis.Count == 0)
            {
                coverage.WithoutProof++;
                gaps.Add(Gap(c, "Delivered with no proof of any kind."));
                continue;
            }

            coverage.WithProof++;

            // Every proof on the consignment has to stand up, not just one of them: a multi-stop run
            // where two drops are evidenced and the third is not is disputed on the third.
            var weakest = forThis.FirstOrDefault(p => WhyWeak(p) is not null);

            if (weakest is null) { coverage.Defensible++; continue; }

            coverage.Weak++;
            gaps.Add(Gap(c, WhyWeak(weakest)!));
        }

        coverage.Gaps = [.. gaps.OrderBy(g => g.DeliveredAt ?? DateTime.MaxValue).Take(MaxGapsListed)];

        if (coverage.WithoutProof > 0)
            coverage.Warnings.Add(
                $"{coverage.WithoutProof} delivered consignment(s) have no proof at all. Every one of "
              + "them is a delivery that cannot be demonstrated if it is denied.");

        if (coverage.Weak > 0)
            coverage.Warnings.Add(
                $"{coverage.Weak} have something recorded that would not stand up — no name, or "
              + "nothing anybody can produce.");

        if (gaps.Count > MaxGapsListed)
            coverage.Warnings.Add(
                $"Showing the {MaxGapsListed} oldest of {gaps.Count} gaps.");

        return coverage;
    }

    private static ProofGapModel Gap(Consignment c, string gap) => new()
    {
        ConsignmentUuid   = c.UUID,
        ConsignmentNumber = c.ConsignmentNumber,
        CarrierName       = c.CarrierName ?? c.Carrier?.Name,
        MasterAwb         = c.MasterAwb,
        DeliveredAt       = c.ActualArrivalAt,
        Gap               = gap
    };

    /// <summary>Why this proof would not stand up, or null when it would.</summary>
    private static string? WhyWeak(DeliveryProof proof)
    {
        var hasName  = !string.IsNullOrWhiteSpace(proof.ReceivedBy);
        var hasFile  = proof.Files.Any(f => !f.IsDelete);

        return (hasName, hasFile) switch
        {
            (false, false) => "The carrier reported a delivery and named nobody, and there is nothing on file.",
            (false, true)  => "Nobody is named as having taken the goods.",
            (true,  false) => "A name, but no signature, photograph or document on file.",
            _              => null
        };
    }

    // ── Amending ──────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(
        Guid uuid, PatchProofRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var proof = await _db.DeliveryProofs.FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete, ct);

        if (proof is null) return false;

        // Amending who signed and where is how a carrier proof naming nobody becomes usable, once
        // somebody has telephoned and found out. The time it happened is not amendable here: that is
        // the carrier's account of the event, and changing it silently would rewrite the record a
        // dispute turns on.
        if (req.ReceivedBy is not null)
            proof.ReceivedBy = Trim(req.ReceivedBy)
                ?? throw new BadRequestException("A name cannot be blanked out once it is recorded.");

        if (req.Relationship is not null) proof.Relationship = Trim(req.Relationship);
        if (req.Location     is not null) proof.Location     = Trim(req.Location);
        if (req.Notes        is not null) proof.Notes        = Trim(req.Notes);

        proof.ModifiedBy   = userId;
        proof.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── The artefacts ─────────────────────────────────────────────────────────

    public async Task<Guid?> AttachFileAsync(
        Guid proofUuid, string kind, byte[] content, string contentType, int userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var proof = await _db.DeliveryProofs
            .Include(p => p.Consignment)
            .FirstOrDefaultAsync(p => p.UUID == proofUuid && !p.IsDelete, ct);

        if (proof is null) return null;

        if (!LogisticsCode.TryParse<ProofFileKind>(kind, out var parsedKind))
            throw new BadRequestException(
                $"'{kind}' is not a kind of proof. Valid values: "
              + $"{string.Join(", ", LogisticsCode.Codes<ProofFileKind>())}.");

        if (content.Length == 0)
            throw new BadRequestException("The file is empty.");

        if (content.Length > MaxFileBytes)
            throw new BadRequestException(
                $"That is {content.Length / 1024 / 1024} MB. A signature is kilobytes and a doorstep "
              + $"photograph a couple of megabytes; the limit is {MaxFileBytes / 1024 / 1024} MB.");

        var (normalised, extension) = Validate(contentType, content);

        var sha = Convert.ToHexString(SHA256.HashData(content));

        var already = await _db.DeliveryProofs.AsNoTracking()
            .Where(p => p.Id == proof.Id)
            .SelectMany(p => p.Files)
            .FirstOrDefaultAsync(f => !f.IsDelete && f.Sha256 == sha, ct);

        // Phones retry uploads on a bad signal. The same bytes twice is one artefact.
        if (already is not null) return already.UUID;

        var file = new DeliveryProofFile
        {
            UUID            = Guid.NewGuid(),
            OrganizationId  = proof.OrganizationId,
            DeliveryProofId = proof.Id,
            Kind            = LogisticsCode.Of(parsedKind),
            ContentType     = normalised,
            FileName        = FileNameFor(proof.Consignment.ConsignmentNumber, parsedKind, sha, extension),
            Content         = content,
            SizeBytes       = content.Length,
            Sha256          = sha,
            CreatedBy       = userId,
            CreatedDate     = DateTime.UtcNow
        };

        _db.DeliveryProofFiles.Add(file);
        await _db.SaveChangesAsync(ct);

        return file.UUID;
    }

    public async Task<DeliveryProofFileContent?> GetFileAsync(Guid fileUuid, CancellationToken ct = default)
    {
        var file = await _db.DeliveryProofFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.UUID == fileUuid && !f.IsDelete, ct);

        return file is null
            ? null
            : new DeliveryProofFileContent(file.Content, file.ContentType, file.FileName);
    }

    public async Task<bool> RemoveFileAsync(Guid fileUuid, int userId, CancellationToken ct = default)
    {
        var file = await _db.DeliveryProofFiles.FirstOrDefaultAsync(f => f.UUID == fileUuid && !f.IsDelete, ct);

        if (file is null) return false;

        // Soft, always. Evidence removed by mistake and gone for good is the one deletion nobody can
        // undo, and the coverage report is what makes its absence visible.
        file.IsDelete = true;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises the claimed type and checks the bytes actually are it. An upload labelled
    /// <c>image/png</c> that is really HTML would be script running in the next viewer's session,
    /// because these files are served back from our own origin.
    /// </summary>
    internal static (string ContentType, string Extension) Validate(string? claimed, byte[] content)
    {
        var type = (claimed ?? string.Empty).Split(';')[0].Trim();

        if (!AllowedTypes.TryGetValue(type, out var extension))
            throw new BadRequestException(
                $"'{claimed}' cannot be stored as proof. Valid types: "
              + $"{string.Join(", ", AllowedTypes.Keys)}.");

        if (!LooksLike(type, content))
            throw new BadRequestException(
                $"The upload says it is {type}, and the content is not. Check the file.");

        return (type.ToLowerInvariant(), extension);
    }

    private static bool LooksLike(string contentType, byte[] b) => contentType.ToLowerInvariant() switch
    {
        "image/png"       => StartsWith(b, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg"      => StartsWith(b, [0xFF, 0xD8, 0xFF]),
        "image/gif"       => StartsWith(b, "GIF87a"u8) || StartsWith(b, "GIF89a"u8),
        "application/pdf" => StartsWith(b, "%PDF-"u8),
        _                 => false
    };

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> signature) =>
        content.AsSpan().StartsWith(signature);

    /// <summary>
    /// Built here from values this system issued. An upload-supplied name could carry path
    /// separators or quotes into a Content-Disposition header.
    /// </summary>
    internal static string FileNameFor(string consignmentNumber, ProofFileKind kind, string sha, string extension)
    {
        static string Safe(string value) =>
            new(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());

        return $"{Safe(consignmentNumber)}-{LogisticsCode.Of(kind)}-{sha[..8]}.{extension}".ToLowerInvariant();
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private IQueryable<DeliveryProof> Query() =>
        _db.DeliveryProofs.AsNoTracking()
            .Include(p => p.Consignment).ThenInclude(c => c.Carrier)
            .Include(p => p.ConsignmentStop)
            .Include(p => p.TrackingEvent)
            .Include(p => p.Files.Where(f => !f.IsDelete));

    private static DeliveryProofModel ToModel(DeliveryProof p)
    {
        var model = new DeliveryProofModel
        {
            UUID                = p.UUID,
            ConsignmentUuid     = p.Consignment.UUID,
            ConsignmentNumber   = p.Consignment.ConsignmentNumber,
            ConsignmentStatus   = p.Consignment.Status,
            MasterAwb           = p.Consignment.MasterAwb,
            CarrierName         = p.Consignment.CarrierName ?? p.Consignment.Carrier?.Name,
            ConsignmentStopUuid = p.ConsignmentStop?.UUID,
            StopSequence        = p.ConsignmentStop?.Sequence,
            ReceivedBy          = p.ReceivedBy,
            Relationship        = p.Relationship,
            DeliveredAt         = p.DeliveredAt,
            Location            = p.Location,
            Notes               = p.Notes,
            Source              = p.Source,
            CarrierStatus       = p.TrackingEvent?.CarrierStatus,
            CreatedDate         = p.CreatedDate,
            Files =
            [
                .. p.Files.Where(f => !f.IsDelete)
                    .OrderBy(f => f.CreatedDate)
                    .Select(f => new DeliveryProofFileModel
                    {
                        UUID        = f.UUID,
                        Kind        = f.Kind,
                        ContentType = f.ContentType,
                        FileName    = f.FileName,
                        SizeBytes   = f.SizeBytes,
                        Sha256      = f.Sha256,
                        CreatedDate = f.CreatedDate
                    })
            ]
        };

        var weak = WhyWeak(p);

        model.IsDefensible = weak is null;

        if (weak is not null) model.Warnings.Add(weak);

        if (p.Consignment.Status != Delivered)
            model.Warnings.Add(
                $"The consignment is {p.Consignment.Status}, not delivered. A proof against a "
              + "movement the system does not think happened will be questioned.");

        return model;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
