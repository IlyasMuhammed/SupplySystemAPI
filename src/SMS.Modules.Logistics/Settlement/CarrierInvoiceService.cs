using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Settlement;

/// <summary>
/// Bills from carriers — the third leg of the three-way match, and the only one that had no record
/// anywhere before this.
/// </summary>
public interface ICarrierInvoiceService
{
    Task<Guid> CreateAsync(CreateCarrierInvoiceRequest req, int userId, CancellationToken ct = default);
    Task<PaginatedResponse<CarrierInvoiceModel>> GetListAsync(CarrierInvoiceFilter filter, CancellationToken ct = default);
    Task<CarrierInvoiceModel?> GetByUuidAsync(Guid uuid, CancellationToken ct = default);
    Task<bool> PatchAsync(Guid uuid, PatchCarrierInvoiceRequest req, int userId, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid uuid, CancelCarrierInvoiceRequest req, int userId, CancellationToken ct = default);

    /// <summary>
    /// Brings in many bills at once, reporting each separately. One bad bill never fails the batch.
    /// </summary>
    Task<CarrierInvoiceImportModel> ImportAsync(
        ImportCarrierInvoicesRequest req, int userId, CancellationToken ct = default);
}

internal sealed class CarrierInvoiceService : ICarrierInvoiceService
{
    /// <summary>
    /// How far the lines may miss the header before it is treated as a mistake. A carrier rounding
    /// its own total to the cent is ordinary; anything larger is a keying or parsing error, and the
    /// whole reason to check is that a header nobody checked absorbs a discrepancy silently.
    /// </summary>
    internal const decimal Tolerance = 0.01m;

    private const string KeyedSource    = "KEYED";
    private const string ImportedSource = "IMPORT";

    private readonly LogisticsDbContext _db;
    public CarrierInvoiceService(LogisticsDbContext db) => _db = db;

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(
        CreateCarrierInvoiceRequest req, int userId, CancellationToken ct = default) =>
        await CreateAsync(req, userId, KeyedSource, ct);

    private async Task<Guid> CreateAsync(
        CreateCarrierInvoiceRequest req, int userId, string source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var carrier = await _db.Carriers.FirstOrDefaultAsync(c => c.UUID == req.CarrierUuid && !c.IsDelete, ct)
            ?? throw new NotFoundException("Carrier", req.CarrierUuid);

        var number   = Require(req.InvoiceNumber, "A carrier's bill needs its own invoice number.");
        var currency = Currency(req.Currency);

        if (req.InvoiceDate == default)
            throw new BadRequestException("A carrier's bill needs the date the carrier issued it.");

        if (req.DueDate is { } due && due < req.InvoiceDate.Date)
            throw new BadRequestException("A bill cannot fall due before it was issued.");

        // The carrier's number, not ours. Keying the same bill twice is how it gets paid twice, and
        // the duplicate looks exactly as legitimate as the original.
        if (await _db.CarrierInvoices.AnyAsync(
                i => i.CarrierId == carrier.Id && !i.IsDelete && i.InvoiceNumber == number, ct))
            throw new ConflictException(
                $"{carrier.Name} invoice '{number}' is already recorded. Paying the same bill twice "
              + "is what this refusal exists to prevent — cancel the first if it was wrong.");

        var lines = BuildLines(req.Lines, req.TotalAmount, currency);

        var now = DateTime.UtcNow;

        var invoice = new CarrierInvoice
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = carrier.OrganizationId,
            CarrierId      = carrier.Id,
            CarrierName    = carrier.Name,
            InvoiceNumber  = number,
            InvoiceDate    = req.InvoiceDate.Date,
            DueDate        = req.DueDate?.Date,
            Currency       = currency,
            TotalAmount    = req.TotalAmount,
            TaxAmount      = req.TaxAmount,
            Status         = LogisticsCode.Of(CarrierInvoiceStatus.Received),
            Source         = source,
            Notes          = Trim(req.Notes),
            CreatedBy      = userId,
            CreatedDate    = now
        };

        foreach (var line in lines)
        {
            line.OrganizationId = carrier.OrganizationId;
            line.CreatedBy      = userId;
            line.CreatedDate    = now;
            invoice.Lines.Add(line);
        }

        _db.CarrierInvoices.Add(invoice);
        await _db.SaveChangesAsync(ct);

