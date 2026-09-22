using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Reports.Services;

/// <summary>
/// R2 and R3 of A29 §15. Reads Finance's receivables books straight from its context, as the other
/// reports read their modules', and asks Suppliers only for what a row stores by id: the customer's name.
/// <para>
/// Both reports are <b>as of a day, by business date</b>. R2 counts a ledger entry on its entry date, so a
/// receipt back-dated to the 3rd is on the 3rd whenever it was keyed. R3 asks what was owed at the end of
/// the as-of day from the same books: an invoice counts once its ledger entry exists, and a payment
/// counts from its payment date until a bounce entry, if any, takes it back. Those are the ledger's own
/// dates, so the two reports agree with each other and with the invoices' stored balances.
/// </para>
/// </summary>
internal sealed class ReceivablesReportService : IReceivablesReportService
{
    /// <summary>The most entries or invoices one report carries.</summary>
    internal const int MaxRows = 10_000;

    private const string InvoiceReferenceType = "SalesInvoice";
    private const string PaymentReferenceType = "CustomerPayment";

    private readonly FinanceDbContext            _finance;
    private readonly ISupplierNameLookupService  _names;
    private readonly IPoDocumentTemplateService  _templates;
    private readonly TimeProvider                _clock;

    public ReceivablesReportService(
        FinanceDbContext finance, ISupplierNameLookupService names, IPoDocumentTemplateService templates,
        TimeProvider? clock = null)
    {
        _finance   = finance;
        _names     = names;
        _templates = templates;
        _clock     = clock ?? TimeProvider.System;
    }

    public Task<CustomerLedgerReport> GetCustomerLedgerAsync(CustomerLedgerReportFilter filter) =>
        BuildLedgerAsync(filter, forExport: false);

    public Task<CustomerLedgerReport> GetCustomerLedgerForExportAsync(CustomerLedgerReportFilter filter) =>
        BuildLedgerAsync(filter, forExport: true);

    public Task<AgingReceivablesReport> GetAgingReceivablesAsync(AgingReceivablesFilter filter) =>
        BuildAgingAsync(filter, forExport: false);

    public Task<AgingReceivablesReport> GetAgingReceivablesForExportAsync(AgingReceivablesFilter filter) =>
        BuildAgingAsync(filter, forExport: true);

    // ── R2 Customer ledger ───────────────────────────────────────────────────

