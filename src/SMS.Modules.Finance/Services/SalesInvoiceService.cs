using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Finance.Services;

// A29-P7-04 §9.5 / §13.3.
internal sealed class SalesInvoiceService : ISalesInvoiceService
{
    /// <summary>
    /// The sale order carries no payment terms, so a receivable falls due this long after the invoice
    /// date. Same placeholder the freight settlement uses for a bill that names no due date.
    /// </summary>
    internal const int DefaultPaymentTermDays = 30;

    private const int MaxAttempts = 5;
    private const string NumberPrefix = "SINV";

    private static readonly string DeliveredStatus = "DELIVERED";
    private static readonly string ClosedStatus    = "CLOSED";

    private readonly FinanceDbContext              _db;
    private readonly DemandDbContext               _demand;
    private readonly IDeliveryFulfillmentReader    _deliveries;
    private readonly ICustomerLedgerService        _ledger;
    private readonly IProductLedgerWriter          _productLedger;
    private readonly ISupplierNameLookupService    _partnerNames;
    private readonly ILookupsService               _lookups;
    private readonly IBackgroundJobClient          _jobs;
    private readonly ILogger<SalesInvoiceService>  _log;
    private readonly TimeProvider                  _clock;
    private readonly SalesInvoiceQuickBooksPublisher? _quickBooks;
    private readonly IExchangeRateProvider?        _exchangeRates;
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly ICurrencyCodeLookup?          _currencyCodes;

    /// <param name="quickBooks">
    /// Tells the QuickBooks gateway about an invoice once it is issued (plan D-7). Optional so a caller that
    /// builds this service by hand needs nothing extra; the module registers it.
    /// </param>
    /// <param name="exchangeRates">
    /// SAP alignment (S-5) — the rate an issued invoice is snapshotted at, with <paramref name="orgCurrency"/>
    /// (the organization's base currency) and <paramref name="currencyCodes"/> (its ISO code; the Lookups
    /// catalog is asked when this is absent). Optional, like <paramref name="quickBooks"/>: without them an
    /// invoice is issued with no snapshot, exactly as a missing rate would leave it.
    /// </param>
    public SalesInvoiceService(
        FinanceDbContext db, DemandDbContext demand, IDeliveryFulfillmentReader deliveries,
        ICustomerLedgerService ledger, IProductLedgerWriter productLedger,
        ISupplierNameLookupService partnerNames, ILookupsService lookups,
        IBackgroundJobClient jobs, ILogger<SalesInvoiceService> log, TimeProvider? clock = null,
        SalesInvoiceQuickBooksPublisher? quickBooks = null,
        IExchangeRateProvider? exchangeRates = null, IOrganizationCurrencyService? orgCurrency = null,
        ICurrencyCodeLookup? currencyCodes = null)
    {
        _db            = db;
        _demand        = demand;
        _deliveries    = deliveries;
        _ledger        = ledger;
        _productLedger = productLedger;
        _partnerNames  = partnerNames;
        _lookups       = lookups;
        _jobs          = jobs;
        _log           = log;
        _clock         = clock ?? TimeProvider.System;
        _quickBooks    = quickBooks;
        _exchangeRates = exchangeRates;
        _orgCurrency   = orgCurrency;
        _currencyCodes = currencyCodes;
    }

    // ── Create ───────────────────────────────────────────────────────────────

    public async Task<SalesInvoiceCreated> CreateFromFulfillmentAsync(Guid deliveryUuid, int userId)
    {
        var delivery = await _deliveries.GetAsync(deliveryUuid)
            ?? throw new NotFoundException("Delivery", deliveryUuid);

        // Asked first, so a retry or a second click returns the invoice already raised. The filtered
        // unique index on (organization, delivery) is what makes this safe under a race rather than
        // merely usually right.
        var existing = await LiveInvoiceForAsync(deliveryUuid);
        if (existing is not null) return ToCreated(existing, alreadyExisted: true);

        if (delivery.Status != DeliveredStatus && delivery.Status != ClosedStatus)
            throw new BadRequestException(
                $"Delivery {delivery.DeliveryNumber} is {delivery.Status}. An invoice can only be raised for goods that " +
                "have reached the customer — a delivery that is DELIVERED (or CLOSED).");

        if (delivery.SaleOrderUuid is not { } saleOrderUuid)
            throw new BadRequestException(
                $"Delivery {delivery.DeliveryNumber} is not for a sale order, so there is no customer to invoice.");

        var order = await _demand.SaleOrders.AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.UUID == saleOrderUuid && !o.IsDeleted)
            ?? throw new NotFoundException("SaleOrder", saleOrderUuid);

