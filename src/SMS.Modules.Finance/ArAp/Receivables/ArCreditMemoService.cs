using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.ArAp.Receivables;

// ── Models (API-CONTRACT §4) ────────────────────────────────────────────────────────────────────────────────────────

public sealed class CreateCreditMemoRequest
{
    public Guid      CustomerUuid        { get; set; }
    public Guid?     OriginalInvoiceUuid { get; set; }
    /// <summary>Default today; not in the future.</summary>
    public DateOnly? Date                { get; set; }
    /// <summary>Standalone memo only (default the sale base); a memo against an invoice is in the invoice's currency.</summary>
    public string?   CurrencyCode        { get; set; }
    public string?   Reason              { get; set; }
    public List<CreditMemoLineRequest> Lines { get; set; } = [];
    /// <summary>D-AR-7 (additive): apply the memo to its original invoice's open balance at once. Default true when there is one.</summary>
    public bool?     ApplyToOriginal     { get; set; }
}

public sealed class CreditMemoLineRequest
{
    public string?  Description     { get; set; }
    /// <summary>Gross (tax included), in the memo's currency, &gt; 0, 2 dp.</summary>
    public decimal  Amount          { get; set; }
    public Guid?    TaxCodeUuid     { get; set; }
    /// <summary>D-AR-7 (additive): the original invoice line credited — its product travels to QuickBooks as the line's item.</summary>
    public Guid?    InvoiceLineUuid { get; set; }
}

public sealed class ApplyCreditMemoRequest
{
    public List<CreditApplicationRequest> Applications { get; set; } = [];
}

public sealed class CreditApplicationRequest
{
    public Guid    InvoiceUuid { get; set; }
    public decimal Amount      { get; set; }
}

public sealed class CreditApplicationModel
{
    public Guid      Uuid           { get; set; }
    public Guid      CreditNoteUuid { get; set; }
    public string    CreditNoteNumber { get; set; } = string.Empty;
    public Guid      InvoiceUuid    { get; set; }
    public string    InvoiceNumber  { get; set; } = string.Empty;
    public decimal   Amount         { get; set; }
    public DateTime  AppliedAt      { get; set; }
    public string?   AppliedByName  { get; set; }
    public DateTime? ReversedAt     { get; set; }
}

public sealed class CreditMemoLineModel
{
    public int      LineNo      { get; set; }
    public string   Description { get; set; } = string.Empty;
    public decimal  Amount      { get; set; }
    public decimal  TaxAmount   { get; set; }
    public decimal  TaxPercent  { get; set; }
    public string?  TaxCode     { get; set; }
}

public sealed class CreditMemoModel
{
    public Guid      Uuid                  { get; set; }
    public string    CreditNoteNumber      { get; set; } = string.Empty;
    public string    SourceType            { get; set; } = string.Empty;
    public Guid      CustomerUuid          { get; set; }
    public string    CustomerName          { get; set; } = string.Empty;
    public Guid?     OriginalInvoiceUuid   { get; set; }
    public string?   OriginalInvoiceNumber { get; set; }
    public DateOnly  Date                  { get; set; }
    public string    CurrencyCode          { get; set; } = string.Empty;
    public decimal   Subtotal              { get; set; }
    public decimal   TaxAmount             { get; set; }
    public decimal   Total                 { get; set; }
    public decimal   Applied               { get; set; }
    public decimal   Refunded              { get; set; }
    public decimal   Remaining             { get; set; }
    public decimal?  RemainingBase         { get; set; }
    public string?   Notes                 { get; set; }
    public List<CreditMemoLineModel>    Lines        { get; set; } = [];
    public List<CreditApplicationModel> Applications { get; set; } = [];
}

