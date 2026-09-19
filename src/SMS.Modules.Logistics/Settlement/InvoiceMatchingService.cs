using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Settlement;

/// <summary>
/// Tying each line of a carrier's bill to the movement it is charging for.
/// <para>
/// <b>The airway bill first.</b> It is the carrier's own reference, it is on every bill it sends,
/// and it is the only identifier both sides agree on. Our consignment number appears only where the
/// carrier echoed back a reference we gave it, which many do not.
/// </para>
/// <para>
/// <b>Nothing is guessed.</b> Where more than one movement fits, the line is left AMBIGUOUS for a
/// person: charging the wrong consignment reconciles to nothing and hides a real discrepancy behind
/// a plausible one.
/// </para>
/// </summary>
public interface IInvoiceMatchingService
{
    /// <summary>Matches every line on a bill that is not already settled. Idempotent.</summary>
    Task<InvoiceMatchResultModel?> MatchInvoiceAsync(Guid invoiceUuid, int userId, CancellationToken ct = default);

    /// <summary>What a bill's lines are tied to now, without changing anything.</summary>
    Task<InvoiceMatchResultModel?> GetMatchesAsync(Guid invoiceUuid, CancellationToken ct = default);

    /// <summary>Everything still needing a person — the queue.</summary>
    Task<PaginatedResponse<InvoiceLineMatchModel>> GetQueueAsync(
        UnmatchedLineFilter filter, CancellationToken ct = default);

    /// <summary>Consignments a line could plausibly belong to, for matching by hand.</summary>
    Task<IReadOnlyList<MatchCandidateModel>> GetCandidatesAsync(Guid lineUuid, CancellationToken ct = default);

    Task<bool> MatchLineAsync(Guid lineUuid, MatchLineRequest req, int userId, CancellationToken ct = default);

    /// <summary>Marks a line as not a movement charge at all, so it leaves the queue.</summary>
    Task<bool> ExcludeLineAsync(Guid lineUuid, ExcludeLineRequest req, int userId, CancellationToken ct = default);

    /// <summary>Puts a line back in the queue, whatever it was tied to.</summary>
    Task<bool> UnmatchLineAsync(Guid lineUuid, ExcludeLineRequest req, int userId, CancellationToken ct = default);
}

internal sealed class InvoiceMatchingService : IInvoiceMatchingService
{
    private static readonly string Unmatched = LogisticsCode.Of(InvoiceLineMatchStatus.Unmatched);
    private static readonly string Matched   = LogisticsCode.Of(InvoiceLineMatchStatus.Matched);
    private static readonly string Ambiguous = LogisticsCode.Of(InvoiceLineMatchStatus.Ambiguous);
    private static readonly string Excluded  = LogisticsCode.Of(InvoiceLineMatchStatus.Excluded);

    private readonly LogisticsDbContext _db;
    public InvoiceMatchingService(LogisticsDbContext db) => _db = db;

    // ── Matching a whole bill ─────────────────────────────────────────────────

    public async Task<InvoiceMatchResultModel?> MatchInvoiceAsync(
        Guid invoiceUuid, int userId, CancellationToken ct = default)
    {
        var invoice = await _db.CarrierInvoices
            .Include(i => i.Carrier)
            .Include(i => i.Lines).ThenInclude(l => l.MatchedConsignment)
            .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete, ct);

        if (invoice is null) return null;

        if (invoice.Status == LogisticsCode.Of(CarrierInvoiceStatus.Cancelled))
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' was withdrawn. There is nothing to match it against.");

        // Only this carrier's consignments. An airway bill from one carrier appearing on another's
        // bill is a keying error or a genuinely wrong bill, and matching across carriers would hide
        // whichever it is behind a tidy-looking result.
        var candidates = await _db.Consignments.AsNoTracking()
            .Where(c => !c.IsDelete && c.CarrierId == invoice.CarrierId)
            .Select(c => new Candidate(c.Id, c.ConsignmentNumber, c.MasterAwb))
            .ToListAsync(ct);

