using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.ArAp.Receivables;

/// <summary>A48 AR — the SalesInvoice.SourceType values AR adds (nvarchar(20), no CHECK; API-CONTRACT §3, §4).</summary>
internal static class ArDocumentSources
{
    /// <summary>D-10 / D-AR-4 — a dunning charge: an issued sales invoice of one non-stock line.</summary>
    public const string DunningCharge = "DUNNING_CHARGE";
    /// <summary>D-6 / D-AR-6 — a back-office customer credit memo: a CREDIT_NOTE sales invoice.</summary>
    public const string CreditMemo    = "AR_CREDIT_MEMO";
}

/// <summary>
/// A48 AR — after-commit A44 postings of the AR documents (D-AR-5): a dunning charge as a sales invoice, a credit memo as a
/// credit note, through the financial posting gateway (internal GL and/or QuickBooks, whatever the organization has), with
/// the very builders the sales side uses (<see cref="SalesPostings"/>). Never throws: the document is booked either way.
/// </summary>
internal sealed class ArDocumentPostings
{
    private readonly FinanceDbContext            _db;
    private readonly ILogger<ArDocumentPostings> _log;
    private readonly IQuickBooksGateway?         _gateway;
    private readonly IFinancialPostingGateway?   _posting;

    public ArDocumentPostings(FinanceDbContext db, ILogger<ArDocumentPostings> log,
        IQuickBooksGateway? gateway = null, IFinancialPostingGateway? posting = null)
    {
        _db      = db;
        _log     = log;
        _gateway = gateway;
        _posting = posting;
    }

    public async Task PublishChargeAsync(Guid invoiceUuid, CancellationToken ct = default)
    {
        if (_gateway is null && !PostingSupport.IsActive(_posting)) return;
        try
        {
            var invoice = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.UUID == invoiceUuid, ct);
            if (invoice is null) return;
            await PostingSupport.SendAsync(_posting, _gateway!, SalesPostings.Invoice(invoice), _log, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Dunning charge {InvoiceUuid} was booked but could not be handed to the posting gateway.", invoiceUuid);
        }
    }

    public async Task PublishCreditMemoAsync(Guid creditNoteUuid, CancellationToken ct = default)
    {
        if (_gateway is null && !PostingSupport.IsActive(_posting)) return;
        try
        {
            var note = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.UUID == creditNoteUuid && i.Status == SalesInvoiceStatuses.CreditNote, ct);
            if (note is null) return;

            SalesInvoice? original = null;
            var originalIsInvoice = false;
            if (note.OriginalInvoiceUuid is { } o)
            {
                original = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(i => i.OrganizationId == note.OrganizationId && i.UUID == o, ct);
                originalIsInvoice = original is not null;
                if (original is not null && PosDocuments.IsPos(original))
                {
                    // A POS sale paid at the till went as a sales receipt: a credit memo cannot be applied to it.
                    var shape = (await PosDocuments.ShapeSalesAsync(_db, [original], ct)).GetValueOrDefault(original.Id);
                    originalIsInvoice = shape is not { IsReceipt: true };
                }
            }

            await PostingSupport.SendAsync(_posting, _gateway!, SalesPostings.CreditNote(note, original, [], originalIsInvoice), _log, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Credit memo {CreditNoteUuid} was booked but could not be handed to the posting gateway.", creditNoteUuid);
        }
    }
}

/// <summary>Numbers of the AR documents, the way A38 numbers its counter-sale documents (unique index + retry).</summary>
internal static class ArDocumentNumbers
{
    public const string ChargePrefix     = "SDC";
    public const string CreditNotePrefix = CounterSaleService.CreditNotePrefix;

