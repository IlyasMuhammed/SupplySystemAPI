using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A38 (FIN) — a customer's payment terms in days (suppliers.BusinessPartners.PaymentTermsDays), for the due date of a POS
/// credit sale. Implemented in Suppliers (which reads Finance, so the seam lives here); optional — without it, or for 0 /
/// unknown, a credit sale falls due <see cref="SalesInvoiceService.DefaultPaymentTermDays"/> days after the sale.
/// </summary>
public interface ICustomerPaymentTermsLookup
{
    Task<int?> GetPaymentTermsDaysAsync(Guid organizationId, Guid partnerId, CancellationToken ct = default);
}

/// <summary>
/// A38 (FIN, D-8/D-9/D-10) — <see cref="ICounterSaleService"/>: a POS sale is an issued <see cref="SalesInvoice"/> (source POS,
/// no sale order / delivery) issued through the sales invoice service's own issue bookings (customer INVOICE debit, product
/// ledger SALE at WAC — allowed below zero, QuickBooks after commit), with one <see cref="CustomerPayment"/> per non-ON_ACCOUNT
/// tender allocated to it. A return is a customer credit note — a <see cref="SalesInvoice"/> row with status CREDIT_NOTE that
/// points at the original invoice (<see cref="SalesInvoice.OriginalInvoiceUuid"/>) — with a CREDIT_NOTE credit, RETURN_IN at the
/// original cost and refunds (<see cref="CustomerRefund"/>, REFUND debit) or, for ON_ACCOUNT, the credit applied to the original
/// invoice's balance. A void reverses the sale's POS payments and credits the whole invoice.
/// <para>
/// Every call is one <c>SaveChanges</c> (one transaction) and idempotent on its SourceUuid (unique per organization on the
/// invoice table — a race loses on the index and returns the winner's result). Every row is stamped with the request's
/// organization explicitly, so a Hangfire retry or a super admin's request books into the right organization.
/// </para>
/// </summary>
internal sealed class CounterSaleService : ICounterSaleService
{
    private const int MaxAttempts = 5;
    private const int MaxReasonLength = 500;

    internal const string InvoicePrefix    = "SINV";
    internal const string CreditNotePrefix = "SCN";
    internal const string PaymentPrefix    = "CPAY";
    internal const string RefundPrefix     = "CRF";