/// <summary>
/// A48 AR — C7 AR credit memos (tasks 607–609, D-6, D-AR-6..8, API-CONTRACT §4).
/// <para>
/// <b>Create</b>: an ISSUED-style CREDIT_NOTE <see cref="SalesInvoice"/> (source AR_CREDIT_MEMO, number SCN-…) of amount
/// lines, booked as A38 books its credit notes — one CREDIT_NOTE credit on the customer ledger for the whole total — and
/// posted through A44 as a credit memo (Dr REVENUE + Dr TAX / Cr RECEIVABLE: TS-CM-001). Financial only: no stock moves
/// (a goods return is the POS / sales-return path).
/// </para>
/// <para>
/// <b>Apply</b>: one <see cref="CustomerCreditApplication"/> per invoice; the invoice's CreditedAmount rises and BalanceDue
/// falls (status as A38 derives it), the note's CreditedAmount rises. No ledger entry: the note's credit was booked in full
/// when it was raised (A38's application writes none either), so applying moves credit between documents, not the balance.
/// Any customer credit note with credit left can be applied — A38 POS notes too. Serialized per customer.
/// </para>
/// </summary>
internal sealed class ArCreditMemoService
{
    private const int MaxAttempts = 5;

    private static readonly string[] Creditable =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Paid, SalesInvoiceStatuses.Overdue];
    private static readonly string[] Open =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Overdue];

    private readonly FinanceDbContext             _db;
    private readonly ICustomerLedgerService       _ledger;
    private readonly ArDocumentCurrency           _currency;
    private readonly ILogger<ArCreditMemoService> _log;
    private readonly IPartnerLookupService?       _partners;
    private readonly ArDocumentPostings?          _postings;
    private readonly TimeProvider                 _clock;

    public ArCreditMemoService(FinanceDbContext db, ICustomerLedgerService ledger, ArDocumentCurrency currency,
        ILogger<ArCreditMemoService> log, IPartnerLookupService? partners = null, ArDocumentPostings? postings = null,
        TimeProvider? clock = null)
    {
        _db       = db;
        _ledger   = ledger;
        _currency = currency;
        _log      = log;
        _partners = partners;
        _postings = postings;
        _clock    = clock ?? TimeProvider.System;
    }

    // ══ Create ═════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CreditMemoModel> CreateAsync(CreateCreditMemoRequest request, ArApCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org    = Org(caller);
        var reason = CreditControlService.RequireReason(request.Reason);
        if (request.CustomerUuid == Guid.Empty) throw new BadRequestException("customerUuid is required.");
        if (request.Lines is null || request.Lines.Count == 0) throw new BadRequestException("A credit memo needs at least one line.");
        if (request.Lines.Count > 200) throw new BadRequestException("A credit memo can have at most 200 lines.");

        var now   = _clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var date  = request.Date ?? today;
        if (date > today) throw new BadRequestException("The credit memo date cannot be in the future.");

        var customerName = await CustomerNameAsync(org, request.CustomerUuid, ct);

        SalesInvoice? original = null;
        if (request.OriginalInvoiceUuid is { } ou && ou != Guid.Empty)
        {
            original = await _db.SalesInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.OrganizationId == org && i.UUID == ou && !i.IsDelete, ct)
                ?? throw new NotFoundException("SalesInvoice", ou);
            if (original.PartnerId != request.CustomerUuid)
                throw new BadRequestException($"Invoice {original.InvoiceNumber} belongs to another customer.");
            if (!Creditable.Contains(original.Status))
                throw new BadRequestException($"Invoice {original.InvoiceNumber} is {original.Status}; only an issued invoice can be credited.");
            if (!string.IsNullOrWhiteSpace(request.CurrencyCode)
                && !string.Equals(request.CurrencyCode.Trim(), original.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                throw new BadRequestException($"Invoice {original.InvoiceNumber} is in {original.CurrencyCode}; a credit memo against it is in the same currency.");
        }

        var taxCodes = await TaxCodesAsync(org, request.Lines, ct);
        var lines = new List<SalesInvoiceLine>();
        var lineNo = 1;

        // REV-3 — per original line: what credit notes (POS returns and memos) have already credited on it.
        var namedLines = request.Lines.Where(l => l.InvoiceLineUuid is { } u && u != Guid.Empty).Select(l => (Guid?)l.InvoiceLineUuid).ToList();
        var creditedOnLine = namedLines.Count == 0 ? new Dictionary<Guid, decimal>() : await _db.SalesInvoiceLines.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.OrganizationId == org && namedLines.Contains(x.OriginalLineUuid)
                     && x.SalesInvoice.Status == SalesInvoiceStatuses.CreditNote && !x.SalesInvoice.IsDelete)
            .GroupBy(x => x.OriginalLineUuid!.Value)
            .Select(g => new { Line = g.Key, Total = g.Sum(x => x.LineTotal) })
            .ToDictionaryAsync(x => x.Line, x => x.Total, ct);
        foreach (var l in request.Lines)
        {
            if (l.Amount <= 0m || decimal.Round(l.Amount, 2) != l.Amount)
                throw new BadRequestException($"Line {lineNo}: {l.Amount} is not an amount above zero with at most two decimals.");
            if (l.Description is { Length: > 300 }) throw new BadRequestException($"Line {lineNo}: the description is longer than 300 characters.");

            SalesInvoiceLine? source = null;
            if (l.InvoiceLineUuid is { } lu && lu != Guid.Empty)
            {
                if (original is null) throw new BadRequestException($"Line {lineNo} names an invoice line, but the memo is not against an invoice.");
                source = original.Lines.FirstOrDefault(x => x.UUID == lu)
                    ?? throw new BadRequestException($"Line {lineNo}: {lu} is not a line of invoice {original.InvoiceNumber}.");
                var soFar = creditedOnLine.GetValueOrDefault(source.UUID) + lines.Where(x => x.OriginalLineUuid == source.UUID).Sum(x => x.LineTotal);
                if (soFar + l.Amount > source.LineTotal)
                    throw new BadRequestException(
                        $"Line {lineNo}: crediting {l.Amount:0.00} on {source.Description} would credit more than the {source.LineTotal:0.00} it was sold for " +
                        $"({soFar:0.00} already credited).");
            }

            var code = l.TaxCodeUuid is { } tu && tu != Guid.Empty ? taxCodes[tu] : null;
            var pct  = code?.RatePercent ?? source?.TaxPercent ?? 0m;
            var tax  = pct <= 0m ? 0m : Math.Round(l.Amount * pct / (100m + pct), 2, MidpointRounding.AwayFromZero);
            lines.Add(new SalesInvoiceLine
            {
                UUID             = Guid.NewGuid(),
                OrganizationId   = org,
                LineNo           = lineNo++,
                VariantUuid      = source?.VariantUuid ?? Guid.Empty,
                OriginalLineUuid = source?.UUID,
                Description      = CreditControlService.Truncate(
                    string.IsNullOrWhiteSpace(l.Description) ? source?.Description ?? "Credit" : l.Description.Trim(), 300),
                Quantity         = 1m,
                UnitPrice        = l.Amount - tax,
                DiscountPercent  = 0m,
                TaxPercent       = pct,
                TaxAmount        = tax,
                TaxCodeUuid      = code?.Uuid ?? (code is null ? source?.TaxCodeUuid : null),
                TaxCode          = code?.Code ?? (code is null ? source?.TaxCode : null),
                LineTotal        = l.Amount
            });
        }

        var total = lines.Sum(l => l.LineTotal);
        var taxTotal = lines.Sum(l => l.TaxAmount ?? 0m);
        if (original is not null && original.ReturnedAmount + total > original.GrandTotal)
            throw new BadRequestException(
                $"Crediting {total:0.00} would take the credit notes on invoice {original.InvoiceNumber} above its total of {original.GrandTotal:0.00} " +
                $"({original.ReturnedAmount:0.00} already credited).");

        var rate = original is null ? await _currency.LockAsync(org, request.CurrencyCode, date, ct) : null;

        Guid noteUuid = Guid.Empty;
        await InCustomerLockAsync(org, request.CustomerUuid, async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Re-read under the customer lock: the balance it is applied against must be the committed one.
                    if (original is not null)
                    {
                        var originalUuid = original.UUID;
                        _db.ChangeTracker.Clear();
                        original = await _db.SalesInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                            .FirstAsync(i => i.OrganizationId == org && i.UUID == originalUuid, ct);
                        if (original.ReturnedAmount + total > original.GrandTotal)
                            throw new BadRequestException(
                                $"Crediting {total:0.00} would take the credit notes on invoice {original.InvoiceNumber} above its total of {original.GrandTotal:0.00} " +
                                $"({original.ReturnedAmount:0.00} already credited).");
                    }
                    var applyNow = original is not null && (request.ApplyToOriginal ?? true)
                                && Open.Contains(original.Status) && original.BalanceDue > 0m;

                    var day = date.ToDateTime(TimeOnly.MinValue);
                    var note = new SalesInvoice
                    {
                        UUID                = Guid.NewGuid(),
                        OrganizationId      = org,
                        TraceId             = original?.TraceId ?? Guid.NewGuid(),
                        InvoiceNumber       = await ArDocumentNumbers.NextAsync(_db, org, ArDocumentNumbers.CreditNotePrefix, day, ct),
                        SaleOrderNumber     = string.Empty,
                        SourceType          = ArDocumentSources.CreditMemo,
                        SourceUuid          = null,
                        SourceNumber        = original?.InvoiceNumber,
                        OriginalInvoiceUuid = original?.UUID,
                        PartnerId           = request.CustomerUuid,
                        PartnerName         = CreditControlService.Truncate(customerName, 200),
                        InvoiceDate         = day,
                        DueDate             = day,
                        Subtotal            = total - taxTotal,
                        TaxAmount           = taxTotal,
                        GrandTotal          = total,
                        BalanceDue          = 0m,
                        CreditedAmount      = 0m,
                        Status              = SalesInvoiceStatuses.CreditNote,
                        CurrencyCode        = original?.CurrencyCode ?? rate?.CurrencyCode ?? request.CurrencyCode?.Trim().ToUpperInvariant() ?? "PKR",
                        CurrencyId          = original?.CurrencyId,
                        ExchangeRate        = original?.ExchangeRate,
                        BaseCurrencyId      = original?.BaseCurrencyId,
                        BaseCurrencyCode    = original?.BaseCurrencyCode,
                        BaseGrandTotal      = original?.ExchangeRate is { } r ? Math.Round(total * r, 2, MidpointRounding.AwayFromZero) : null,
                        ExchangeRateLockedAt = original?.ExchangeRateLockedAt,
                        Notes               = CreditControlService.Truncate(
                            original is null ? $"Credit memo: {reason}" : $"Credit memo for {original.InvoiceNumber}: {reason}", 500),
                        CreatedBy           = caller.UserId,
                        CreatedDate         = now,
                        ModifiedBy          = caller.UserIdOrNull,
                        ModifiedDate        = now
                    };
                    foreach (var l in lines) note.Lines.Add(Clone(l));
                    if (rate is not null) SalesInvoiceService.ApplyRateLock(note, rate, now);
                    _db.SalesInvoices.Add(note);

                    if (original is not null)
                    {
                        original.ReturnedAmount += total;
                        original.ModifiedBy   = caller.UserIdOrNull;
                        original.ModifiedDate = now;
                    }

                    await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                        request.CustomerUuid, CustomerLedgerEntryTypes.CreditNote, CounterSaleReferences.CreditNote, note.UUID, note.InvoiceNumber,
                        Debit: 0m, Credit: total, note.CurrencyCode,
                        CreditControlService.Truncate(original is null
                            ? $"Credit memo {note.InvoiceNumber}: {reason}"
                            : $"Credit memo {note.InvoiceNumber} for invoice {original.InvoiceNumber}: {reason}", 500),
                        day, caller.UserId));

                    if (applyNow)
                        Apply(note, original!, Math.Min(total, original!.BalanceDue), caller, now, null);

                    StampOrganization(org);
                    await _db.SaveChangesAsync(ct);
                    noteUuid = note.UUID;
                    return;
                }
                catch (DbUpdateException) when (attempt < MaxAttempts)
                {
                    _db.ChangeTracker.Clear();
                }
            }
        }, ct);

        if (_postings is not null) await _postings.PublishCreditMemoAsync(noteUuid, ct);
        return await GetAsync(noteUuid, caller, ct);
    }

    // ══ Apply ══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CreditMemoModel> ApplyAsync(Guid creditNoteUuid, ApplyCreditMemoRequest request, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        if (request?.Applications is null || request.Applications.Count == 0)
            throw new BadRequestException("Name at least one invoice to apply the credit to.");
        if (request.Applications.Select(a => a.InvoiceUuid).Distinct().Count() != request.Applications.Count)
            throw new BadRequestException("Each invoice can appear only once.");

        var head = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.UUID == creditNoteUuid && !i.IsDelete)
            .Select(i => new { i.PartnerId, i.InvoiceNumber, i.Status }).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("SalesInvoice", creditNoteUuid);
        if (head.Status != SalesInvoiceStatuses.CreditNote)
            throw new ConflictException($"{head.InvoiceNumber} is not an issued credit note.");

        await InCustomerLockAsync(org, head.PartnerId, async () =>
        {
            var note = await _db.SalesInvoices.IgnoreQueryFilters()
                .FirstAsync(i => i.OrganizationId == org && i.UUID == creditNoteUuid, ct);
            var refunded = await RefundedAsync(org, note.UUID, ct);
            var left = CreditMemoMath.Remaining(note.GrandTotal, note.CreditedAmount, refunded);

            var uuids = request.Applications.Select(a => a.InvoiceUuid).ToList();
            var invoices = await _db.SalesInvoices.IgnoreQueryFilters()
                .Where(i => i.OrganizationId == org && uuids.Contains(i.UUID) && !i.IsDelete).ToListAsync(ct);

            var now = _clock.GetUtcNow().UtcDateTime;
            var total = 0m;
            foreach (var a in request.Applications)
            {
                var invoice = invoices.FirstOrDefault(i => i.UUID == a.InvoiceUuid) ?? throw new NotFoundException("SalesInvoice", a.InvoiceUuid);
                if (a.Amount <= 0m || decimal.Round(a.Amount, 2) != a.Amount)
                    throw new BadRequestException($"{a.Amount} is not an amount above zero with at most two decimals.");
                if (invoice.PartnerId != note.PartnerId)
                    throw new BadRequestException($"Invoice {invoice.InvoiceNumber} belongs to another customer.");
                if (!string.Equals(invoice.CurrencyCode, note.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                    throw new BadRequestException($"{note.InvoiceNumber} is in {note.CurrencyCode}; invoice {invoice.InvoiceNumber} is in {invoice.CurrencyCode}.");
                if (!Open.Contains(invoice.Status) || invoice.BalanceDue < a.Amount)
                    throw new BadRequestException($"Invoice {invoice.InvoiceNumber} has only {Math.Max(0m, Open.Contains(invoice.Status) ? invoice.BalanceDue : 0m):0.00} open.");
                total += a.Amount;
                if (total > left)
                    throw new BadRequestException($"{note.InvoiceNumber} has only {left:0.00} left to apply.");

                Apply(note, invoice, a.Amount, caller, now, null);
            }

            StampOrganization(org);
            await _db.SaveChangesAsync(ct);
        }, ct);

        return await GetAsync(creditNoteUuid, caller, ct);
    }

    /// <summary>Tracks one application (unsaved): rows, both CreditedAmounts, the invoice's balance and status (A38's rule).</summary>
    private void Apply(SalesInvoice note, SalesInvoice invoice, decimal amount, ArApCaller caller, DateTime now, string? notes)
    {
        if (amount <= 0m) return;
        _db.Set<CustomerCreditApplication>().Add(new CustomerCreditApplication
        {
            OrganizationId   = note.OrganizationId,
            CreditNote       = note,
            CreditNoteUuid   = note.UUID,
            CreditNoteNumber = note.InvoiceNumber,
            Invoice          = invoice,
            InvoiceUuid      = invoice.UUID,
            InvoiceNumber    = invoice.InvoiceNumber,
            PartnerUuid      = invoice.PartnerId,
            Amount           = amount,
            Notes            = notes,
            AppliedAt        = now,
            AppliedBy        = caller.UserIdOrNull,
            AppliedByName    = caller.UserName
        });

        note.CreditedAmount   += amount;
        note.ModifiedBy        = caller.UserIdOrNull;
        note.ModifiedDate      = now;
        invoice.CreditedAmount += amount;
        invoice.BalanceDue     -= amount;
        invoice.Status = invoice.BalanceDue <= 0m ? SalesInvoiceStatuses.Paid
                       : invoice.DueDate.Date < now.Date ? SalesInvoiceStatuses.Overdue
                       : invoice.AmountPaid + invoice.CreditedAmount > 0m ? SalesInvoiceStatuses.PartiallyPaid
                       : SalesInvoiceStatuses.Issued;
        invoice.ModifiedBy   = caller.UserIdOrNull;
        invoice.ModifiedDate = now;
    }

    // ══ Read ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CreditMemoModel> GetAsync(Guid creditNoteUuid, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var note = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.OrganizationId == org && i.UUID == creditNoteUuid && !i.IsDelete, ct)
            ?? throw new NotFoundException("SalesInvoice", creditNoteUuid);
        if (note.Status != SalesInvoiceStatuses.CreditNote)
            throw new ConflictException($"{note.InvoiceNumber} is not an issued credit note.");

        var originalNumber = note.OriginalInvoiceUuid is { } o
            ? await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.OrganizationId == org && i.UUID == o).Select(i => i.InvoiceNumber).FirstOrDefaultAsync(ct)
            : null;
        var refunded = await RefundedAsync(org, note.UUID, ct);
        var remaining = CreditMemoMath.Remaining(note.GrandTotal, note.CreditedAmount, refunded);

        return new CreditMemoModel
        {
            Uuid                  = note.UUID,
            CreditNoteNumber      = note.InvoiceNumber,
            SourceType            = note.SourceType,
            CustomerUuid          = note.PartnerId,
            CustomerName          = note.PartnerName,
            OriginalInvoiceUuid   = note.OriginalInvoiceUuid,
            OriginalInvoiceNumber = originalNumber,
            Date                  = DateOnly.FromDateTime(note.InvoiceDate),
            CurrencyCode          = note.CurrencyCode,
            Subtotal              = note.Subtotal,
            TaxAmount             = note.TaxAmount,
            Total                 = note.GrandTotal,
            Applied               = note.CreditedAmount,
            Refunded              = refunded,
            Remaining             = remaining,
            RemainingBase         = note.ExchangeRate is { } r ? Math.Round(remaining * r, 2, MidpointRounding.AwayFromZero) : null,
            Notes                 = note.Notes,
            Lines = note.Lines.OrderBy(l => l.LineNo).Select(l => new CreditMemoLineModel
            {
                LineNo = l.LineNo, Description = l.Description, Amount = l.LineTotal,
                TaxAmount = l.TaxAmount ?? 0m, TaxPercent = l.TaxPercent, TaxCode = l.TaxCode
            }).ToList(),
            Applications = await ApplicationsAsync(org, note.UUID, ct)
        };
    }

    public async Task<List<CreditApplicationModel>> GetApplicationsAsync(Guid creditNoteUuid, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        if (!await _db.SalesInvoices.IgnoreQueryFilters().AnyAsync(
                i => i.OrganizationId == org && i.UUID == creditNoteUuid && !i.IsDelete && i.Status == SalesInvoiceStatuses.CreditNote, ct))
            throw new NotFoundException("SalesInvoice", creditNoteUuid);
        return await ApplicationsAsync(org, creditNoteUuid, ct);
    }

    /// <summary>The customer's credit notes with credit left to apply (A38 POS notes included), oldest first.</summary>
    public async Task<List<CreditMemoModel>> OpenCreditsAsync(Guid customerUuid, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        await CustomerNameAsync(org, customerUuid, ct);
        var refunds = _db.CustomerRefunds.IgnoreQueryFilters().Where(r => r.OrganizationId == org);
        var candidates = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.PartnerId == customerUuid && !i.IsDelete && i.Status == SalesInvoiceStatuses.CreditNote)
            .Select(i => new { i.UUID, i.InvoiceDate, i.Id, i.GrandTotal, i.CreditedAmount,
                Refunded = refunds.Where(r => r.CreditNoteUuid == i.UUID).Sum(r => (decimal?)r.Amount) ?? 0m })
            .ToListAsync(ct);

        var result = new List<CreditMemoModel>();
        foreach (var c in candidates.Where(c => CreditMemoMath.Remaining(c.GrandTotal, c.CreditedAmount, c.Refunded) > 0m)
                                    .OrderBy(c => c.InvoiceDate).ThenBy(c => c.Id))
            result.Add(await GetAsync(c.UUID, caller, ct));
        return result;
    }

    // ══ Helpers ════════════════════════════════════════════════════════════════════════════════════════════════════

    private Task<List<CreditApplicationModel>> ApplicationsAsync(Guid org, Guid noteUuid, CancellationToken ct) =>
        _db.Set<CustomerCreditApplication>().IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && a.CreditNoteUuid == noteUuid)
            .OrderBy(a => a.AppliedAt).ThenBy(a => a.Id)
            .Select(a => new CreditApplicationModel
            {
                Uuid = a.Uuid, CreditNoteUuid = a.CreditNoteUuid, CreditNoteNumber = a.CreditNoteNumber, InvoiceUuid = a.InvoiceUuid,
                InvoiceNumber = a.InvoiceNumber, Amount = a.Amount, AppliedAt = a.AppliedAt, AppliedByName = a.AppliedByName,
                ReversedAt = a.ReversedAt
            })
            .ToListAsync(ct);

    private async Task<decimal> RefundedAsync(Guid org, Guid noteUuid, CancellationToken ct) =>
        await _db.CustomerRefunds.IgnoreQueryFilters()
            .Where(r => r.OrganizationId == org && r.CreditNoteUuid == noteUuid).SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;

    private async Task<Dictionary<Guid, TaxCode>> TaxCodesAsync(Guid org, IEnumerable<CreditMemoLineRequest> lines, CancellationToken ct)
    {
        var ids = lines.Where(l => l.TaxCodeUuid is { } u && u != Guid.Empty).Select(l => l.TaxCodeUuid!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        var codes = await _db.TaxCodes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.OrganizationId == org && ids.Contains(t.Uuid)).ToListAsync(ct);
        foreach (var id in ids)
        {
            var c = codes.FirstOrDefault(t => t.Uuid == id) ?? throw new BadRequestException($"Tax code with key '{id}' was not found.");
            if (c.Usage == TaxCodeUsages.Purchase) throw new BadRequestException($"Tax code {c.Code} is for purchases only.");
        }
        return codes.ToDictionary(c => c.Uuid);
    }

    private async Task<string> CustomerNameAsync(Guid org, Guid customer, CancellationToken ct)
    {
        if (_partners is not null)
        {
            var p = await _partners.GetAsync(org, customer, null, ct) ?? throw new NotFoundException("Business partner", customer);
            if (!p.IsCustomer) throw new BadRequestException($"{p.Name} is not a customer.");
            return p.Name;
        }
        return await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.PartnerId == customer).Select(i => i.PartnerName).FirstOrDefaultAsync(ct)
            ?? "(unknown customer)";
    }

    /// <summary>One credit operation per customer at a time (a transaction-owned app lock on SQL Server; nothing on InMemory).</summary>
    private async Task InCustomerLockAsync(Guid org, Guid customer, Func<Task> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            await work();
            return;
        }

        var resource = $"finance.ar-credit/{org:N}/{customer:N}";

        // Inside a caller's open transaction: take the lock in it and let the caller commit (D-CORE-14).
        if (_db.Database.CurrentTransaction is not null)
        {
            await FinanceAppLock.AcquireAsync(_db, resource, "Transaction", 15000, ct);
            await work();
            return;
        }

        // The API's FinanceDbContext retries on failure (EnableRetryOnFailure): an own transaction must run inside the
        // execution strategy, and a retried attempt starts from a clean change tracker (the work re-reads everything).
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt  = 0;
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await FinanceAppLock.AcquireAsync(_db, resource, "Transaction", 15000, ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }

    private static SalesInvoiceLine Clone(SalesInvoiceLine l) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = l.OrganizationId, LineNo = l.LineNo, VariantUuid = l.VariantUuid,
        OriginalLineUuid = l.OriginalLineUuid, Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
        DiscountPercent = l.DiscountPercent, TaxPercent = l.TaxPercent, TaxAmount = l.TaxAmount, TaxCodeUuid = l.TaxCodeUuid,
        TaxCode = l.TaxCode, LineTotal = l.LineTotal
    };

    private void StampOrganization(Guid org)
    {
        foreach (var e in _db.ChangeTracker.Entries<ITenantScopedEntity>())
            if (e.State == EntityState.Added && e.Entity.OrganizationId == Guid.Empty) e.Entity.OrganizationId = org;
    }

    private static Guid Org(ArApCaller caller) =>
        caller.OrganizationId == Guid.Empty
            ? throw new BadRequestException("Pick an organization first: credit memos are one organization's books.")
            : caller.OrganizationId;
}