    public static async Task<string> NextAsync(FinanceDbContext db, Guid org, string prefix, DateTime day, CancellationToken ct)
    {
        var p = $"{prefix}-{day:yyyyMMdd}-";
        var used = await db.SalesInvoices.IgnoreQueryFilters()
            .Where(i => i.OrganizationId == org && i.InvoiceNumber.StartsWith(p)).Select(i => i.InvoiceNumber).ToListAsync(ct);
        used.AddRange(db.ChangeTracker.Entries<SalesInvoice>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.InvoiceNumber));
        var max = used.Where(n => n.StartsWith(p, StringComparison.Ordinal))
            .Select(n => int.TryParse(n[p.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return $"{p}{max + 1:D4}";
    }
}

/// <summary>The currency a new AR document is raised in, and its locked rate (A35 D-12).</summary>
internal sealed class ArDocumentCurrency
{
    private readonly ICurrencyService?   _currency;
    private readonly IOrgCurrencyLookup? _orgCurrencies;

    public ArDocumentCurrency(ICurrencyService? currency = null, IOrgCurrencyLookup? orgCurrencies = null)
    {
        _currency      = currency;
        _orgCurrencies = orgCurrencies;
    }

    /// <summary>
    /// The lock for <paramref name="currencyCode"/> (null = the sale base) on <paramref name="day"/>; null when the host has
    /// no currency service (a test harness) — the document is then raised unlocked in <paramref name="fallbackCode"/>.
    /// </summary>
    public async Task<DocumentRateLock?> LockAsync(Guid org, string? currencyCode, DateOnly day, CancellationToken ct)
    {
        if (_currency is null) return null;
        Guid currencyId;
        if (string.IsNullOrWhiteSpace(currencyCode))
            currencyId = await _currency.GetBaseCurrencyIdAsync(org, TransactionDomain.Sale, ct);
        else
        {
            var found = _orgCurrencies is null ? null : await _orgCurrencies.GetByCodeAsync(org, currencyCode.Trim(), ct);
            if (found is null) throw new BadRequestException($"{currencyCode.Trim().ToUpperInvariant()} is not a currency of this organization.");
            currencyId = found.CurrencyId;
        }
        return await _currency.LockRateAsync(org, currencyId, day, TransactionDomain.Sale, ct);
    }
}

/// <summary>
/// A48 AR (D-10, D-AR-4) — a dunning charge as a customer document through the existing receivables path: an ISSUED sales
/// invoice (source DUNNING_CHARGE, idempotent on the run item's uuid) of one non-stock line in the sale base, booked as an
/// INVOICE debit on the customer ledger (so it ages, is paid and is dunned like any invoice), then posted through A44.
/// No product-ledger entry (nothing is sold).
/// </summary>
internal sealed class DunningChargeWriter
{
    private const int MaxAttempts = 5;

    private readonly FinanceDbContext       _db;
    private readonly ICustomerLedgerService _ledger;
    private readonly ArDocumentCurrency     _currency;
    private readonly ArDocumentPostings?    _postings;
    private readonly TimeProvider           _clock;

    public DunningChargeWriter(FinanceDbContext db, ICustomerLedgerService ledger, ArDocumentCurrency currency,
        ArDocumentPostings? postings = null, TimeProvider? clock = null)
    {
        _db       = db;
        _ledger   = ledger;
        _currency = currency;
        _postings = postings;
        _clock    = clock ?? TimeProvider.System;
    }

    public async Task<(Guid Uuid, string Number)> RaiseAsync(
        Guid org, Guid itemUuid, Guid customer, string customerName, decimal amount, int level, string runNumber,
        string? fallbackCurrency, int? userId, CancellationToken ct)
    {
        if (await FindAsync(org, itemUuid, ct) is { } done) return done;

        var now  = _clock.GetUtcNow().UtcDateTime;
        var day  = now.Date;
        var rate = await _currency.LockAsync(org, null, DateOnly.FromDateTime(day), ct);
        var description = $"Dunning charge, level {level} ({runNumber})";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var invoice = new SalesInvoice
                {
                    UUID            = Guid.NewGuid(),
                    OrganizationId  = org,
                    TraceId         = itemUuid,
                    InvoiceNumber   = await ArDocumentNumbers.NextAsync(_db, org, ArDocumentNumbers.ChargePrefix, day, ct),
                    SaleOrderNumber = string.Empty,
                    SourceType      = ArDocumentSources.DunningCharge,
                    SourceUuid      = itemUuid,
                    SourceNumber    = CreditControlService.Truncate(runNumber, 50),
                    PartnerId       = customer,
                    PartnerName     = CreditControlService.Truncate(customerName, 200),
                    InvoiceDate     = day,
                    DueDate         = day,
                    Subtotal        = amount,
                    GrandTotal      = amount,
                    BalanceDue      = amount,
                    Status          = SalesInvoiceStatuses.Issued,
                    CurrencyCode    = rate?.CurrencyCode ?? fallbackCurrency ?? "PKR",
                    Notes           = description,
                    CreatedBy       = userId ?? 0,
                    CreatedDate     = now,
                    ModifiedBy      = userId,
                    ModifiedDate    = now
                };
                invoice.Lines.Add(new SalesInvoiceLine
                {
                    UUID = Guid.NewGuid(), OrganizationId = org, LineNo = 1, VariantUuid = Guid.Empty,
                    Description = description, Quantity = 1m, UnitPrice = amount, LineTotal = amount
                });
                if (rate is not null) SalesInvoiceService.ApplyRateLock(invoice, rate, now);
                _db.SalesInvoices.Add(invoice);

                await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                    customer, CustomerLedgerEntryTypes.Invoice, "SalesInvoice", invoice.UUID, invoice.InvoiceNumber,
                    Debit: amount, Credit: 0m, invoice.CurrencyCode, CreditControlService.Truncate($"{description}: {invoice.InvoiceNumber}", 500),
                    now, userId ?? 0));
                foreach (var e in _db.ChangeTracker.Entries<ITenantScopedEntity>())
                    if (e.State == EntityState.Added && e.Entity.OrganizationId == Guid.Empty) e.Entity.OrganizationId = org;

                await _db.SaveChangesAsync(ct);
                if (_postings is not null) await _postings.PublishChargeAsync(invoice.UUID, ct);
                return (invoice.UUID, invoice.InvoiceNumber);
            }
            catch (DbUpdateException)
            {
                DetachAdded();
                if (await FindAsync(org, itemUuid, ct) is { } raced) return raced;
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    private async Task<(Guid, string)?> FindAsync(Guid org, Guid itemUuid, CancellationToken ct)
    {
        var hit = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.SourceUuid == itemUuid)
            .Select(i => new { i.UUID, i.InvoiceNumber }).FirstOrDefaultAsync(ct);
        return hit is null ? null : (hit.UUID, hit.InvoiceNumber);
    }

    private void DetachAdded()
    {
        foreach (var e in _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            e.State = EntityState.Detached;
    }
}