        return invoice.UUID;
    }

    /// <summary>
    /// Turns requested lines into entities, and refuses a bill that does not add up.
    /// </summary>
    private static List<CarrierInvoiceLine> BuildLines(
        List<CarrierInvoiceLineRequest>? requested, decimal total, string currency)
    {
        if (requested is null || requested.Count == 0)
            throw new BadRequestException(
                "A bill with no lines cannot be matched against anything. Record at least one charge.");

        if (total == 0m)
            throw new BadRequestException("A bill for nothing is not a bill. Check the total.");

        var lines = new List<CarrierInvoiceLine>();
        var lineNo = 1;

        foreach (var request in requested)
        {
            if (request.Amount == 0m)
                throw new BadRequestException(
                    $"Line {lineNo} charges nothing. A zero line explains nothing and matches nothing.");

            lines.Add(new CarrierInvoiceLine
            {
                UUID                 = Guid.NewGuid(),
                LineNo               = lineNo++,
                Description          = Require(request.Description, $"Line {lineNo - 1} needs a description."),
                AwbNumber            = Trim(request.AwbNumber),
                ConsignmentReference = Trim(request.ConsignmentReference),
                ChargeCode           = Trim(request.ChargeCode)?.ToUpperInvariant(),
                ServiceCode          = Trim(request.ServiceCode),
                ChargeableWeightKg   = request.ChargeableWeightKg,
                ShipDate             = request.ShipDate?.Date,
                Amount               = request.Amount
            });
        }

        var sum = lines.Sum(l => l.Amount);

        // The check the whole document rests on. Matching line by line against a header that says
        // something else is how a discrepancy gets absorbed without anybody seeing it.
        if (Math.Abs(sum - total) > Tolerance)
            throw new BadRequestException(
                $"The lines come to {currency} {sum:N2} and the bill says {currency} {total:N2}. "
              + "A bill whose lines do not add up to its own total has been mis-keyed or mis-read — "
              + "matching it line by line would hide the difference rather than find it.");

        return lines;
    }

    // ── Import ────────────────────────────────────────────────────────────────

    /// <remarks>
    /// Each bill is judged on its own. An import that stops at the first problem leaves somebody
    /// re-running it and re-importing everything before it, which is how duplicates get created —
    /// exactly what the unique invoice number exists to prevent.
    /// <para>
    /// Parsing a carrier's own file format is deliberately not here: every carrier's layout differs
    /// and a parser per carrier belongs with that carrier's adapter, not in the document.
    /// </para>
    /// </remarks>
    public async Task<CarrierInvoiceImportModel> ImportAsync(
        ImportCarrierInvoicesRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var model = new CarrierInvoiceImportModel();

        foreach (var invoice in req.Invoices ?? [])
        {
            var number = Trim(invoice.InvoiceNumber) ?? "(no number)";

            try
            {
                var uuid = await CreateAsync(invoice, userId, ImportedSource, ct);

                model.Accepted++;
                model.Results.Add(new CarrierInvoiceImportResultModel
                {
                    InvoiceNumber = number, Accepted = true, UUID = uuid
                });
            }
            catch (Exception ex) when (ex is BadRequestException or ConflictException or NotFoundException)
            {
                model.Refused++;
                model.Results.Add(new CarrierInvoiceImportResultModel
                {
                    InvoiceNumber = number, Accepted = false, Reason = ex.Message
                });
            }
        }

        return model;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<CarrierInvoiceModel>> GetListAsync(
        CarrierInvoiceFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var q = _db.CarrierInvoices.AsNoTracking().Where(i => !i.IsDelete);

        if (filter.CarrierUuid is { } carrierUuid)
            q = q.Where(i => i.Carrier.UUID == carrierUuid);

        if (!string.IsNullOrWhiteSpace(filter.Status))
            q = q.Where(i => i.Status == filter.Status);

        if (filter.From is { } from) q = q.Where(i => i.InvoiceDate >= from.Date);
        if (filter.To   is { } to)   q = q.Where(i => i.InvoiceDate <= to.Date);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            // The airway bill is searched too: somebody chasing one parcel's charge has that, not
            // the carrier's invoice number.
            q = q.Where(i => i.InvoiceNumber.ToLower().Contains(s)
                          || (i.CarrierName != null && i.CarrierName.ToLower().Contains(s))
                          || i.Lines.Any(l => l.AwbNumber != null && l.AwbNumber.ToLower().Contains(s)));
        }

        var page     = filter.Page     < 1 ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var total = await q.CountAsync(ct);

        var data = await q
            .OrderByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new CarrierInvoiceModel
            {
                UUID          = i.UUID,
                CarrierUuid   = i.Carrier.UUID,
                CarrierName   = i.CarrierName ?? i.Carrier.Name,
                InvoiceNumber = i.InvoiceNumber,
                InvoiceDate   = i.InvoiceDate,
                DueDate       = i.DueDate,
                Currency      = i.Currency,
                TotalAmount   = i.TotalAmount,
                TaxAmount     = i.TaxAmount,
                LineTotal     = i.Lines.Sum(l => l.Amount),
                Status        = i.Status,
                Source        = i.Source,
                Notes         = i.Notes,
                CancelReason  = i.CancelReason,
                LineCount     = i.Lines.Count,
                CreatedDate   = i.CreatedDate
            })
            .ToListAsync(ct);

        return new PaginatedResponse<CarrierInvoiceModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<CarrierInvoiceModel?> GetByUuidAsync(Guid uuid, CancellationToken ct = default)
    {
        var invoice = await _db.CarrierInvoices.AsNoTracking()
            .Include(i => i.Carrier)
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.UUID == uuid && !i.IsDelete, ct);

        return invoice is null ? null : ToModel(invoice);
    }

    private static CarrierInvoiceModel ToModel(CarrierInvoice i) => new()
    {
        UUID          = i.UUID,
        CarrierUuid   = i.Carrier.UUID,
        CarrierName   = i.CarrierName ?? i.Carrier.Name,
        InvoiceNumber = i.InvoiceNumber,
        InvoiceDate   = i.InvoiceDate,
        DueDate       = i.DueDate,
        Currency      = i.Currency,
        TotalAmount   = i.TotalAmount,
        TaxAmount     = i.TaxAmount,
        LineTotal     = i.Lines.Sum(l => l.Amount),
        Status        = i.Status,
        Source        = i.Source,
        Notes         = i.Notes,
        CancelReason  = i.CancelReason,
        LineCount     = i.Lines.Count,
        CreatedDate   = i.CreatedDate,
        Lines = [.. i.Lines.OrderBy(l => l.LineNo).Select(l => new CarrierInvoiceLineModel
        {
            UUID                 = l.UUID,
            LineNo               = l.LineNo,
            Description          = l.Description,
            AwbNumber            = l.AwbNumber,
            ConsignmentReference = l.ConsignmentReference,
            ChargeCode           = l.ChargeCode,
            ServiceCode          = l.ServiceCode,
            ChargeableWeightKg   = l.ChargeableWeightKg,
            ShipDate             = l.ShipDate,
            Amount               = l.Amount
        })]
    };

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(
        Guid uuid, PatchCarrierInvoiceRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var invoice = await _db.CarrierInvoices
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.UUID == uuid && !i.IsDelete, ct);

        if (invoice is null) return false;

        EnsureOpen(invoice);

        if (req.InvoiceDate is { } issued) invoice.InvoiceDate = issued.Date;
        if (req.DueDate     is { } due)    invoice.DueDate     = due.Date;
        if (req.TotalAmount is { } total)  invoice.TotalAmount = total;
        if (req.TaxAmount   is { } tax)    invoice.TaxAmount   = tax;
        if (req.Notes is not null)         invoice.Notes       = Trim(req.Notes);

        if (invoice.DueDate is { } dueDate && dueDate < invoice.InvoiceDate)
            throw new BadRequestException("A bill cannot fall due before it was issued.");

        var now = DateTime.UtcNow;

        if (req.Lines is not null)
        {
            // Replaced whole. Correcting one line at a time would let the lines and the header
            // disagree for as long as it took to fix the other.
            _db.CarrierInvoiceLines.RemoveRange(invoice.Lines);
            invoice.Lines.Clear();

            foreach (var line in BuildLines(req.Lines, invoice.TotalAmount, invoice.Currency))
            {
                line.OrganizationId = invoice.OrganizationId;
                line.CreatedBy      = userId;
                line.CreatedDate    = now;
                invoice.Lines.Add(line);
            }
        }
        else if (req.TotalAmount is not null)
        {
            // The total moved and the lines did not. Checked against what is already stored, so a
            // header cannot be edited away from its own lines.
            var sum = invoice.Lines.Sum(l => l.Amount);

            if (Math.Abs(sum - invoice.TotalAmount) > Tolerance)
                throw new BadRequestException(
                    $"The lines come to {invoice.Currency} {sum:N2} and the bill would say "
                  + $"{invoice.Currency} {invoice.TotalAmount:N2}. Change the lines too, or the two "
                  + "no longer describe the same bill.");
        }

        invoice.ModifiedBy   = userId;
        invoice.ModifiedDate = now;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> CancelAsync(
        Guid uuid, CancelCarrierInvoiceRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var invoice = await _db.CarrierInvoices.FirstOrDefaultAsync(i => i.UUID == uuid && !i.IsDelete, ct);
        if (invoice is null) return false;

        EnsureOpen(invoice);

        var reason = Trim(req.Reason)
            ?? throw new BadRequestException(
                "Say why this bill is being withdrawn. A bill that disappears without a reason "
              + "cannot be explained when the carrier chases it.");

        invoice.Status       = LogisticsCode.Of(CarrierInvoiceStatus.Cancelled);
        invoice.CancelReason = reason;
        invoice.ModifiedBy   = userId;
        invoice.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void EnsureOpen(CarrierInvoice invoice)
    {
        if (invoice.Status == LogisticsCode.Of(CarrierInvoiceStatus.Cancelled))
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' was withdrawn. Record a fresh bill rather than "
              + "reviving this one — the carrier's number has not changed, but what it says has.");
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static string Currency(string? value)
    {
        var currency = Trim(value)?.ToUpperInvariant();

        if (currency is null || currency.Length != 3 || !currency.All(char.IsLetter))
            throw new BadRequestException(
                "A carrier's bill needs a three-letter ISO currency — an amount with no currency is a number.");

        return currency;
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new BadRequestException(message) : value.Trim();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