    private static readonly string[] Creditable =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Paid, SalesInvoiceStatuses.Overdue];

    private static readonly Dictionary<string, string> MethodMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [PosPaymentMethod.Cash]         = CustomerPaymentMethods.Cash,
        [PosPaymentMethod.Card]         = CustomerPaymentMethods.Card,
        [PosPaymentMethod.Mobile]       = CustomerPaymentMethods.Mobile,
        [PosPaymentMethod.BankTransfer] = CustomerPaymentMethods.BankTransfer,
        [PosPaymentMethod.Cheque]       = CustomerPaymentMethods.Cheque,
        // A39 (XFIN, D-3/D-6) — no cash moves: a payment / refund of that method, allocated and ledgered like any other.
        [PosPaymentMethod.StoreCredit]  = CustomerPaymentMethods.StoreCredit,
        [PosPaymentMethod.Exchange]     = CustomerPaymentMethods.Exchange,
    };

    private readonly FinanceDbContext            _db;
    private readonly SalesInvoiceService         _invoices;
    private readonly CustomerPaymentService      _payments;
    private readonly ICustomerLedgerService      _ledger;
    private readonly IProductLedgerWriter        _productLedger;
    private readonly ISupplierNameLookupService  _partnerNames;
    private readonly ICurrencyService            _currency;
    private readonly ILogger<CounterSaleService> _log;
    private readonly TimeProvider                _clock;
    private readonly ICustomerPaymentTermsLookup? _terms;
    private readonly PosDocumentPostings?        _postings;

    /// <param name="postings">
    /// A44 (D-21..D-23) — after each commit: the sale as a sales receipt or an invoice with its tenders as payments, a
    /// return as a credit memo / refund receipt, a void as voids, through the financial posting gateway. Without it (a
    /// hand-built harness) a sale is published as the invoice it was before A44, and returns and voids send nothing.
    /// </param>
    public CounterSaleService(
        FinanceDbContext db, SalesInvoiceService invoices, CustomerPaymentService payments,
        ICustomerLedgerService ledger, IProductLedgerWriter productLedger, ISupplierNameLookupService partnerNames,
        ICurrencyService currency, ILogger<CounterSaleService> log, TimeProvider? clock = null,
        ICustomerPaymentTermsLookup? terms = null, PosDocumentPostings? postings = null)
    {
        _postings      = postings;
        _db            = db;
        _invoices      = invoices;
        _payments      = payments;
        _ledger        = ledger;
        _productLedger = productLedger;
        _partnerNames  = partnerNames;
        _currency      = currency;
        _log           = log;
        _clock         = clock ?? TimeProvider.System;
        _terms         = terms;
    }

    // ══ Sale ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CounterSaleResult> PostSaleAsync(CounterSaleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = request.OrganizationId;
        EnsureOrganization(org);
        await ReleaseFinishedTransactionAsync(ct);
        var grand = ValidateSale(request);

        var tenderUuids = request.Tenders.Select(t => t.SourceUuid).ToList();
        if (await FindSaleAsync(org, request.SourceUuid, tenderUuids, ct) is { } done) return done;

        var saleDate    = request.SaleDateUtc;
        var day         = saleDate.Date;
        var rateLock    = await _currency.LockRateAsync(org, await _currency.GetBaseCurrencyIdAsync(org, TransactionDomain.Sale, ct), DateOnly.FromDateTime(day), TransactionDomain.Sale, ct);
        var partnerName = await PartnerNameAsync(org, request.CustomerPartnerUuid);
        var onAccount   = request.Tenders.Where(IsOnAccount).Sum(t => t.Amount);
        var dueDate     = onAccount > 0m ? day.AddDays(await TermsAsync(org, request.CustomerPartnerUuid, ct)) : day;

        var tax      = request.Lines.Sum(l => l.TaxAmount);
        var discount = request.Lines.Sum(l => l.DiscountAmount);

        for (var attempt = 1; ; attempt++)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            try
            {
                var invoice = new SalesInvoice
                {
                    UUID            = Guid.NewGuid(),
                    OrganizationId  = org,
                    TraceId         = request.SourceUuid,
                    InvoiceNumber   = await NextInvoiceNumberAsync(org, InvoicePrefix, day, ct),
                    SaleOrderUuid   = null,
                    SaleOrderNumber = string.Empty,
                    SourceType      = SalesInvoiceSources.Pos,
                    SourceUuid      = request.SourceUuid,
                    SourceNumber    = request.SourceNumber.Trim(),
                    PartnerId       = request.CustomerPartnerUuid,
                    PartnerName     = partnerName,
                    InvoiceDate     = day,
                    DueDate         = dueDate,
                    // Subtotal − Discount + Tax = GrandTotal, and Subtotal − Discount = Σ net (the revenue the reports read).
                    Subtotal        = grand - tax + discount,
                    DiscountAmount  = discount,
                    TaxAmount       = tax,
                    GrandTotal      = grand,
                    AmountPaid      = 0m,
                    BalanceDue      = grand,
                    Status          = SalesInvoiceStatuses.Draft,
                    CurrencyCode    = rateLock.CurrencyCode,
                    Notes           = Truncate(request.Notes is null ? $"POS sale {request.SourceNumber.Trim()}" : request.Notes.Trim(), 500),
                    CreatedBy       = request.UserId,
                    CreatedDate     = now
                };

                var lineNo = 1;
                foreach (var l in request.Lines)
                    invoice.Lines.Add(new SalesInvoiceLine
                    {
                        UUID            = Guid.NewGuid(),
                        OrganizationId  = org,
                        LineNo          = lineNo++,
                        VariantUuid     = l.VariantUuid,
                        SourceLineUuid  = l.SourceLineUuid,
                        Description     = Truncate(string.IsNullOrWhiteSpace(l.Description) ? "Item" : l.Description.Trim(), 300),
                        Quantity        = l.Quantity,
                        UnitPrice       = l.UnitPrice,
                        DiscountPercent = 0m,
                        DiscountAmount  = l.DiscountAmount,
                        TaxPercent      = l.TaxPercent,
                        TaxAmount       = l.TaxAmount,
                        TaxCodeUuid     = l.TaxCodeUuid,
                        TaxCode         = l.TaxCode is null ? null : Truncate(l.TaxCode.Trim(), 20),
                        LineTotal       = l.LineTotal
                    });

                _db.SalesInvoices.Add(invoice);
                SalesInvoiceService.ApplyRateLock(invoice, rateLock, now);

                // The SAME issue the back office does: ISSUED, INVOICE debit, SALE at WAC (D-9: may run below zero).
                await _invoices.TrackIssueBookingsAsync(invoice, now, saleDate, request.UserId, [], allowNegativeStock: true);

                var payments = new List<CustomerPayment>();
                var nextPayment = await HighestAsync(
                    _db.CustomerPayments.IgnoreQueryFilters().Where(p => p.OrganizationId == org).Select(p => p.PaymentNumber),
                    Prefix(PaymentPrefix, day), ct);

                foreach (var tender in request.Tenders.Where(t => !IsOnAccount(t)))
                {
                    var method  = MethodMap[tender.Method.Trim()];
                    var payment = new CustomerPayment
                    {
                        UUID               = Guid.NewGuid(),
                        OrganizationId     = org,
                        PartnerId          = request.CustomerPartnerUuid,
                        PartnerName        = partnerName,
                        PaymentNumber      = $"{Prefix(PaymentPrefix, day)}{++nextPayment:D4}",
                        PaymentDate        = day,
                        Amount             = tender.Amount,
                        PaymentMethod      = method,
                        ChequeNumber       = method == CustomerPaymentMethods.Cheque
                            ? Truncate(Clean(tender.Reference) ?? request.SourceNumber.Trim(), 30) : null,
                        BankReference      = method == CustomerPaymentMethods.Cheque ? null : Clean(tender.Reference) is { } r ? Truncate(r, 100) : null,
                        CurrencyCode       = invoice.CurrencyCode,
                        CurrencyId         = rateLock.CurrencyId,
                        ExchangeRate       = rateLock.Rate,
                        BaseCurrencyId     = rateLock.BaseCurrencyId,
                        AmountBase         = rateLock.ToBase(tender.Amount),
                        ExchangeDifference = 0m,
                        Status             = CustomerPaymentStatuses.Received,
                        SourceUuid         = tender.SourceUuid,
                        Notes              = $"POS sale {request.SourceNumber.Trim()}",
                        CreatedBy          = request.UserId,
                        CreatedDate        = now
                    };

                    // The same allocation the back office makes: invoice paid down, PARTIALLY_PAID / PAID.
                    foreach (var allocation in CustomerPaymentService.Apply(payment, [(invoice, tender.Amount)], now, request.UserId))
                    {
                        allocation.OrganizationId     = org;
                        allocation.ExchangeDifference = 0m; // sale base, rate 1: nothing realized
                    }

                    _db.CustomerPayments.Add(payment);
                    await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                        payment.PartnerId, CustomerLedgerEntryTypes.Payment, "CustomerPayment", payment.UUID, payment.PaymentNumber,
                        Debit: 0m, Credit: payment.Amount, payment.CurrencyCode,
                        CustomerPaymentService.Narrate(payment, [invoice.InvoiceNumber]), saleDate, request.UserId));
                    payments.Add(payment);
                }

                StampOrganization(org);
                await _db.SaveChangesAsync(ct);

                // A44 (D-21) — the tenders are committed with the sale, so its shape (receipt or invoice) is final here.
                if (_postings is not null) await _postings.PublishSaleAsync(invoice.UUID, ct);
                else await _invoices.PublishIssuedAsync(invoice);
                return new CounterSaleResult(invoice.UUID, invoice.InvoiceNumber, invoice.GrandTotal, [.. payments.Select(p => p.UUID)]);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                if (ex is not DbUpdateException) throw;

                // Lost a race: on the source uuid (somebody posted this very sale) — return theirs — or on a number / ledger
                // sequence — build it again from what is now committed.
                if (await FindSaleAsync(org, request.SourceUuid, tenderUuids, ct) is { } raced) return raced;
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    private async Task<CounterSaleResult?> FindSaleAsync(Guid org, Guid sourceUuid, IReadOnlyList<Guid> tenderUuids, CancellationToken ct)
    {
        var found = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.SourceUuid == sourceUuid)
            .Select(i => new { i.UUID, i.InvoiceNumber, i.GrandTotal, i.Status })
            .FirstOrDefaultAsync(ct);
        if (found is null) return null;

        if (found.Status == SalesInvoiceStatuses.CreditNote)
            throw new BadRequestException($"POS document {sourceUuid} was already posted as credit note {found.InvoiceNumber}, not as a sale.");

        var payments = tenderUuids.Count == 0 ? [] : await _db.CustomerPayments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && p.SourceUuid != null && tenderUuids.Contains(p.SourceUuid.Value))
            .OrderBy(p => p.Id).Select(p => p.UUID).ToListAsync(ct);

        return new CounterSaleResult(found.UUID, found.InvoiceNumber, found.GrandTotal, payments);
    }

    private static decimal ValidateSale(CounterSaleRequest r)
    {
        RequireSource(r.SourceUuid, r.SourceNumber);
        if (r.CustomerPartnerUuid == Guid.Empty) throw new BadRequestException("A POS sale needs a customer (the walk-in customer for an anonymous sale).");
        if (r.Lines is null || r.Lines.Count == 0) throw new BadRequestException("A POS sale needs at least one line.");
        if (r.Notes is { Length: > 500 }) throw new BadRequestException("The notes are longer than 500 characters.");
        if (r.Lines.Select(l => l.SourceLineUuid).Distinct().Count() != r.Lines.Count || r.Lines.Any(l => l.SourceLineUuid == Guid.Empty))
            throw new BadRequestException("Every POS line needs its own line id.");

        foreach (var l in r.Lines)
        {
            if (l.VariantUuid == Guid.Empty) throw new BadRequestException("A POS line has no product variant.");
            Quantity(l.Quantity);
            Money(l.UnitPrice, "unit price"); Money(l.DiscountAmount, "discount"); Money(l.NetAmount, "net amount");
            Money(l.TaxAmount, "tax amount"); Money(l.LineTotal, "line total");
            if (l.TaxPercent is < 0m or > 100m || decimal.Round(l.TaxPercent, 2) != l.TaxPercent)
                throw new BadRequestException($"A tax rate of {l.TaxPercent}% is not a percentage with at most two decimals.");
            if (l.LineTotal != l.NetAmount + l.TaxAmount)
                throw new BadRequestException(
                    $"Line {l.Description}: the line total {l.LineTotal:0.00} is not its net {l.NetAmount:0.00} plus its tax {l.TaxAmount:0.00}.");
        }

        var grand = r.Lines.Sum(l => l.LineTotal);
        if (grand <= 0m) throw new BadRequestException("A POS sale for nothing has no receivable to book.");

        ValidateTenders(r.Tenders, grand, "The tenders", "the sale's total");
        return grand;
    }

    // ══ Return ═════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CounterReturnResult> PostReturnAsync(CounterReturnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = request.OrganizationId;
        EnsureOrganization(org);
        await ReleaseFinishedTransactionAsync(ct);
        RequireSource(request.SourceUuid, request.SourceNumber);
        var reason = Reason(request.Reason);
        if (request.OriginalSalesInvoiceUuid == Guid.Empty) throw new BadRequestException("A POS return names the sale's invoice.");
        if (request.Lines is null || request.Lines.Count == 0) throw new BadRequestException("A POS return needs at least one line.");
        if (request.Lines.Select(l => l.SourceLineUuid).Distinct().Count() != request.Lines.Count || request.Lines.Any(l => l.SourceLineUuid == Guid.Empty))
            throw new BadRequestException("Every POS return line needs its own line id.");
        foreach (var l in request.Lines)
        {
            Quantity(l.Quantity);
            Money(l.NetAmount, "net amount"); Money(l.TaxAmount, "tax amount"); Money(l.LineTotal, "line total");
            if (l.LineTotal != l.NetAmount + l.TaxAmount)
                throw new BadRequestException($"A return line's total {l.LineTotal:0.00} is not its net {l.NetAmount:0.00} plus its tax {l.TaxAmount:0.00}.");
        }

        var total = request.Lines.Sum(l => l.LineTotal);
        if (total <= 0m) throw new BadRequestException("A return of nothing has nothing to credit.");
        ValidateTenders(request.Refunds, total, "The refunds", "the return's total");

        var refundUuids = request.Refunds.Select(t => t.SourceUuid).ToList();
        if (await FindCreditNoteAsync(org, request.SourceUuid, refundUuids, ct) is { } done) return done;

        for (var attempt = 1; ; attempt++)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            try
            {
                var original = await LoadCreditableAsync(org, request.OriginalSalesInvoiceUuid, ct);
                var already  = await CreditedSoFarAsync(org, original.UUID, ct);

                var lines = new List<(SalesInvoiceLine Original, decimal Qty, decimal Net, decimal Tax, decimal Total, Guid SourceLine)>();
                foreach (var l in request.Lines)
                {
                    var line = original.Lines.FirstOrDefault(x => x.SourceLineUuid == l.OriginalSourceLineUuid)
                        ?? throw new BadRequestException($"The returned line {l.OriginalSourceLineUuid} is not a line of invoice {original.InvoiceNumber}.");

                    var (qtySoFar, totalSoFar) = already.GetValueOrDefault(line.UUID);
                    var qtyNow   = lines.Where(x => x.Original.UUID == line.UUID).Sum(x => x.Qty);
                    var totalNow = lines.Where(x => x.Original.UUID == line.UUID).Sum(x => x.Total);
                    if (qtySoFar + qtyNow + l.Quantity > line.Quantity)
                        throw new BadRequestException("Return quantity cannot exceed the original quantity.");
                    if (totalSoFar + totalNow + l.LineTotal > line.LineTotal)
                        throw new BadRequestException(
                            $"Returning {l.LineTotal:0.00} of {line.Description} would credit more than the {line.LineTotal:0.00} it was sold for.");

                    lines.Add((line, l.Quantity, l.NetAmount, l.TaxAmount, l.LineTotal, l.SourceLineUuid));
                }

                var onAccount = request.Refunds.Where(IsOnAccount).Sum(t => t.Amount);
                // ON_ACCOUNT takes the credit off the sale's open balance; what the balance cannot absorb stays on the
                // customer's account as credit (the ledger has it; the invoice cannot go below zero).
                var applied = Math.Min(onAccount, Math.Max(0m, original.BalanceDue));

                var (creditNote, creditEntry) = await TrackCreditNoteAsync(
                    org, original, lines, request.SourceUuid, request.SourceNumber.Trim(), reason, request.ReturnDateUtc, applied, now, request.UserId, ct);

                var refunds = new List<CustomerRefund>();
                var nextRefund = await HighestAsync(
                    _db.CustomerRefunds.IgnoreQueryFilters().Where(x => x.OrganizationId == org).Select(x => x.RefundNumber),
                    Prefix(RefundPrefix, request.ReturnDateUtc.Date), ct);

                foreach (var tender in request.Refunds.Where(t => !IsOnAccount(t)))
                {
                    var refund = new CustomerRefund
                    {
                        UUID           = Guid.NewGuid(),
                        OrganizationId = org,
                        RefundNumber   = $"{Prefix(RefundPrefix, request.ReturnDateUtc.Date)}{++nextRefund:D4}",
                        PartnerId      = original.PartnerId,
                        CreditNoteUuid = creditNote.UUID,
                        RefundDate     = request.ReturnDateUtc.Date,
                        Amount         = tender.Amount,
                        Method         = MethodMap[tender.Method.Trim()],
                        Reference      = Clean(tender.Reference) is { } r ? Truncate(r, 100) : null,
                        CurrencyCode   = original.CurrencyCode,
                        SourceUuid     = tender.SourceUuid,
                        Notes          = Truncate($"POS return {request.SourceNumber.Trim()}: {reason}", 500),
                        CreatedBy      = request.UserId,
                        CreatedDate    = now
                    };
                    _db.CustomerRefunds.Add(refund);

                    // §10 REFUND is a debit: the credit note took the amount off what the customer owes; paying it out puts it back.
                    await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                        original.PartnerId, CustomerLedgerEntryTypes.Refund, CounterSaleReferences.Refund, refund.UUID, refund.RefundNumber,
                        Debit: refund.Amount, Credit: 0m, original.CurrencyCode,
                        Truncate($"Refund {refund.RefundNumber} by {refund.Method} for credit note {creditNote.InvoiceNumber}", 500),
                        request.ReturnDateUtc, request.UserId));
                    refunds.Add(refund);
                }

                StampOrganization(org);
                await _db.SaveChangesAsync(ct);
                _ = creditEntry;

                // A44 (D-23) — the refunds are committed with the credit note: refund receipt or credit memo is final here.
                if (_postings is not null) await _postings.PublishReturnAsync(creditNote.UUID, ct);

                return new CounterReturnResult(creditNote.UUID, creditNote.InvoiceNumber, creditNote.GrandTotal, [.. refunds.Select(x => x.UUID)]);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                if (ex is not DbUpdateException) throw;
                if (await FindCreditNoteAsync(org, request.SourceUuid, refundUuids, ct) is { } raced) return raced;
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    // ══ Void ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CounterReturnResult> VoidSaleAsync(
        Guid organizationId, Guid salesInvoiceUuid, Guid sourceUuid, string reason, int userId, CancellationToken ct = default)
    {
        var org = organizationId;
        EnsureOrganization(org);
        await ReleaseFinishedTransactionAsync(ct);
        if (sourceUuid == Guid.Empty) throw new BadRequestException("A void needs its own id (the idempotency key).");
        if (salesInvoiceUuid == Guid.Empty) throw new BadRequestException("A void names the sale's invoice.");
        var why = Reason(reason);

        if (await FindCreditNoteAsync(org, sourceUuid, [], ct) is { } done) return done;

        for (var attempt = 1; ; attempt++)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            try
            {
                var invoice = await LoadCreditableAsync(org, salesInvoiceUuid, ct);

                if (invoice.ReturnedAmount > 0m || await _db.SalesInvoices.IgnoreQueryFilters()
                        .AnyAsync(i => i.OrganizationId == org && i.OriginalInvoiceUuid == invoice.UUID && !i.IsDelete, ct))
                    throw new BadRequestException(
                        $"Sale {invoice.SourceNumber} ({invoice.InvoiceNumber}) already has a return; it can no longer be voided.");

                var payments = await _db.CustomerPayments.IgnoreQueryFilters()
                    .Include(p => p.Allocations).ThenInclude(a => a.SalesInvoice)
                    .Where(p => p.OrganizationId == org && p.Status == CustomerPaymentStatuses.Received
                             && p.Allocations.Any(a => a.SalesInvoiceId == invoice.Id))
                    .OrderBy(p => p.Id)
                    .ToListAsync(ct);

                if (payments.FirstOrDefault(p => p.SourceUuid is null) is { } foreign)
                    throw new BadRequestException(
                        $"Payment {foreign.PaymentNumber} was recorded in the back office against {invoice.InvoiceNumber}; reverse or reallocate it there before voiding the sale.");
                if (payments.FirstOrDefault(p => p.Allocations.Any(a => a.SalesInvoiceId != invoice.Id)) is { } shared)
                    throw new BadRequestException($"Payment {shared.PaymentNumber} is also applied to other invoices; it cannot be reversed by this void.");

                // A44 (D-22, REV-9) — what the sale was posted as, read before the reversals remove its tender allocations,
                // with the very rule the sale was posted by.
                var shape = _postings is null ? null : (await PosDocuments.ShapeSalesAsync(_db, [invoice], ct)).GetValueOrDefault(invoice.Id);

                // The money is handed back: each POS payment is reversed (REVERSED, allocations removed, PAYMENT debit) …
                foreach (var payment in payments)
                    await _payments.TrackReversalAsync(payment, Truncate($"POS void of {invoice.SourceNumber}: {why}", 200), now, userId);

                // … and the whole sale is credited, applied to the balance the reversals reopened.
                var lines = invoice.Lines.OrderBy(l => l.LineNo)
                    .Select(l => (l, l.Quantity, l.LineTotal - (l.TaxAmount ?? 0m), l.TaxAmount ?? 0m, l.LineTotal, Guid.NewGuid()))
                    .ToList();
                var applied = Math.Min(invoice.GrandTotal, Math.Max(0m, invoice.BalanceDue));

                var (creditNote, _) = await TrackCreditNoteAsync(
                    org, invoice, lines, sourceUuid, Truncate($"{PosDocuments.VoidPrefix}{invoice.SourceNumber}", 50), why, now, applied, now, userId, ct);

                StampOrganization(org);
                await _db.SaveChangesAsync(ct);

                if (_postings is not null && shape is not null)
                    await _postings.PublishVoidAsync(invoice.UUID, shape.IsReceipt, shape.Tenders, Truncate($"POS void of {invoice.SourceNumber}: {why}", 200), userId, now, ct);

                return new CounterReturnResult(creditNote.UUID, creditNote.InvoiceNumber, creditNote.GrandTotal, []);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                if (ex is not DbUpdateException) throw;
                if (await FindCreditNoteAsync(org, sourceUuid, [], ct) is { } raced) return raced;
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    // ══ Credit notes ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds and tracks the credit note (a CREDIT_NOTE sales_invoices row, balance 0: it is used up in full by
    /// <paramref name="applied"/> and the refunds), its CREDIT_NOTE ledger credit, RETURN_IN at the original sale's unit cost
    /// per line, and moves the original invoice: ReturnedAmount += total, CreditedAmount += applied, BalanceDue −= applied, status.
    /// </summary>
    private async Task<(SalesInvoice CreditNote, CustomerLedgerEntry Entry)> TrackCreditNoteAsync(
        Guid org, SalesInvoice original,
        IReadOnlyList<(SalesInvoiceLine Original, decimal Qty, decimal Net, decimal Tax, decimal Total, Guid SourceLine)> lines,
        Guid sourceUuid, string sourceNumber, string reason, DateTime date, decimal applied, DateTime now, int userId, CancellationToken ct)
    {
        var total = lines.Sum(l => l.Total);
        var tax   = lines.Sum(l => l.Tax);
        var day   = date.Date;

        var creditNote = new SalesInvoice
        {
            UUID                = Guid.NewGuid(),
            OrganizationId      = org,
            TraceId             = original.TraceId,
            InvoiceNumber       = await NextInvoiceNumberAsync(org, CreditNotePrefix, day, ct),
            SaleOrderUuid       = null,
            SaleOrderNumber     = string.Empty,
            SourceType          = original.SourceType,
            SourceUuid          = sourceUuid,
            SourceNumber        = sourceNumber,
            OriginalInvoiceUuid = original.UUID,
            PartnerId           = original.PartnerId,
            PartnerName         = original.PartnerName,
            InvoiceDate         = day,
            DueDate             = day,
            Subtotal            = total - tax,
            DiscountAmount      = 0m,
            TaxAmount           = tax,
            GrandTotal          = total,
            AmountPaid          = 0m,
            BalanceDue          = 0m,
            CreditedAmount      = applied,
            Status              = SalesInvoiceStatuses.CreditNote,
            CurrencyCode        = original.CurrencyCode,
            CurrencyId          = original.CurrencyId,
            ExchangeRate        = original.ExchangeRate,
            BaseCurrencyId      = original.BaseCurrencyId,
            BaseCurrencyCode    = original.BaseCurrencyCode,
            BaseGrandTotal      = original.ExchangeRate is { } rate ? Math.Round(total * rate, 4, MidpointRounding.AwayFromZero) : null,
            ExchangeRateLockedAt = original.ExchangeRateLockedAt,
            Notes               = Truncate($"Credit note for {original.InvoiceNumber} ({sourceNumber}): {reason}", 500),
            CreatedBy           = userId,
            CreatedDate         = now,
            ModifiedBy          = userId,
            ModifiedDate        = now
        };

        var lineNo = 1;
        foreach (var l in lines)
            creditNote.Lines.Add(new SalesInvoiceLine
            {
                UUID             = Guid.NewGuid(),
                OrganizationId   = org,
                LineNo           = lineNo++,
                VariantUuid      = l.Original.VariantUuid,
                SourceLineUuid   = l.SourceLine,
                OriginalLineUuid = l.Original.UUID,
                Description      = l.Original.Description,
                Quantity         = l.Qty,
                UnitPrice        = l.Original.UnitPrice,
                DiscountPercent  = 0m,
                DiscountAmount   = null,
                TaxPercent       = l.Original.TaxPercent,
                TaxAmount        = l.Tax,
                TaxCodeUuid      = l.Original.TaxCodeUuid,
                TaxCode          = l.Original.TaxCode,
                LineTotal        = l.Total
            });

        _db.SalesInvoices.Add(creditNote);

        // The original invoice: what came back, what of it reduced the balance, and a status that reads right for aging.
        var today = _clock.GetUtcNow().UtcDateTime.Date;
        original.ReturnedAmount += total;
        original.CreditedAmount += applied;
        original.BalanceDue     -= applied;
        original.Status = original.BalanceDue <= 0m ? SalesInvoiceStatuses.Paid
                        : original.DueDate.Date < today ? SalesInvoiceStatuses.Overdue
                        : original.AmountPaid + original.CreditedAmount > 0m ? SalesInvoiceStatuses.PartiallyPaid
                        : SalesInvoiceStatuses.Issued;
        original.ModifiedBy   = userId;
        original.ModifiedDate = now;

        var entry = await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
            original.PartnerId, CustomerLedgerEntryTypes.CreditNote, CounterSaleReferences.CreditNote, creditNote.UUID, creditNote.InvoiceNumber,
            Debit: 0m, Credit: total, original.CurrencyCode,
            Truncate($"Credit note {creditNote.InvoiceNumber} for invoice {original.InvoiceNumber} ({sourceNumber}): {reason}", 500),
            date, userId));

        // RETURN_IN at what the goods went out at on this invoice (its SALE entries), so the stock value is restored, not
        // re-costed. A variant the sale booked no cost for (an invoice from before the product ledger) takes nothing back.
        var costs = await _db.ProductLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org && e.ReferenceType == "SalesInvoice" && e.ReferenceId == original.UUID
                     && e.EntryType == ProductLedgerEntryTypes.Sale && e.Direction == ProductLedgerDirections.Out)
            .GroupBy(e => new { e.VariantUuid, e.ProductUuid })
            .Select(g => new { g.Key.VariantUuid, g.Key.ProductUuid, Qty = g.Sum(e => e.Quantity), Cost = g.Sum(e => e.TotalCost) })
            .ToListAsync(ct);

        foreach (var l in lines)
        {
            var sale = costs.FirstOrDefault(c => c.VariantUuid == l.Original.VariantUuid);
            if (sale is null || sale.Qty <= 0m) continue;

            var unit = Math.Max(0m, Math.Round(sale.Cost / sale.Qty, 4, MidpointRounding.AwayFromZero));
            await _productLedger.TrackEntryAsync(new ProductLedgerPosting(
                l.Original.VariantUuid, ProductLedgerEntryTypes.ReturnIn, ProductLedgerDirections.In, l.Qty, unit,
                CounterSaleReferences.CreditNote, creditNote.UUID, creditNote.InvoiceNumber, userId,
                ProductUuid: sale.ProductUuid, PartnerId: original.PartnerId,
                Narration: Truncate($"Credit note {creditNote.InvoiceNumber} ({sourceNumber}): {l.Qty:0.####} back at {unit:0.####}", 500),
                EntryDate: date));
        }

        return (creditNote, entry);
    }

    private async Task<SalesInvoice> LoadCreditableAsync(Guid org, Guid invoiceUuid, CancellationToken ct)
    {
        var invoice = await _db.SalesInvoices.IgnoreQueryFilters().Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.OrganizationId == org && i.UUID == invoiceUuid && !i.IsDelete, ct)
            ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

        if (invoice.SourceType != SalesInvoiceSources.Pos)
            throw new BadRequestException($"Invoice {invoice.InvoiceNumber} is not a POS sale; it cannot be returned or voided at the POS.");
        if (!Creditable.Contains(invoice.Status))
            throw new BadRequestException($"Invoice {invoice.InvoiceNumber} is {invoice.Status}; only an issued sale can be returned or voided.");
        return invoice;
    }

    /// <summary>Per original line: quantity and amount already credited by earlier credit notes.</summary>
    private async Task<Dictionary<Guid, (decimal Qty, decimal Total)>> CreditedSoFarAsync(Guid org, Guid originalUuid, CancellationToken ct)
    {
        var rows = await _db.SalesInvoiceLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.OriginalLineUuid != null
                     && l.SalesInvoice.OriginalInvoiceUuid == originalUuid && l.SalesInvoice.Status == SalesInvoiceStatuses.CreditNote
                     && !l.SalesInvoice.IsDelete)
            .GroupBy(l => l.OriginalLineUuid!.Value)
            .Select(g => new { Line = g.Key, Qty = g.Sum(x => x.Quantity), Total = g.Sum(x => x.LineTotal) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Line, r => (r.Qty, r.Total));
    }

    private async Task<CounterReturnResult?> FindCreditNoteAsync(Guid org, Guid sourceUuid, IReadOnlyList<Guid> refundUuids, CancellationToken ct)
    {
        var found = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.SourceUuid == sourceUuid)
            .Select(i => new { i.UUID, i.InvoiceNumber, i.GrandTotal, i.Status })
            .FirstOrDefaultAsync(ct);
        if (found is null) return null;

        if (found.Status != SalesInvoiceStatuses.CreditNote)
            throw new BadRequestException($"POS document {sourceUuid} was already posted as sale invoice {found.InvoiceNumber}, not as a return.");

        var refunds = await _db.CustomerRefunds.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.OrganizationId == org && x.CreditNoteUuid == found.UUID)
            .OrderBy(x => x.Id).Select(x => x.UUID).ToListAsync(ct);

        return new CounterReturnResult(found.UUID, found.InvoiceNumber, found.GrandTotal, refunds);
    }

    // ══ Helpers ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes go through the shared context, whose ledger reads use the tenant filter: a caller of another organization
    /// (not a super admin) would chain ledger rows against the wrong books. Refused rather than guessed.
    /// </summary>
    /// <summary>
    /// A38 (QA) — the request's <see cref="FinanceDbContext"/> can still be enlisted in a transaction another module already
    /// finished: the POS stock posting writes the master product ledger through <c>MasterProductLedgerService</c>, which joins
    /// Inventory's transaction on this very (scoped) context and never lets go. Saving on a context that still holds that dead,
    /// "user-initiated" transaction is refused by the SQL retrying execution strategy, so every real POS sale failed its Finance
    /// posting. A transaction whose connection is gone (committed / rolled back) is released; a live one is left alone.
    /// </summary>
    private async Task ReleaseFinishedTransactionAsync(CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return;
        if (_db.Database.CurrentTransaction?.GetDbTransaction() is { Connection: null })
            await _db.Database.UseTransactionAsync(null, ct);
    }

    private void EnsureOrganization(Guid org)
    {
        if (org == Guid.Empty) throw new BadRequestException("The organization is required.");
        var tenant = _db.TenantContext;
        if (!tenant.IsSuperAdmin && tenant.OrganizationId != org)
            throw new BadRequestException("The POS posting is for another organization than the caller's.");
    }

    /// <summary>Every row this unit adds belongs to the request's organization — set explicitly, never inferred from the caller.</summary>
    private void StampOrganization(Guid org)
    {
        foreach (var e in _db.ChangeTracker.Entries<ITenantScopedEntity>())
            if (e.State == EntityState.Added && e.Entity.OrganizationId == Guid.Empty)
                e.Entity.OrganizationId = org;
    }

    private static bool IsOnAccount(CounterSaleTender t) =>
        string.Equals(t.Method?.Trim(), PosPaymentMethod.OnAccount, StringComparison.OrdinalIgnoreCase);

    private static void ValidateTenders(IReadOnlyList<CounterSaleTender>? tenders, decimal total, string what, string against)
    {
        if (tenders is null || tenders.Count == 0) throw new BadRequestException($"{what} are missing.");
        if (tenders.Any(t => t.SourceUuid == Guid.Empty) || tenders.Select(t => t.SourceUuid).Distinct().Count() != tenders.Count)
            throw new BadRequestException($"{what}: each needs its own id.");

        foreach (var t in tenders)
        {
            var method = t.Method?.Trim() ?? string.Empty;
            if (!MethodMap.ContainsKey(method) && !string.Equals(method, PosPaymentMethod.OnAccount, StringComparison.OrdinalIgnoreCase))
                throw new BadRequestException(
                    $"'{t.Method}' is not a POS payment method. Valid: CASH, CARD, MOBILE, BANK_TRANSFER, CHEQUE, ON_ACCOUNT, STORE_CREDIT, EXCHANGE.");
            if (t.Amount <= 0m || decimal.Round(t.Amount, 2) != t.Amount)
                throw new BadRequestException($"{what}: {t.Amount} is not an amount above zero with at most two decimals.");
            if (t.Reference is { Length: > 100 }) throw new BadRequestException("A tender reference is longer than 100 characters.");
        }

        var sum = tenders.Sum(t => t.Amount);
        if (sum != total)
            throw new BadRequestException($"{what} come to {sum:0.00}, not {against} of {total:0.00}.");
    }

    private static void RequireSource(Guid sourceUuid, string? sourceNumber)
    {
        if (sourceUuid == Guid.Empty) throw new BadRequestException("The POS document id is required (it is the idempotency key).");
        if (string.IsNullOrWhiteSpace(sourceNumber)) throw new BadRequestException("The POS document number is required.");
        if (sourceNumber.Trim().Length > 50) throw new BadRequestException("The POS document number is longer than 50 characters.");
    }

    private static string Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BadRequestException("Say why: the reason is kept on the credit note and the customer's ledger.");
        return Truncate(reason.Trim(), MaxReasonLength);
    }

    private static void Quantity(decimal q)
    {
        if (q <= 0m || decimal.Round(q, 4) != q)
            throw new BadRequestException($"A quantity of {q} is not above zero with at most four decimals.");
    }

    private static void Money(decimal value, string what)
    {
        if (value < 0m || decimal.Round(value, 2) != value)
            throw new BadRequestException($"A line's {what} of {value} is not an amount of zero or more with at most two decimals.");
    }

    private static string Prefix(string prefix, DateTime day) => $"{prefix}-{day:yyyyMMdd}-";

    private Task<string> NextInvoiceNumberAsync(Guid org, string prefix, DateTime day, CancellationToken ct) =>
        NextAsync(_db.SalesInvoices.IgnoreQueryFilters().Where(i => i.OrganizationId == org).Select(i => i.InvoiceNumber), Prefix(prefix, day), ct);

    /// <summary>
    /// The day's next number for the prefix: the highest committed (or tracked in this unit) + 1, the unique
    /// (organization, number) index being the guard — the loser of a race retries.
    /// </summary>
    private async Task<string> NextAsync(IQueryable<string> numbers, string prefix, CancellationToken ct) =>
        $"{prefix}{await HighestAsync(numbers, prefix, ct) + 1:D4}";

    private async Task<int> HighestAsync(IQueryable<string> numbers, string prefix, CancellationToken ct)
    {
        var used = await numbers.Where(n => n.StartsWith(prefix)).ToListAsync(ct);

        // A number this unit of work has already taken (a credit note built before another) counts too.
        used.AddRange(_db.ChangeTracker.Entries<SalesInvoice>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.InvoiceNumber));
        used.AddRange(_db.ChangeTracker.Entries<CustomerPayment>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.PaymentNumber));
        used.AddRange(_db.ChangeTracker.Entries<CustomerRefund>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.RefundNumber));

        return used.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private async Task<string> PartnerNameAsync(Guid org, Guid partnerId)
    {
        try
        {
            var names = await _partnerNames.GetNamesAsync([partnerId]);
            if (names is not null && names.TryGetValue(partnerId, out var name) && !string.IsNullOrWhiteSpace(name))
                return Truncate(name.Trim(), 200);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Customer {Partner}'s name could not be read for a POS posting.", partnerId);
        }

        var billed = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.PartnerId == partnerId)
            .Select(i => i.PartnerName).FirstOrDefaultAsync();
        return billed ?? "(unknown customer)";
    }

    private async Task<int> TermsAsync(Guid org, Guid partnerId, CancellationToken ct)
    {
        if (_terms is null) return SalesInvoiceService.DefaultPaymentTermDays;
        try
        {
            var days = await _terms.GetPaymentTermsDaysAsync(org, partnerId, ct);
            return days is > 0 ? days.Value : SalesInvoiceService.DefaultPaymentTermDays;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Customer {Partner}'s payment terms could not be read; a POS credit sale falls due in {Days} days.",
                partnerId, SalesInvoiceService.DefaultPaymentTermDays);
            return SalesInvoiceService.DefaultPaymentTermDays;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