        if (order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft)
         || order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Cancelled))
            throw new ConflictException(
                $"Sale order {order.SoNumber} is {order.Status}, so goods delivered against it cannot be invoiced.");

        var billed = await BillableLinesAsync(delivery, order);

        var (subtotal, discount, tax, grand) = SalesInvoiceTotals.Header(
            billed.Select(b => (b.Quantity, b.SoLine.UnitPrice, b.SoLine.DiscountPercent, b.SoLine.TaxPercent)));

        if (grand <= 0m)
            throw new BadRequestException(
                $"Delivery {delivery.DeliveryNumber} comes to {grand:0.00} on sale order {order.SoNumber}, and an invoice " +
                "for nothing has no receivable to book.");

        var currencyCode  = ResolveCurrencyCode(order);
        var partnerName   = await ResolvePartnerNameAsync(order.PartnerId);
        var invoiceDate   = _clock.GetUtcNow().UtcDateTime.Date;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var invoice = new SalesInvoice
            {
                UUID            = Guid.NewGuid(),
                // §9.1: copied from the sale order, so one trace spans SO → delivery → invoice → payment.
                TraceId         = order.TraceId,
                InvoiceNumber   = await NextNumberAsync(invoiceDate),
                SaleOrderUuid   = order.UUID,
                SaleOrderNumber = order.SoNumber,
                DeliveryUuid    = delivery.DeliveryUuid,
                DeliveryNumber  = delivery.DeliveryNumber,
                PartnerId       = order.PartnerId,
                PartnerName     = partnerName,
                InvoiceDate     = invoiceDate,
                DueDate         = invoiceDate.AddDays(DefaultPaymentTermDays),
                Subtotal        = subtotal,
                DiscountAmount  = discount,
                TaxAmount       = tax,
                GrandTotal      = grand,
                AmountPaid      = 0m,
                BalanceDue      = grand,
                Status          = SalesInvoiceStatuses.Draft,
                CurrencyCode    = currencyCode,
                Notes           = $"Raised from delivery {delivery.DeliveryNumber} against sale order {order.SoNumber}.",
                CreatedBy       = userId,
                CreatedDate     = _clock.GetUtcNow().UtcDateTime
            };

            var lineNo = 1;
            foreach (var (line, soLine, quantity) in billed)
                invoice.Lines.Add(new SalesInvoiceLine
                {
                    UUID            = Guid.NewGuid(),
                    LineNo          = lineNo++,
                    SoLineUuid      = soLine.UUID,
                    VariantUuid     = soLine.VariantUuid,
                    Description     = Truncate(line.Description, 300),
                    Quantity        = quantity,
                    UnitPrice       = soLine.UnitPrice,
                    DiscountPercent = soLine.DiscountPercent,
                    TaxPercent      = soLine.TaxPercent,
                    // S-3 — the code and its rate travel together, as the order line snapshotted them.
                    TaxCodeUuid     = soLine.TaxCodeUuid,
                    TaxCode         = soLine.TaxCode,
                    LineTotal       = SalesInvoiceTotals.LineTotal(quantity, soLine.UnitPrice, soLine.DiscountPercent, soLine.TaxPercent)
                });

            _db.SalesInvoices.Add(invoice);

            try
            {
                await _db.SaveChangesAsync();
                return ToCreated(invoice, alreadyExisted: false);
            }
            catch (DbUpdateException)
            {
                // Either another caller took the same invoice number, or another caller invoiced this
                // very delivery first. Forget what failed; the next pass re-checks the second, and
                // re-reads the highest number for the first.
                DetachAdded();
                if (attempt >= MaxAttempts) throw;

                var raced = await LiveInvoiceForAsync(deliveryUuid);
                if (raced is not null) return ToCreated(raced, alreadyExisted: true);
            }
        }

        throw new InvalidOperationException(
            $"Failed to raise an invoice for delivery {delivery.DeliveryNumber} after {MaxAttempts} attempts.");
    }

    /// <summary>
    /// Each delivered line, the sale order line it fulfils, and the quantity to bill. Enforces §9.5's
    /// "invoiced qty ≤ delivered qty" across <b>every</b> invoice of the order, not only this one: what
    /// is already billed on other deliveries (drafts included — they are committed quantity) plus this
    /// delivery's quantity may not exceed what the order has delivered in all.
    /// </summary>
    private async Task<List<(DeliveredLineForInvoicing Line, SaleOrderLine SoLine, decimal Quantity)>> BillableLinesAsync(
        DeliveryForInvoicing delivery, SaleOrder order)
    {
        var delivered = delivery.Lines.Where(l => l.QtyDelivered > 0m).ToList();

        if (delivered.Count == 0)
            throw new BadRequestException(
                $"Nothing was delivered on {delivery.DeliveryNumber} — every line has a delivered quantity of zero, so there is nothing to invoice.");

        var soLineUuids = delivered.Where(l => l.SoLineUuid is not null).Select(l => l.SoLineUuid!.Value).Distinct().ToList();
        var invoicedSoFar = await InvoicedQuantitiesAsync(soLineUuids);

        var result = new List<(DeliveredLineForInvoicing, SaleOrderLine, decimal)>();

        foreach (var line in delivered)
        {
            if (line.SoLineUuid is not { } soLineUuid)
                throw new ConflictException(
                    $"Line {line.LineNo} of delivery {delivery.DeliveryNumber} does not name the sale order line it fulfils, so its price is unknown.");

            var soLine = order.Lines.FirstOrDefault(l => l.UUID == soLineUuid)
                ?? throw new ConflictException(
                    $"Line {line.LineNo} of delivery {delivery.DeliveryNumber} points at a line that is not on sale order {order.SoNumber}.");

            var alreadyInvoiced = invoicedSoFar.GetValueOrDefault(soLineUuid);

            if (alreadyInvoiced + line.QtyDelivered > soLine.FulfilledQty)
                throw new BadRequestException(
                    $"Invoicing {line.QtyDelivered:0.####} of {Truncate(line.Description, 60)} would bring the invoiced quantity to " +
                    $"{alreadyInvoiced + line.QtyDelivered:0.####}, above the {soLine.FulfilledQty:0.####} delivered on sale order {order.SoNumber}. " +
                    "An invoice cannot bill more than has been delivered.");

            result.Add((line, soLine, line.QtyDelivered));
        }

        return result;
    }

    /// <summary>
    /// What each order line has already been billed, from Finance's own invoice lines. A cancelled
    /// invoice bills nothing and a credit note is the opposite of a billing, so neither counts.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> InvoicedQuantitiesAsync(IReadOnlyList<Guid> soLineUuids)
    {
        if (soLineUuids.Count == 0) return [];

        return await _db.SalesInvoiceLines
            .Where(l => soLineUuids.Contains(l.SoLineUuid)
                     && !l.SalesInvoice.IsDelete
                     && l.SalesInvoice.Status != SalesInvoiceStatuses.Cancelled
                     && l.SalesInvoice.Status != SalesInvoiceStatuses.CreditNote)
            .GroupBy(l => l.SoLineUuid)
            .Select(g => new { SoLineUuid = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.SoLineUuid, x => x.Quantity);
    }

    // ── Issue ────────────────────────────────────────────────────────────────

    public async Task<SalesInvoiceIssued> IssueAsync(Guid invoiceUuid, int userId)
    {
        SalesInvoice issued = null!;
        CustomerLedgerEntry entry = null!;
        var costOfSales = new List<ProductLedgerEntry>();
        CurrencySnapshot? snapshot = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Re-read on every pass: a lost race means somebody else committed in between, and what
            // they committed may have been this very invoice.
            var invoice = await OwnInvoices().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete)
                ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

            if (invoice.Status != SalesInvoiceStatuses.Draft)
                throw new ConflictException(
                    $"Sales invoice {invoice.InvoiceNumber} is {invoice.Status}. Only a DRAFT can be issued.");

            var now = _clock.GetUtcNow().UtcDateTime;

            invoice.Status       = SalesInvoiceStatuses.Issued;
            invoice.ModifiedBy   = userId;
            invoice.ModifiedDate = now;

            // S-5 — the rate is fixed now, as the invoice becomes final, and never re-read afterwards. Looked
            // up once: neither the invoice's currency nor its date can change between two passes.
            snapshot ??= await SnapshotCurrencyAsync(invoice);
            invoice.ExchangeRate     = snapshot.Rate;
            invoice.BaseCurrencyCode = snapshot.BaseCurrencyCode;
            invoice.BaseGrandTotal   = snapshot.Rate is { } rate ? ExchangeRateMath.Convert(invoice.GrandTotal, rate) : null;

            // §9.5 — the receivable is booked in the same transaction as the status change: one
            // SaveChanges below commits both, or neither.
            entry = await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                invoice.PartnerId, CustomerLedgerEntryTypes.Invoice,
                "SalesInvoice", invoice.UUID, invoice.InvoiceNumber,
                Debit: invoice.GrandTotal, Credit: 0m, invoice.CurrencyCode,
                $"Invoice {invoice.InvoiceNumber} for sale order {invoice.SaleOrderNumber}"
                    + (invoice.DeliveryNumber is null ? "" : $" (delivery {invoice.DeliveryNumber})"),
                now, userId));

            // §11.2 — what the goods cost is booked in the same unit of work too, one SALE entry per line.
            await TrackCostOfSalesAsync(invoice, now, userId, entry, costOfSales);

            try
            {
                await _db.SaveChangesAsync();
                issued = invoice;
                break;
            }
            catch (DbUpdateException)
            {
                // Another writer took this customer's next SequenceNo — or one of these variants' — first.
                // Detach everything so the next pass re-reads the invoice's status and the ledgers' last
                // rows afresh, and re-costs the sale at whatever the average has become — and so a final
                // failure leaves neither the half-issued invoice nor any of its entries tracked.
                DetachAll(invoice, entry, costOfSales);
                if (attempt >= MaxAttempts) throw;
            }
        }

        await RecordInvoicedQuantitiesAsync(issued);
        RecordOnTimeline(issued, userId);

        // DRAFT → ISSUED: the invoice is now something QuickBooks should have. After the commit above and
        // never able to fail the issue — the publisher logs and swallows whatever the gateway does.
        if (_quickBooks is not null)
            await _quickBooks.OnChangedAsync(issued, SalesInvoiceStatuses.Draft, contentChanged: false);

        return new SalesInvoiceIssued(issued.UUID, issued.InvoiceNumber, issued.Status, issued.GrandTotal, entry.RunningBalance);
    }

    /// <summary>
    /// What an invoice is worth in the organization's base currency. The base currency is kept whenever it is
    /// known, the rate only when one was on file; both null when not even the base currency could be told.
    /// </summary>
    private sealed record CurrencySnapshot(decimal? Rate, string? BaseCurrencyCode)
    {
        public static readonly CurrencySnapshot None = new(null, null);
    }

    /// <summary>
    /// S-5 — the invoice currency's rate into the organization's base currency on the invoice date: exactly
    /// 1 when they are the same, the rate on file (or the reciprocal of the opposite pair) otherwise. No rate
    /// on file (or none that could be read) keeps the base currency with no rate — so the invoice can say
    /// "no USD → PKR rate was on file", as an approved supplier invoice does; no base currency, or no ISO code
    /// for it, leaves no snapshot at all. Neither <b>ever</b> stops the issue: an organization that has not set
    /// up rates bills exactly as it did before they existed.
    /// </summary>
    private async Task<CurrencySnapshot> SnapshotCurrencyAsync(SalesInvoice invoice)
    {
        string? baseCode;
        try
        {
            if (_orgCurrency is null) return CurrencySnapshot.None;

            var baseId = await _orgCurrency.GetBaseCurrencyIdAsync(invoice.OrganizationId);
            if (baseId is not { } id) return CurrencySnapshot.None;

            baseCode = _currencyCodes is not null
                ? await _currencyCodes.GetCodeAsync(id)
                : _lookups.GetCurrencies().FirstOrDefault(c => c.Id == id)?.Code;
            if (string.IsNullOrWhiteSpace(baseCode)) return CurrencySnapshot.None;
            baseCode = baseCode.Trim();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Sales invoice {Invoice} is being issued without an exchange-rate snapshot: its organization's base currency could not be read.",
                invoice.InvoiceNumber);
            return CurrencySnapshot.None;
        }

        if (string.Equals(baseCode, invoice.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            return new CurrencySnapshot(1m, baseCode);

        var noRate = new CurrencySnapshot(null, baseCode);
        if (_exchangeRates is null) return noRate;

        try
        {
            var quote = await _exchangeRates.GetRateAsync(invoice.CurrencyCode, baseCode, invoice.InvoiceDate.Date);
            if (quote is null) return noRate;

            // decimal(18,8) is what the column keeps; the base total is worked from the rate as stored, so
            // the two always agree when read back.
            var rate = Math.Round(quote.Rate, 8, MidpointRounding.AwayFromZero);
            return rate > 0m ? new CurrencySnapshot(rate, baseCode) : noRate;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Sales invoice {Invoice} is being issued with no exchange rate: looking the {From} → {To} rate up failed.",
                invoice.InvoiceNumber, invoice.CurrencyCode, baseCode);
            return noRate;
        }
    }

    /// <summary>
    /// §11.2/§11.3 — the cost side of the sale: an OUT entry on the product ledger for each invoice line,
    /// costed at the variant's weighted-average cost <b>as it stands</b>, never at the selling price. The
    /// entries are only tracked, so the caller's single save commits them with the status change and the
    /// customer's debit. The ledger holds the stock or the invoice is not issued: a sale it cannot cost
    /// would book revenue with no cost of sales behind it.
    /// </summary>
    private async Task TrackCostOfSalesAsync(
        SalesInvoice invoice, DateTime now, int userId, CustomerLedgerEntry customerEntry, List<ProductLedgerEntry> tracked)
    {
        try
        {
            foreach (var line in invoice.Lines.OrderBy(l => l.LineNo))
                tracked.Add(await _productLedger.TrackEntryAsync(new ProductLedgerPosting(
                    line.VariantUuid, ProductLedgerEntryTypes.Sale, ProductLedgerDirections.Out, line.Quantity, UnitCost: null,
                    "SalesInvoice", invoice.UUID, invoice.InvoiceNumber, userId,
                    PartnerId: invoice.PartnerId,
                    Narration: Truncate(
                        $"Invoice {invoice.InvoiceNumber} line {line.LineNo}: {line.Quantity:0.####} sold at {line.UnitPrice:0.####}", 500),
                    EntryDate: now)));
        }
        catch (Exception ex)
        {
            // Whatever was tracked so far — the invoice's new status, the customer's debit, the earlier
            // lines' entries — must not wait on the context for some later save to commit half of it.
            DetachAll(invoice, customerEntry, tracked);

            if (ex is ConflictException)
                throw new ConflictException($"Sales invoice {invoice.InvoiceNumber} cannot be issued: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Brings the order's per-line <c>invoiced_qty</c> up to date. After the invoice has committed and
    /// never allowed to fail it: the receivable is booked whatever the order's counter does next, and
    /// the counter is derivable from Finance's own invoice lines, so a failure here is logged for
    /// someone to reconcile rather than surfaced as a failed issue.
    /// </summary>
    private async Task RecordInvoicedQuantitiesAsync(SalesInvoice invoice)
    {
        try
        {
            var soLineUuids = invoice.Lines.Select(l => l.SoLineUuid).ToList();
            var soLines = await _demand.SaleOrderLines.Where(l => soLineUuids.Contains(l.UUID)).ToListAsync();

            foreach (var soLine in soLines)
                soLine.InvoicedQty += invoice.Lines.Where(l => l.SoLineUuid == soLine.UUID).Sum(l => l.Quantity);

            await _demand.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Sales invoice {Invoice} was issued but sale order {SoNumber}'s invoiced quantities could not be updated; reconcile by hand.",
                invoice.InvoiceNumber, invoice.SaleOrderNumber);
        }
    }

    /// <summary>
    /// §13.3 <c>SO_INVOICED</c> (SALE_ORDER → SALES_INVOICE), on the order's own trace — copied onto the
    /// invoice at creation, so no read of the order is needed. The organization travels with the job
    /// (§13.7) rather than relying on a request that a background caller may not have.
    /// </summary>
    private void RecordOnTimeline(SalesInvoice invoice, int userId)
    {
        var units = invoice.Lines.Sum(l => l.Quantity);
        var notes = $"Invoice {invoice.InvoiceNumber} issued for {invoice.GrandTotal:0.00} {invoice.CurrencyCode} " +
                    $"({units:0.####} unit(s))" +
                    (invoice.DeliveryNumber is null ? "" : $" — delivery {invoice.DeliveryNumber}");

        var traceId  = invoice.TraceId;
        var evt      = new TimelineEvent(
            SaleOrderTimelineEventTypes.SoInvoiced, "SO", invoice.SaleOrderUuid, invoice.SaleOrderNumber,
            _clock.GetUtcNow().UtcDateTime, userId, notes);
        var soNumber = invoice.SaleOrderNumber;
        var orgId    = invoice.OrganizationId;

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(traceId, evt, "SO", soNumber, orgId));
    }

    // ── Read, edit, delete ───────────────────────────────────────────────────

    public async Task<SalesInvoiceDetailModel?> GetAsync(Guid invoiceUuid)
    {
        var invoice = await _db.SalesInvoices.AsNoTracking()
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete);

        if (invoice is null) return null;

        // Every payment applied to this invoice, oldest first — including one that has since bounced
        // or been reversed, whose status says so, rather than quietly dropping it from the history.
        var payments = await (
            from allocation in _db.PaymentAllocations.AsNoTracking()
            join payment in _db.CustomerPayments.AsNoTracking() on allocation.CustomerPaymentId equals payment.Id
            where allocation.SalesInvoiceId == invoice.Id
            orderby allocation.AllocatedAt, allocation.Id
            select new SalesInvoicePaymentModel
            {
                AllocationUuid  = allocation.UUID,
                PaymentUuid     = payment.UUID,
                PaymentNumber   = payment.PaymentNumber,
                PaymentDate     = payment.PaymentDate,
                PaymentMethod   = payment.PaymentMethod,
                PaymentStatus   = payment.Status,
                AllocatedAmount = allocation.AllocatedAmount,
                AllocatedAt     = allocation.AllocatedAt,
                AllocatedBy     = allocation.AllocatedBy
            }).ToListAsync();

        var detail = new SalesInvoiceDetailModel
        {
            TraceId        = invoice.TraceId,
            Subtotal       = invoice.Subtotal,
            DiscountAmount = invoice.DiscountAmount,
            TaxAmount      = invoice.TaxAmount,
            Notes          = invoice.Notes,
            CreatedBy      = invoice.CreatedBy,
            CreatedDate    = invoice.CreatedDate,
            ModifiedBy     = invoice.ModifiedBy,
            ModifiedDate   = invoice.ModifiedDate,
            Lines = [.. invoice.Lines.OrderBy(l => l.LineNo).Select(l => new SalesInvoiceLineModel
            {
                LineNo          = l.LineNo,
                SoLineUuid      = l.SoLineUuid,
                VariantUuid     = l.VariantUuid,
                Description     = l.Description,
                Quantity        = l.Quantity,
                UnitPrice       = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent      = l.TaxPercent,
                TaxCodeUuid     = l.TaxCodeUuid,
                TaxCode         = l.TaxCode,
                LineTotal       = l.LineTotal
            })],
            Payments           = payments,
            ExchangeRate       = invoice.ExchangeRate,
            BaseCurrencyCode   = invoice.BaseCurrencyCode,
            BaseGrandTotal     = invoice.BaseGrandTotal,
            CancelledAt        = invoice.CancelledAt,
            CancelledBy        = invoice.CancelledBy,
            CancellationReason = invoice.CancellationReason
        };
        FillHeader(detail, invoice);
        return detail;
    }

    public async Task<PaginatedResponse<SalesInvoiceListItemModel>> ListAsync(SalesInvoiceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "invoice list");

        var status = filter.Status?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(status) && !SalesInvoiceStatuses.All.Contains(status))
            throw new BadRequestException(
                $"'{filter.Status}' is not an invoice status. Valid: {string.Join(", ", SalesInvoiceStatuses.All)}.");

        var query = _db.SalesInvoices.AsNoTracking().Where(i => !i.IsDelete);

        if (filter.PartnerId is { } partner)         query = query.Where(i => i.PartnerId == partner);
        if (filter.SaleOrderUuid is { } saleOrder)   query = query.Where(i => i.SaleOrderUuid == saleOrder);
        if (!string.IsNullOrEmpty(status))           query = query.Where(i => i.Status == status);
        if (start is { } from)                       query = query.Where(i => i.InvoiceDate >= from);
        if (endExclusive is { } end)                 query = query.Where(i => i.InvoiceDate < end);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            query = query.Where(i => i.InvoiceNumber.ToLower().Contains(s)
                                  || i.PartnerName.ToLower().Contains(s)
                                  || i.SaleOrderNumber.ToLower().Contains(s)
                                  || (i.DeliveryNumber != null && i.DeliveryNumber.ToLower().Contains(s)));
        }

        var total = await query.CountAsync();
        var (page, pageSize) = PagedResults.Clamp(filter.Page, filter.PageSize);

        var items = await query
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new SalesInvoiceListItemModel
            {
                Uuid            = i.UUID,
                InvoiceNumber   = i.InvoiceNumber,
                SaleOrderUuid   = i.SaleOrderUuid,
                SaleOrderNumber = i.SaleOrderNumber,
                DeliveryUuid    = i.DeliveryUuid,
                DeliveryNumber  = i.DeliveryNumber,
                PartnerId       = i.PartnerId,
                PartnerName     = i.PartnerName,
                InvoiceDate     = i.InvoiceDate,
                DueDate         = i.DueDate,
                GrandTotal      = i.GrandTotal,
                AmountPaid      = i.AmountPaid,
                BalanceDue      = i.BalanceDue,
                Status          = i.Status,
                CurrencyCode    = i.CurrencyCode
            })
            .ToListAsync();

        return PagedResults.Of(items, total, page, pageSize);
    }

    public async Task UpdateAsync(Guid invoiceUuid, UpdateSalesInvoiceRequest request, int userId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invoice = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete)
            ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

        if (invoice.Status != SalesInvoiceStatuses.Draft)
            throw new ConflictException(
                $"Sales invoice {invoice.InvoiceNumber} is {invoice.Status}. Only a DRAFT can be edited.");

        var dueDate = request.DueDate.Date;
        if (dueDate < invoice.InvoiceDate.Date)
            throw new BadRequestException(
                $"The due date {dueDate:dd MMM yyyy} is before the invoice date {invoice.InvoiceDate:dd MMM yyyy}.");

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes is { Length: > 500 })
            throw new BadRequestException("The notes are longer than 500 characters.");

        invoice.DueDate      = dueDate;
        invoice.Notes        = notes;
        invoice.ModifiedBy   = userId;
        invoice.ModifiedDate = _clock.GetUtcNow().UtcDateTime;

        await SaveEditAsync(invoice);
    }

    public async Task DeleteAsync(Guid invoiceUuid, int userId)
    {
        var invoice = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete)
            ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

        if (invoice.Status != SalesInvoiceStatuses.Draft)
            throw new ConflictException(
                $"Sales invoice {invoice.InvoiceNumber} is {invoice.Status}. Only a DRAFT can be deleted — " +
                "an issued invoice has a receivable booked against it.");

        // Soft: the number stays used and the row stays for the audit trail, but the invoice no longer
        // counts against its delivery (the unique index and the quantity check both skip deleted rows),
        // so the delivery can be invoiced afresh.
        invoice.IsDelete     = true;
        invoice.IsActive     = false;
        invoice.ModifiedBy   = userId;
        invoice.ModifiedDate = _clock.GetUtcNow().UtcDateTime;

        await SaveEditAsync(invoice);
    }

    // ── Cancel (SAP alignment S-7: reverse, don't edit) ──────────────────────

    internal const int MaxCancellationReasonLength = 500;

    private const string SalesInvoiceReferenceType = "SalesInvoice";

    public async Task<SalesInvoiceDetailModel> CancelAsync(Guid invoiceUuid, string? reason, int userId)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is null)
            throw new BadRequestException(
                "Say why the invoice is being cancelled: the reason is kept on the invoice and on the customer's ledger.");
        if (reason.Length > MaxCancellationReasonLength)
            throw new BadRequestException($"The reason is longer than {MaxCancellationReasonLength} characters.");

        SalesInvoice cancelled = null!;
        var previousStatus = SalesInvoiceStatuses.Issued;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Re-read on every pass, as issuing does: a lost race means someone else committed in between —
            // a payment applied to this very invoice, say — and the checks must see what they committed.
            var invoice = await OwnInvoices().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete)
                ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

            await EnsureCancellableAsync(invoice);

            var now = _clock.GetUtcNow().UtcDateTime;
            previousStatus = invoice.Status;

            invoice.Status             = SalesInvoiceStatuses.Cancelled;
            invoice.BalanceDue         = 0m;
            invoice.CancelledAt        = now;
            invoice.CancelledBy        = userId;
            invoice.CancellationReason = reason;
            invoice.ModifiedBy         = userId;
            // The concurrency token: a payment that read the invoice before this commit cannot then apply
            // itself to a cancelled invoice — its save matches no row, and its retry sees CANCELLED.
            invoice.ModifiedDate       = now;

            CustomerLedgerEntry? credit = null;
            var returns = new List<ProductLedgerEntry>();

            try
            {
                // The opposite of the INVOICE debit the issue booked: the same amount, currency and reference,
                // as a credit. The ledger is append-only, so the debit stays and this offsets it.
                credit = await _ledger.TrackEntryAsync(new CustomerLedgerPosting(
                    invoice.PartnerId, CustomerLedgerEntryTypes.CreditNote,
                    SalesInvoiceReferenceType, invoice.UUID, invoice.InvoiceNumber,
                    Debit: 0m, Credit: invoice.GrandTotal, invoice.CurrencyCode,
                    Truncate($"Invoice {invoice.InvoiceNumber} cancelled: {reason}", 500),
                    now, userId));

                await TrackSaleReversalsAsync(invoice, now, userId, returns);

                // One save: the status, the customer's credit and the stock coming back commit together or not at all.
                await _db.SaveChangesAsync();
                cancelled = invoice;
                break;
            }
            catch (Exception ex)
            {
                // Nothing of this pass may wait on the context for some later save to commit half of it.
                DetachAll(invoice, credit, returns);

                // Another writer took this customer's (or a variant's) next sequence number, or changed the
                // invoice: try again from a fresh read.
                if (ex is DbUpdateException && attempt < MaxAttempts) continue;
                if (ex is ConflictException)
                    throw new ConflictException($"Sales invoice {invoice.InvoiceNumber} cannot be cancelled: {ex.Message}");
                throw;
            }
        }

        // After the commit, and never able to undo it — each logs rather than throws.
        await RecordUninvoicedQuantitiesAsync(cancelled);
        RecordCancellationOnTimeline(cancelled, userId, reason);

        // ISSUED/OVERDUE → CANCELLED: QuickBooks voids its copy (SalesInvoiceQuickBooksRules). The publisher
        // logs and swallows whatever the gateway does.
        if (_quickBooks is not null)
            await _quickBooks.OnChangedAsync(cancelled, previousStatus, contentChanged: false);

        return (await GetAsync(invoiceUuid))!;
    }

    /// <summary>
    /// Only an invoice that is a receivable, and that nothing has been paid against, can be cancelled. A
    /// draft booked nothing (it is deleted instead); a paid or part-paid one would leave a payment applied
    /// to an invoice that no longer stands. A payment that bounced or was reversed keeps its allocation row
    /// as history but has put its money back, so it does not count.
    /// </summary>
    private async Task EnsureCancellableAsync(SalesInvoice invoice)
    {
        var number = invoice.InvoiceNumber;

        switch (invoice.Status)
        {
            case SalesInvoiceStatuses.Issued:
            case SalesInvoiceStatuses.Overdue:
                break;

            case SalesInvoiceStatuses.Draft:
                throw new ConflictException(
                    $"Sales invoice {number} is a DRAFT: nothing has been booked for it, so there is nothing to reverse. Delete the draft instead.");

            case SalesInvoiceStatuses.Cancelled:
                throw new ConflictException($"Sales invoice {number} is already CANCELLED.");

            case SalesInvoiceStatuses.PartiallyPaid:
            case SalesInvoiceStatuses.Paid:
                throw new ConflictException(
                    $"Sales invoice {number} is {invoice.Status}: {invoice.AmountPaid:0.00} {invoice.CurrencyCode} has been paid against it. " +
                    "Only an invoice nothing has been paid against can be cancelled.");

            default:
                throw new ConflictException(
                    $"Sales invoice {number} is {invoice.Status}. Only an ISSUED or OVERDUE invoice can be cancelled.");
        }

        if (invoice.AmountPaid != 0m)
            throw new ConflictException(
                $"Sales invoice {number} has {invoice.AmountPaid:0.00} {invoice.CurrencyCode} paid against it. " +
                "Only an invoice nothing has been paid against can be cancelled.");

        var applied = await _db.PaymentAllocations.AnyAsync(a =>
            a.SalesInvoiceId == invoice.Id && a.CustomerPayment.Status == CustomerPaymentStatuses.Received);
        if (applied)
            throw new ConflictException(
                $"Sales invoice {number} has a customer payment applied to it. " +
                "Only an invoice nothing has been paid against can be cancelled.");
    }

    /// <summary>
    /// §11 — every SALE the issue booked for this invoice comes back in as a RETURN_IN of the same quantity,
    /// at the unit cost it went out at, so the variant's stock value is restored rather than re-costed at
    /// whatever the average has become. Tracked only: the caller's single save commits them. An invoice
    /// issued before the product ledger existed booked none, and so takes none back.
    /// </summary>
    private async Task TrackSaleReversalsAsync(
        SalesInvoice invoice, DateTime now, int userId, List<ProductLedgerEntry> tracked)
    {
        var sales = await _db.ProductLedgerEntries.AsNoTracking()
            .Where(e => e.ReferenceType == SalesInvoiceReferenceType && e.ReferenceId == invoice.UUID
                     && e.EntryType == ProductLedgerEntryTypes.Sale && e.Direction == ProductLedgerDirections.Out)
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Id)
            .ToListAsync();

        foreach (var sale in sales)
            tracked.Add(await _productLedger.TrackEntryAsync(new ProductLedgerPosting(
                sale.VariantUuid, ProductLedgerEntryTypes.ReturnIn, ProductLedgerDirections.In, sale.Quantity, UnitCost: sale.UnitCost,
                SalesInvoiceReferenceType, invoice.UUID, invoice.InvoiceNumber, userId,
                ProductUuid: sale.ProductUuid,
                PartnerId: invoice.PartnerId,
                Narration: Truncate(
                    $"Invoice {invoice.InvoiceNumber} cancelled: {sale.Quantity:0.####} taken back at {sale.UnitCost:0.####}", 500),
                EntryDate: now)));
    }

    /// <summary>
    /// Takes the cancelled invoice's quantities back off the order's per-line <c>invoiced_qty</c>, never below
    /// zero. Best-effort, like <see cref="RecordInvoicedQuantitiesAsync"/>: the counter is derivable from
    /// Finance's own invoice lines, so a failure is logged for someone to reconcile.
    /// </summary>
    private async Task RecordUninvoicedQuantitiesAsync(SalesInvoice invoice)
    {
        try
        {
            var soLineUuids = invoice.Lines.Select(l => l.SoLineUuid).Distinct().ToList();
            var soLines = await _demand.SaleOrderLines.Where(l => soLineUuids.Contains(l.UUID)).ToListAsync();

            foreach (var soLine in soLines)
                soLine.InvoicedQty = Math.Max(0m,
                    soLine.InvoicedQty - invoice.Lines.Where(l => l.SoLineUuid == soLine.UUID).Sum(l => l.Quantity));

            await _demand.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Sales invoice {Invoice} was cancelled but sale order {SoNumber}'s invoiced quantities could not be reduced; reconcile by hand.",
                invoice.InvoiceNumber, invoice.SaleOrderNumber);
        }
    }

    /// <summary><c>SO_INVOICE_CANCELLED</c> on the order's own trace, beside the <c>SO_INVOICED</c> it undoes.</summary>
    private void RecordCancellationOnTimeline(SalesInvoice invoice, int userId, string reason)
    {
        try
        {
            var notes = Truncate(
                $"Invoice {invoice.InvoiceNumber} for {invoice.GrandTotal:0.00} {invoice.CurrencyCode} cancelled: {reason}", 500);

            var traceId  = invoice.TraceId;
            var evt      = new TimelineEvent(
                SaleOrderTimelineEventTypes.SoInvoiceCancelled, "SO", invoice.SaleOrderUuid, invoice.SaleOrderNumber,
                _clock.GetUtcNow().UtcDateTime, userId, notes);
            var soNumber = invoice.SaleOrderNumber;
            var orgId    = invoice.OrganizationId;

            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(traceId, evt, "SO", soNumber, orgId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sales invoice {Invoice} was cancelled but the order's timeline could not be told.", invoice.InvoiceNumber);
        }
    }

    /// <summary>
    /// A draft is edited or deleted from a read that may be stale — somebody issuing it at the same
    /// moment, say. The save then matches no row, and that is reported as what it is, not as a fault.
    /// </summary>
    private async Task SaveEditAsync(SalesInvoice invoice)
    {
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.Entry(invoice).State = EntityState.Detached;
            throw new ConflictException(
                $"Sales invoice {invoice.InvoiceNumber} was changed by someone else while you were working on it. Reload it and try again.");
        }
    }

    private static void FillHeader(SalesInvoiceListItemModel model, SalesInvoice i)
    {
        model.Uuid            = i.UUID;
        model.InvoiceNumber   = i.InvoiceNumber;
        model.SaleOrderUuid   = i.SaleOrderUuid;
        model.SaleOrderNumber = i.SaleOrderNumber;
        model.DeliveryUuid    = i.DeliveryUuid;
        model.DeliveryNumber  = i.DeliveryNumber;
        model.PartnerId       = i.PartnerId;
        model.PartnerName     = i.PartnerName;
        model.InvoiceDate     = i.InvoiceDate;
        model.DueDate         = i.DueDate;
        model.GrandTotal      = i.GrandTotal;
        model.AmountPaid      = i.AmountPaid;
        model.BalanceDue      = i.BalanceDue;
        model.Status          = i.Status;
        model.CurrencyCode    = i.CurrencyCode;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The caller's own organization's invoices — for the actions that <b>book</b> something (issue, cancel).
    /// The tenant filter already limits everyone else to them; a platform super admin bypasses it and would
    /// find another organization's invoice by its uuid, but every ledger row the action adds is stamped with
    /// the caller's organization at save, so the debit or the credit note, the stock moving and the QuickBooks
    /// call (made on the caller's connection) would all land in the wrong books. Another organization's
    /// invoice is therefore "not found" to these actions, for a super admin exactly as for anyone else.
    /// </summary>
    private IQueryable<SalesInvoice> OwnInvoices()
    {
        var organization = _db.TenantContext.OrganizationId;
        return _db.SalesInvoices.Where(i => i.OrganizationId == organization);
    }

    private Task<SalesInvoice?> LiveInvoiceForAsync(Guid deliveryUuid) =>
        _db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i =>
            i.DeliveryUuid == deliveryUuid && !i.IsDelete && i.Status != SalesInvoiceStatuses.Cancelled);

    private static SalesInvoiceCreated ToCreated(SalesInvoice i, bool alreadyExisted) =>
        new(i.UUID, i.InvoiceNumber, i.GrandTotal, i.CurrencyCode, alreadyExisted);

    /// <summary>
    /// <c>SINV-YYYYMMDD-NNNN</c>, one counter per organization per day (§9.1). Read as the highest
    /// number already used that day plus one — soft-deleted invoices included, so a number is never
    /// reused — with the unique <c>(organization, number)</c> index as the guard: two writers reading
    /// the same maximum cannot both commit, and the loser retries.
    /// </summary>
    private async Task<string> NextNumberAsync(DateTime date)
    {
        var prefix = $"{NumberPrefix}-{date:yyyyMMdd}-";

        var used = await _db.SalesInvoices
            .Where(i => i.InvoiceNumber.StartsWith(prefix))
            .Select(i => i.InvoiceNumber)
            .ToListAsync();

        var highest = used
            .Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{highest + 1:D4}";
    }

    private string ResolveCurrencyCode(SaleOrder order)
    {
        var code = _lookups.GetCurrencies().FirstOrDefault(c => c.Id == order.CurrencyId)?.Code;

        // No guess: a receivable booked in the wrong currency is worse than one that is refused.
        return string.IsNullOrWhiteSpace(code)
            ? throw new BadRequestException(
                $"The currency of sale order {order.SoNumber} has no ISO code in the Lookups catalog, so the invoice cannot say what it is denominated in. " +
                "Set the currency's code and try again.")
            : code.Trim();
    }

    private async Task<string> ResolvePartnerNameAsync(Guid partnerId)
    {
        var names = await _partnerNames.GetNamesAsync([partnerId]);
        return names is not null && names.TryGetValue(partnerId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : "(unknown customer)";
    }

    /// <summary>A failed save leaves what it tried to add still tracked; the retry must not see it.</summary>
    private void DetachAdded()
    {
        foreach (var e in _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            e.State = EntityState.Detached;
    }

    private void DetachAll(SalesInvoice invoice, CustomerLedgerEntry? entry, IEnumerable<ProductLedgerEntry> costOfSales)
    {
        foreach (var cost in costOfSales) _db.Entry(cost).State = EntityState.Detached;
        if (entry is not null) _db.Entry(entry).State = EntityState.Detached;
        foreach (var line in invoice.Lines) _db.Entry(line).State = EntityState.Detached;
        _db.Entry(invoice).State = EntityState.Detached;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