    private async Task<CustomerLedgerReport> BuildLedgerAsync(CustomerLedgerReportFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.PartnerId is not { } partner || partner == Guid.Empty)
            throw new BadRequestException("Name the customer: a customer ledger is one customer's account.");

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "customer ledger");

        var account = _finance.CustomerLedgerEntries.AsNoTracking().Where(e => e.PartnerId == partner);
        var inRange = account;
        if (start is { } from)         inRange = inRange.Where(e => e.EntryDate >= from);
        if (endExclusive is { } end)   inRange = inRange.Where(e => e.EntryDate < end);

        var total = await inRange.CountAsync();
        if (total > MaxRows)
            throw new BadRequestException(
                $"{total} ledger entries fall in that range, more than the {MaxRows} one statement carries. Narrow the date range.");

        // What was owed when the range began: everything posted for an earlier day, whenever it was keyed.
        var opening = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (start is { } first)
        {
            var before = await account.Where(e => e.EntryDate < first)
                .GroupBy(e => e.CurrencyCode)
                .Select(g => new { Currency = g.Key, Net = g.Sum(e => e.DebitAmount - e.CreditAmount) })
                .ToListAsync();

            foreach (var b in before) opening[b.Currency] = b.Net;
        }

        var rows = await inRange.OrderBy(e => e.SequenceNo)
            .Select(e => new
            {
                e.SequenceNo, e.EntryDate, e.EntryType, e.ReferenceType, e.ReferenceId, e.ReferenceNumber,
                e.Narration, e.CurrencyCode, e.DebitAmount, e.CreditAmount
            })
            .ToListAsync();

        // A statement reads by business date; posting order breaks ties within a day, so a debit and a
        // credit of one day never swap places, and a back-dated receipt lands where its money came in.
        var running = new Dictionary<string, decimal>(opening, StringComparer.Ordinal);
        var entries = new List<CustomerLedgerReportEntry>(rows.Count);
        foreach (var r in rows.OrderBy(r => r.EntryDate.Date).ThenBy(r => r.SequenceNo))
        {
            var balance = running.GetValueOrDefault(r.CurrencyCode) + r.DebitAmount - r.CreditAmount;
            running[r.CurrencyCode] = balance;

            entries.Add(new CustomerLedgerReportEntry
            {
                SequenceNo      = r.SequenceNo,
                EntryDate       = r.EntryDate,
                EntryType       = r.EntryType,
                ReferenceType   = r.ReferenceType,
                ReferenceId     = r.ReferenceId,
                ReferenceNumber = r.ReferenceNumber,
                Narration       = r.Narration,
                CurrencyCode    = r.CurrencyCode,
                DebitAmount     = r.DebitAmount,
                CreditAmount    = r.CreditAmount,
                Balance         = balance
            });
        }

        var summaries = opening.Keys.Concat(entries.Select(e => e.CurrencyCode))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(currency =>
            {
                var openingBalance = opening.GetValueOrDefault(currency);
                var mine           = entries.Where(e => e.CurrencyCode == currency).ToList();
                var debit          = mine.Sum(e => e.DebitAmount);
                var credit         = mine.Sum(e => e.CreditAmount);

                return new CustomerLedgerReportSummary
                {
                    CurrencyCode   = currency,
                    OpeningBalance = openingBalance,
                    TotalDebit     = debit,
                    TotalCredit    = credit,
                    ClosingBalance = openingBalance + debit - credit,
                    EntryCount     = mine.Count
                };
            })
            // A currency the customer settled to nothing before the range, and never touched in it, is not a line.
            .Where(s => s.OpeningBalance != 0m || s.EntryCount > 0)
            .ToList();

        var name = (await _names.GetNamesAsync([partner])).GetValueOrDefault(partner);
        if (name is null && total == 0 && !await account.AnyAsync())
            throw new NotFoundException("Customer", partner);

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, entries.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);

        return new CustomerLedgerReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new CustomerLedgerReportCriteria
            {
                PartnerId = partner, CustomerName = name, DateFrom = start, DateTo = filter.DateTo?.Date
            },
            Summaries    = summaries,
            Entries      = [.. entries.Skip((page - 1) * pageSize).Take(pageSize)],
            TotalRecords = entries.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(entries.Count / (double)pageSize)
        };
    }

    // ── R3 Aging receivables ─────────────────────────────────────────────────

    private async Task<AgingReceivablesReport> BuildAgingAsync(AgingReceivablesFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var generatedAt = _clock.GetUtcNow().UtcDateTime;
        var asOf        = (filter.AsOf ?? generatedAt).Date;
        var end         = asOf.AddDays(1);

        // An invoice is a receivable from the day its ledger entry was posted (issue, not the draft's
        // date), and a payment reduces it from the day the money came in — unless the cheque came back by
        // then. Drafts, cancelled invoices and credit notes are not receivables at all.
        var booked = _finance.CustomerLedgerEntries.Where(e =>
            e.EntryType == CustomerLedgerEntryTypes.Invoice && e.ReferenceType == InvoiceReferenceType && e.EntryDate < end);
        var bounces = _finance.CustomerLedgerEntries.Where(e =>
            e.EntryType == CustomerLedgerEntryTypes.Payment && e.ReferenceType == PaymentReferenceType
            && e.DebitAmount > 0m && e.EntryDate < end);

        var candidates = _finance.SalesInvoices.AsNoTracking()
            .Where(i => !i.IsDelete && i.Status != SalesInvoiceStatuses.Draft && i.Status != SalesInvoiceStatuses.Cancelled && i.Status != SalesInvoiceStatuses.CreditNote)
            .Where(i => booked.Any(b => b.ReferenceId == i.UUID));

        if (filter.PartnerId is { } only) candidates = candidates.Where(i => i.PartnerId == only);

        var open = await candidates
            .Select(i => new
            {
                i.Id, i.UUID, i.InvoiceNumber, i.SaleOrderNumber, i.PartnerId, i.CurrencyCode, i.InvoiceDate, i.DueDate, i.GrandTotal,
                Paid = _finance.PaymentAllocations
                    .Where(a => a.SalesInvoiceId == i.Id
                             && a.CustomerPayment.PaymentDate < end
                             && a.CustomerPayment.Status != CustomerPaymentStatuses.Reversed
                             && !bounces.Any(b => b.ReferenceId == a.CustomerPayment.UUID))
                    .Sum(a => (decimal?)a.AllocatedAmount) ?? 0m
            })
            .Where(x => x.GrandTotal - x.Paid > 0m)
            .OrderBy(x => x.Id)
            .Take(MaxRows + 1)
            .ToListAsync();

        if (open.Count > MaxRows)
            throw new BadRequestException(
                $"More than {MaxRows} invoices were still owed on {asOf:dd MMM yyyy}, more than one report carries. Narrow it to one customer.");

        var partnerIds = open.Select(x => x.PartnerId).Concat(filter.PartnerId is { } p ? [p] : []).Distinct().ToList();
        var names      = partnerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : new Dictionary<Guid, string>(await _names.GetNamesAsync(partnerIds));

        if (filter.PartnerId is { } named && !names.ContainsKey(named) && open.Count == 0
            && !await _finance.CustomerLedgerEntries.AnyAsync(e => e.PartnerId == named))
            throw new NotFoundException("Customer", named);

        var invoices = open
            .Select(x =>
            {
                var daysPastDue = Math.Max(0, (asOf - x.DueDate.Date).Days);
                return new AgingReceivablesInvoice
                {
                    InvoiceUuid     = x.UUID,
                    InvoiceNumber   = x.InvoiceNumber,
                    SaleOrderNumber = x.SaleOrderNumber,
                    PartnerId       = x.PartnerId,
                    CustomerName    = names.GetValueOrDefault(x.PartnerId),
                    CurrencyCode    = x.CurrencyCode,
                    InvoiceDate     = x.InvoiceDate,
                    DueDate         = x.DueDate,
                    DaysPastDue     = daysPastDue,
                    Bucket          = AgingBuckets.For(daysPastDue),
                    GrandTotal      = x.GrandTotal,
                    AmountPaid      = x.Paid,
                    Outstanding     = x.GrandTotal - x.Paid
                };
            })
            .OrderBy(i => i.CustomerName is null)
            .ThenBy(i => i.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.PartnerId)
            .ThenBy(i => i.DueDate)
            .ThenBy(i => i.InvoiceNumber, StringComparer.Ordinal)
            .ToList();

        var totals = invoices
            .GroupBy(i => i.CurrencyCode)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AgingReceivablesTotal
            {
                CurrencyCode = g.Key,
                InvoiceCount = g.Count(),
                Days0To30    = InBucket(g, AgingBuckets.Days0To30),
                Days31To60   = InBucket(g, AgingBuckets.Days31To60),
                Days61To90   = InBucket(g, AgingBuckets.Days61To90),
                Over90       = InBucket(g, AgingBuckets.Over90),
                Total        = g.Sum(i => i.Outstanding)
            })
            .ToList();

        // Grouped in invoice order, which is already customer by customer, so the rows come out in it too.
        var customers = invoices
            .GroupBy(i => (i.PartnerId, i.CurrencyCode))
            .Select(g => new AgingReceivablesCustomer
            {
                PartnerId    = g.Key.PartnerId,
                CustomerName = g.First().CustomerName,
                CurrencyCode = g.Key.CurrencyCode,
                InvoiceCount = g.Count(),
                Days0To30    = InBucket(g, AgingBuckets.Days0To30),
                Days31To60   = InBucket(g, AgingBuckets.Days31To60),
                Days61To90   = InBucket(g, AgingBuckets.Days61To90),
                Over90       = InBucket(g, AgingBuckets.Over90),
                Total        = g.Sum(i => i.Outstanding)
            })
            .OrderBy(c => c.CustomerName is null)
            .ThenBy(c => c.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.PartnerId)
            .ThenBy(c => c.CurrencyCode, StringComparer.Ordinal)
            .ToList();

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, invoices.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);

        return new AgingReceivablesReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = generatedAt,
            Criteria    = new AgingReceivablesCriteria
            {
                AsOf = asOf, PartnerId = filter.PartnerId, CustomerName = filter.PartnerId is { } id ? names.GetValueOrDefault(id) : null
            },
            Totals       = totals,
            Customers    = customers,
            Invoices     = [.. invoices.Skip((page - 1) * pageSize).Take(pageSize)],
            TotalRecords = invoices.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(invoices.Count / (double)pageSize)
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static decimal InBucket(IEnumerable<AgingReceivablesInvoice> invoices, string bucket) =>
        invoices.Where(i => i.Bucket == bucket).Sum(i => i.Outstanding);

    private async Task<string?> CompanyNameAsync()
    {
        var template = await _templates.GetActiveAsync();
        return string.IsNullOrWhiteSpace(template?.CompanyName) ? null : template.CompanyName.Trim();
    }
}