        var byAwb = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.MasterAwb))
            .GroupBy(c => c.MasterAwb!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var byNumber = candidates
            .GroupBy(c => c.ConsignmentNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Airway bills belonging to some *other* carrier, so a line that points at one can be told
        // why it did not match rather than simply failing.
        var elsewhere = await _db.Consignments.AsNoTracking()
            .Where(c => !c.IsDelete && c.CarrierId != invoice.CarrierId && c.MasterAwb != null)
            .Select(c => new { c.MasterAwb, CarrierName = c.CarrierName })
            .ToListAsync(ct);

        var otherCarrierAwbs = elsewhere
            .GroupBy(c => c.MasterAwb!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CarrierName ?? "another carrier",
                          StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;

        foreach (var line in invoice.Lines)
        {
            // Settled lines are left alone, so re-running this never undoes a person's decision.
            if (line.MatchStatus == Matched || line.MatchStatus == Excluded) continue;

            Apply(line, byAwb, byNumber, otherCarrierAwbs, now, userId);
        }

        await _db.SaveChangesAsync(ct);

        return await BuildResultAsync(invoice, ct);
    }

    /// <summary>One of this carrier's movements, as far as matching cares.</summary>
    private sealed record Candidate(int Id, string ConsignmentNumber, string? MasterAwb);

    /// <summary>
    /// Decides one line. The airway bill is tried first because it is the carrier's own reference
    /// and the only identifier both sides always have; our consignment number is a fallback,
    /// because it only appears where the carrier echoed back what we sent it.
    /// </summary>
    private static void Apply(
        CarrierInvoiceLine line,
        Dictionary<string, List<Candidate>> byAwb,
        Dictionary<string, List<Candidate>> byNumber,
        Dictionary<string, string> otherCarrierAwbs,
        DateTime now, int userId)
    {
        void Decide(string status, int? consignmentId, InvoiceLineMatchMethod? method, string? note)
        {
            line.MatchStatus          = status;
            line.MatchedConsignmentId = consignmentId;
            line.MatchMethod          = method is { } m ? LogisticsCode.Of(m) : null;
            line.MatchedAt            = now;
            line.MatchedByUser        = userId;
            line.MatchNote            = note;
        }

        var awb = line.AwbNumber?.Trim();

        if (!string.IsNullOrWhiteSpace(awb))
        {
            if (byAwb.TryGetValue(awb, out var onAwb))
            {
                // More than one of our consignments carrying one airway bill is itself a problem,
                // and picking either would settle a charge against a movement nobody checked.
                if (onAwb.Count > 1)
                {
                    Decide(Ambiguous, null, null,
                        $"{onAwb.Count} consignments carry airway bill '{awb}': "
                      + $"{string.Join(", ", onAwb.Select(c => c.ConsignmentNumber))}.");
                    return;
                }

                Decide(Matched, onAwb[0].Id, InvoiceLineMatchMethod.Awb, null);
                return;
            }

            // The airway bill exists, but on a different carrier's consignment. Said plainly,
            // because it means either the bill or the consignment is wrong.
            if (otherCarrierAwbs.TryGetValue(awb, out var otherCarrier))
            {
                Decide(Ambiguous, null, null,
                    $"Airway bill '{awb}' belongs to a consignment carried by {otherCarrier}, not by "
                  + "the carrier that sent this bill.");
                return;
            }
        }

        var reference = line.ConsignmentReference?.Trim();

        if (!string.IsNullOrWhiteSpace(reference) && byNumber.TryGetValue(reference, out var onNumber))
        {
            if (onNumber.Count > 1)
            {
                Decide(Ambiguous, null, null,
                    $"More than one consignment is numbered '{reference}'.");
                return;
            }

            Decide(Matched, onNumber[0].Id, InvoiceLineMatchMethod.Reference, null);
            return;
        }

        Decide(Unmatched, null, null,
            string.IsNullOrWhiteSpace(awb) && string.IsNullOrWhiteSpace(reference)
                ? "The line carries neither an airway bill nor a consignment reference."
                : "Nothing this carrier moved matches the references on this line.");
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<InvoiceMatchResultModel?> GetMatchesAsync(
        Guid invoiceUuid, CancellationToken ct = default)
    {
        var invoice = await _db.CarrierInvoices.AsNoTracking()
            .Include(i => i.Carrier)
            .Include(i => i.Lines).ThenInclude(l => l.MatchedConsignment)
            .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete, ct);

        return invoice is null ? null : await BuildResultAsync(invoice, ct);
    }

    private async Task<InvoiceMatchResultModel> BuildResultAsync(CarrierInvoice invoice, CancellationToken ct)
    {
        var lines = invoice.Lines.OrderBy(l => l.LineNo).ToList();

        var result = new InvoiceMatchResultModel
        {
            InvoiceUuid   = invoice.UUID,
            InvoiceNumber = invoice.InvoiceNumber,
            LineCount     = lines.Count,
            Matched       = lines.Count(l => l.MatchStatus == Matched),
            Ambiguous     = lines.Count(l => l.MatchStatus == Ambiguous),
            Unmatched     = lines.Count(l => l.MatchStatus == Unmatched),
            Excluded      = lines.Count(l => l.MatchStatus == Excluded)
        };

        result.IsComplete = result.Ambiguous == 0 && result.Unmatched == 0;

        var duplicates = await DuplicateChargesAsync(invoice, lines, ct);

        // Looked up rather than read off the navigation. A line matched moments ago has its foreign
        // key set but no navigation to fix up — the consignment was never tracked — so relying on
        // one would return a blank consignment number precisely when somebody has just matched it.
        var numbers = await ConsignmentNumbersAsync(lines, ct);

        result.Lines =
        [
            .. lines.Select(l => ToModel(l, invoice, duplicates.GetValueOrDefault(l.Id), numbers))
        ];

        if (result.Unmatched > 0 || result.Ambiguous > 0)
            result.Warnings.Add(
                $"{result.Unmatched + result.Ambiguous} line(s) are not tied to a movement. Lines "
              + "nothing matches are where a wrong charge goes unnoticed, so they stay in the queue.");

        if (duplicates.Count > 0)
            result.Warnings.Add(
                $"{duplicates.Count} line(s) charge a consignment that is already charged on another "
              + "bill. That may be legitimate — carriage and a surcharge can be billed separately — "
              + "but a double charge looks exactly the same until somebody checks.");

        return result;
    }

    /// <summary>
    /// Lines whose consignment is already charged on a <em>different</em> bill. Reported, never
    /// refused: a carrier may bill carriage and a surcharge separately and weeks apart.
    /// </summary>
    private async Task<Dictionary<int, string>> DuplicateChargesAsync(
        CarrierInvoice invoice, List<CarrierInvoiceLine> lines, CancellationToken ct)
    {
        var consignmentIds = lines
            .Where(l => l.MatchedConsignmentId is not null)
            .Select(l => l.MatchedConsignmentId!.Value)
            .Distinct()
            .ToList();

        if (consignmentIds.Count == 0) return [];

        var elsewhere = await _db.CarrierInvoiceLines.AsNoTracking()
            .Where(l => l.CarrierInvoiceId != invoice.Id
                     && !l.CarrierInvoice.IsDelete
                     && l.CarrierInvoice.Status != LogisticsCode.Of(CarrierInvoiceStatus.Cancelled)
                     && l.MatchedConsignmentId != null
                     && consignmentIds.Contains(l.MatchedConsignmentId!.Value))
            .Select(l => new { l.MatchedConsignmentId, l.CarrierInvoice.InvoiceNumber })
            .ToListAsync(ct);

        if (elsewhere.Count == 0) return [];

        var byConsignment = elsewhere
            .GroupBy(x => x.MatchedConsignmentId!.Value)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.InvoiceNumber).Distinct().Order()));

        return lines
            .Where(l => l.MatchedConsignmentId is { } id && byConsignment.ContainsKey(id))
            .ToDictionary(
                l => l.Id,
                l => $"Also charged on {byConsignment[l.MatchedConsignmentId!.Value]}.");
    }

    public async Task<PaginatedResponse<InvoiceLineMatchModel>> GetQueueAsync(
        UnmatchedLineFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var q = _db.CarrierInvoiceLines.AsNoTracking()
            .Include(l => l.CarrierInvoice).ThenInclude(i => i.Carrier)
            .Include(l => l.MatchedConsignment)
            .Where(l => !l.CarrierInvoice.IsDelete
                     && l.CarrierInvoice.Status != LogisticsCode.Of(CarrierInvoiceStatus.Cancelled));

        // The default is everything still needing attention. A queue that defaulted to "everything"
        // would be a list nobody works.
        q = string.IsNullOrWhiteSpace(filter.MatchStatus)
            ? q.Where(l => l.MatchStatus == Unmatched || l.MatchStatus == Ambiguous)
            : q.Where(l => l.MatchStatus == filter.MatchStatus);

        if (filter.CarrierUuid is { } carrierUuid)
            q = q.Where(l => l.CarrierInvoice.Carrier.UUID == carrierUuid);

        if (filter.InvoiceUuid is { } invoiceUuid)
            q = q.Where(l => l.CarrierInvoice.UUID == invoiceUuid);

        var page     = filter.Page     < 1 ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var total = await q.CountAsync(ct);

        var lines = await q
            // Largest first: the biggest unexplained charge is the one worth an hour of somebody's
            // time, and a queue ordered by date buries it.
            .OrderByDescending(l => Math.Abs(l.Amount))
            .ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PaginatedResponse<InvoiceLineMatchModel>
        {
            Data         = [.. lines.Select(l => ToModel(l, l.CarrierInvoice, null))],
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<IReadOnlyList<MatchCandidateModel>> GetCandidatesAsync(
        Guid lineUuid, CancellationToken ct = default)
    {
        var line = await _db.CarrierInvoiceLines.AsNoTracking()
            .Include(l => l.CarrierInvoice).ThenInclude(i => i.Carrier)
            .FirstOrDefaultAsync(l => l.UUID == lineUuid, ct);

        if (line is null) return [];

        var carrierId = line.CarrierInvoice.CarrierId;

        var consignments = await _db.Consignments.AsNoTracking()
            .Where(c => !c.IsDelete && c.CarrierId == carrierId)
            .ToListAsync(ct);

        var awb       = line.AwbNumber?.Trim();
        var reference = line.ConsignmentReference?.Trim();

        var scored = consignments
            .Select(c => new
            {
                Consignment = c,
                Reason = Same(c.MasterAwb, awb)               ? "The airway bill matches."
                       : Same(c.ConsignmentNumber, reference) ? "The consignment number matches."
                       : Contains(c.MasterAwb, awb)           ? "The airway bill is similar."
                       : null
            })
            .Where(x => x.Reason is not null)
            .Take(25)
            .ToList();

        return
        [
            .. scored.Select(x => new MatchCandidateModel
            {
                ConsignmentUuid   = x.Consignment.UUID,
                ConsignmentNumber = x.Consignment.ConsignmentNumber,
                MasterAwb         = x.Consignment.MasterAwb,
                CarrierName       = x.Consignment.CarrierName,
                Status            = x.Consignment.Status,
                FreightCost       = x.Consignment.FreightCost,
                FreightCurrency   = x.Consignment.FreightCurrency,
                Reason            = x.Reason!
            })
        ];
    }

    // ── Deciding by hand ──────────────────────────────────────────────────────

    public async Task<bool> MatchLineAsync(
        Guid lineUuid, MatchLineRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var line = await _db.CarrierInvoiceLines
            .Include(l => l.CarrierInvoice)
            .FirstOrDefaultAsync(l => l.UUID == lineUuid, ct);

        if (line is null) return false;

        EnsureOpen(line);

        var note = Trim(req.Note)
            ?? throw new BadRequestException(
                "Say what this match is based on. Matching by hand is a weaker claim than matching "
              + "on an airway bill, and whoever unpicks it later needs to know why it was made.");

        var consignment = await _db.Consignments
            .FirstOrDefaultAsync(c => c.UUID == req.ConsignmentUuid && !c.IsDelete, ct)
            ?? throw new NotFoundException("Consignment", req.ConsignmentUuid);

        // Still refused across carriers, even by hand. A bill from one carrier charging another's
        // movement is wrong however confident somebody is about it.
        if (consignment.CarrierId != line.CarrierInvoice.CarrierId)
            throw new ConflictException(
                $"{consignment.ConsignmentNumber} was not carried by the carrier that sent this bill. "
              + "Either the bill is wrong or the consignment is — matching them would hide whichever.");

        var now = DateTime.UtcNow;

        line.MatchedConsignmentId = consignment.Id;
        line.MatchStatus          = Matched;
        line.MatchMethod          = LogisticsCode.Of(InvoiceLineMatchMethod.Manual);
        line.MatchedAt            = now;
        line.MatchedByUser        = userId;
        line.MatchNote            = note;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ExcludeLineAsync(
        Guid lineUuid, ExcludeLineRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var line = await _db.CarrierInvoiceLines
            .Include(l => l.CarrierInvoice)
            .FirstOrDefaultAsync(l => l.UUID == lineUuid, ct);

        if (line is null) return false;

        EnsureOpen(line);

        var reason = Trim(req.Reason)
            ?? throw new BadRequestException(
                "Say why this is not a movement charge. It is a judgement, and a line that leaves "
              + "the queue without one is a charge nobody ever explained.");

        var now = DateTime.UtcNow;

        line.MatchedConsignmentId = null;
        line.MatchStatus          = Excluded;
        line.MatchMethod          = null;
        line.MatchedAt            = now;
        line.MatchedByUser        = userId;
        line.MatchNote            = reason;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UnmatchLineAsync(
        Guid lineUuid, ExcludeLineRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var line = await _db.CarrierInvoiceLines
            .Include(l => l.CarrierInvoice)
            .FirstOrDefaultAsync(l => l.UUID == lineUuid, ct);

        if (line is null) return false;

        EnsureOpen(line);

        var reason = Trim(req.Reason)
            ?? throw new BadRequestException("Say why this match is being undone.");

        var now = DateTime.UtcNow;

        line.MatchedConsignmentId = null;
        line.MatchStatus          = Unmatched;
        line.MatchMethod          = null;
        line.MatchedAt            = now;
        line.MatchedByUser        = userId;
        line.MatchNote            = reason;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void EnsureOpen(CarrierInvoiceLine line)
    {
        if (line.CarrierInvoice.Status == LogisticsCode.Of(CarrierInvoiceStatus.Cancelled))
            throw new ConflictException(
                $"Invoice '{line.CarrierInvoice.InvoiceNumber}' was withdrawn. Its lines are not "
              + "matched against anything.");
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>Consignment UUID and number for every line that points at one.</summary>
    private async Task<Dictionary<int, (Guid Uuid, string Number)>> ConsignmentNumbersAsync(
        IEnumerable<CarrierInvoiceLine> lines, CancellationToken ct)
    {
        var ids = lines
            .Where(l => l.MatchedConsignmentId is not null)
            .Select(l => l.MatchedConsignmentId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return [];

        return await _db.Consignments.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => (c.UUID, c.ConsignmentNumber), ct);
    }

    private static InvoiceLineMatchModel ToModel(
        CarrierInvoiceLine l, CarrierInvoice invoice, string? duplicate,
        IReadOnlyDictionary<int, (Guid Uuid, string Number)>? numbers = null) => new()
    {
        LineUuid                 = l.UUID,
        LineNo                   = l.LineNo,
        Description              = l.Description,
        InvoiceUuid              = invoice.UUID,
        InvoiceNumber            = invoice.InvoiceNumber,
        CarrierName              = invoice.CarrierName ?? invoice.Carrier?.Name ?? string.Empty,
        AwbNumber                = l.AwbNumber,
        ConsignmentReference     = l.ConsignmentReference,
        ChargeCode               = l.ChargeCode,
        Amount                   = l.Amount,
        Currency                 = invoice.Currency,
        MatchStatus              = l.MatchStatus,
        MatchMethod              = l.MatchMethod,
        MatchedConsignmentUuid   = Resolved(l, numbers)?.Uuid   ?? l.MatchedConsignment?.UUID,
        MatchedConsignmentNumber = Resolved(l, numbers)?.Number ?? l.MatchedConsignment?.ConsignmentNumber,
        MatchedAt                = l.MatchedAt,
        MatchNote                = l.MatchNote,
        DuplicateWarning         = duplicate
    };

    private static (Guid Uuid, string Number)? Resolved(
        CarrierInvoiceLine line, IReadOnlyDictionary<int, (Guid Uuid, string Number)>? numbers) =>
        line.MatchedConsignmentId is { } id && numbers is not null && numbers.TryGetValue(id, out var found)
            ? found
            : null;

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
     && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? haystack, string? needle) =>
        !string.IsNullOrWhiteSpace(haystack) && !string.IsNullOrWhiteSpace(needle)
     && needle.Trim().Length >= 4
     && haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
