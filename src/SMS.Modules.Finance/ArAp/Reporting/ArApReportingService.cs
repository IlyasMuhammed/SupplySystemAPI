using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Reporting;

/// <summary>A48 RPT C3 / C8 — aged receivables and payables, the drill-down, DSO and DPO.</summary>
public interface IAgingReportService
{
    Task<AgingReport> GetAgedReceivablesAsync(AgingReportRequest request, CancellationToken ct = default);
    Task<AgingReport> GetAgedPayablesAsync(AgingReportRequest request, CancellationToken ct = default);
    /// <summary>Every partner line with its details, unpaged (exports).</summary>
    Task<AgingReport> GetAgingForExportAsync(bool payables, AgingReportRequest request, CancellationToken ct = default);
    Task<PaginatedResponse<AgingDetailLine>> GetAgingDetailsAsync(bool payables, AgingDetailsRequest request, CancellationToken ct = default);
    Task<DaysOutstandingResult> GetDsoAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default);
    Task<DaysOutstandingResult> GetDpoAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default);
}

/// <summary>A48 RPT C6 — one customer's balance, open items and history; the AR KPIs.</summary>
public interface IArInquiryService
{
    Task<PartnerBalanceResult> GetCustomerBalanceAsync(Guid customerId, DateOnly? asOfDate = null, CancellationToken ct = default);
    Task<PaginatedResponse<OpenItemDto>> GetCustomerOpenItemsAsync(Guid customerId, OpenItemsFilter filter, CancellationToken ct = default);
    Task<PaginatedResponse<PartnerTransactionDto>> GetCustomerTransactionsAsync(Guid customerId, PartnerTransactionFilter filter, CancellationToken ct = default);
    Task<ArSummaryKpis> GetArSummaryAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default);
}

/// <summary>A48 RPT C12 — one vendor's balance, open items and history; the AP KPIs.</summary>
public interface IApInquiryService
{
    Task<PartnerBalanceResult> GetVendorBalanceAsync(Guid vendorId, DateOnly? asOfDate = null, CancellationToken ct = default);
    Task<PaginatedResponse<OpenItemDto>> GetVendorOpenItemsAsync(Guid vendorId, OpenItemsFilter filter, CancellationToken ct = default);
    Task<PaginatedResponse<PartnerTransactionDto>> GetVendorTransactionsAsync(Guid vendorId, PartnerTransactionFilter filter, CancellationToken ct = default);
    Task<ApSummaryKpis> GetApSummaryAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default);
}

/// <summary>
/// A48 RPT — optional source of the early-payment discounts still takeable (AP's payment schedules, C15). When none is
/// registered the AP summary reports 0.
/// </summary>
public interface IApDiscountOpportunitySource
{
    Task<decimal> GetDiscountOpportunityBaseAsync(DateOnly asOfDate, CancellationToken ct = default);
}

/// <summary>
/// A48 RPT — the AR / AP read side over <see cref="SubledgerOpenItems"/>. Open amounts per document are computed in SQL
/// (correlated sums, org-explicit and index-seekable), only the open documents come back, and the bucketing / partner
/// roll-up runs over that projection (D-RPT-1). All totals are base currency at each document's rate (D-13).
/// </summary>
internal sealed class ArApReportingService : IAgingReportService, IArInquiryService, IApInquiryService
{
    /// <summary>The most open documents one report rolls up (the 10k / 50k targets fit well inside).</summary>
    internal const int MaxOpenItems = 200_000;
    /// <summary>The most ledger entries one vendor's history is resolved over (supplier ledger rows carry no currency).</summary>
    internal const int MaxVendorLedgerEntries = 50_000;
    /// <summary>Contract §0: page size default 25, max 200.</summary>
    internal const int MaxPageSize = 200;

    private readonly FinanceDbContext              _db;
    private readonly SubledgerOpenItems            _items;
    private readonly SubledgerBaseCurrency         _base;
    private readonly ISupplierNameLookupService?   _names;
    private readonly IPartnerLookupService?        _partners;
    private readonly IApDiscountOpportunitySource? _discounts;
    private readonly TimeProvider                  _clock;

    public ArApReportingService(
        FinanceDbContext db, ISupplierNameLookupService? names = null, ICurrencyService? currency = null,
        IOrgCurrencyLookup? orgCurrencies = null, IApDiscountOpportunitySource? discounts = null, TimeProvider? clock = null,
        IPartnerLookupService? partners = null)
    {
        _db        = db;
        _items     = new SubledgerOpenItems(db);
        _base      = new SubledgerBaseCurrency(db, currency, orgCurrencies);
        _names     = names;
        _partners  = partners;
        _discounts = discounts;
        _clock     = clock ?? TimeProvider.System;
    }

    internal DateTime Today => _clock.GetUtcNow().UtcDateTime.Date;
    internal DateTime GeneratedAt => _clock.GetUtcNow().UtcDateTime;

    internal static (int Page, int PageSize) Clamp(int page, int pageSize) =>
        (Math.Max(1, page), pageSize < 1 ? 25 : Math.Min(pageSize, MaxPageSize));

    internal static PaginatedResponse<T> PageOf<T>(IReadOnlyList<T> all, int page, int pageSize) => new()
    {
        Data         = [.. all.Skip((page - 1) * pageSize).Take(pageSize)],
        TotalRecords = all.Count,
        Page         = page,
        PageSize     = pageSize,
        TotalPages   = (int)Math.Ceiling(all.Count / (double)pageSize)
    };

    internal static DateOnly D(DateTime value) => DateOnly.FromDateTime(value);
    internal static DateTime T(DateOnly value) => value.ToDateTime(TimeOnly.MinValue);

    // ══ Aging ═══════════════════════════════════════════════════════════════

    public Task<AgingReport> GetAgedReceivablesAsync(AgingReportRequest request, CancellationToken ct = default) =>
        BuildAgingAsync(SubledgerSide.Receivable, request, forExport: false, ct);

    public Task<AgingReport> GetAgedPayablesAsync(AgingReportRequest request, CancellationToken ct = default) =>
        BuildAgingAsync(SubledgerSide.Payable, request, forExport: false, ct);

    public Task<AgingReport> GetAgingForExportAsync(bool payables, AgingReportRequest request, CancellationToken ct = default) =>
        BuildAgingAsync(payables ? SubledgerSide.Payable : SubledgerSide.Receivable, request, forExport: true, ct);

    private static Guid? PartnerOf(SubledgerSide side, Guid? customer, Guid? vendor) =>
        side == SubledgerSide.Receivable ? customer : vendor;

    private async Task<AgingReport> BuildAgingAsync(SubledgerSide side, AgingReportRequest request, bool forExport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asOf    = request.AsOfDate is { } a ? T(a) : Today;
        var basis   = AgingBucketSet.NormalizeBasis(request.Basis);
        var buckets = AgingBucketSet.Parse(request.Buckets);
        var sortBy  = request.SortBy;
        var (page, pageSize) = Clamp(request.Page, request.PageSize);

        var rows     = await LoadAsync(side, asOf, PartnerOf(side, request.CustomerUuid, request.VendorUuid), true, request.CurrencyCode, ct);
        var baseCode = await _base.CodeAsync(side, ct);

        var lines = RollUp(rows, buckets, asOf, basis, request.IncludeDetails || forExport);
        await NameAsync(lines, ct);
        lines = Sort(lines, sortBy, request.SortDirection);

        var debits = rows.Where(r => !r.IsCredit).ToList();
        var totals = new AgingTotals
        {
            BucketTotals     = Enumerable.Range(0, buckets.Count).Select(b => lines.Sum(l => l.BucketAmounts[b])).ToArray(),
            GrandTotal       = lines.Sum(l => l.TotalOutstanding),
            TotalOverdue     = lines.Sum(l => l.TotalOverdue),
            UnappliedCredits = lines.Sum(l => l.UnappliedCredits),
            TotalPartners    = lines.Count,
            TotalOpenItems   = debits.Count
        };
        totals.NetBalance = totals.GrandTotal - totals.UnappliedCredits;

        var byCurrency = debits
            .GroupBy(r => r.CurrencyCode)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var amounts = new decimal[buckets.Count];
                foreach (var r in g) amounts[buckets.IndexOf(AgingBucketSet.AgeOf(r.DocumentDate, r.DueDate, asOf, basis))] += r.Open;
                return new AgingCurrencyTotal
                {
                    CurrencyCode = g.Key, BucketAmounts = amounts, Total = g.Sum(r => r.Open), TotalBase = g.Sum(r => r.OpenBase),
                    OpenItemCount = g.Count()
                };
            })
            .ToList();

        // DSO / DPO over the whole organization (not the filtered partner / currency) — the KPI the report header shows.
        var allRows = request.CustomerUuid is null && request.VendorUuid is null && string.IsNullOrWhiteSpace(request.CurrencyCode)
            ? rows
            : await LoadAsync(side, asOf, null, true, null, ct);
        var balance = Math.Max(0m, allRows.Sum(r => r.SignedOpenBase));
        var period  = side == SubledgerSide.Receivable ? await CreditSalesAsync(asOf, 365, ct) : await PurchasesAsync(asOf, 365, ct);

        if (forExport) (page, pageSize) = (1, Math.Max(1, lines.Count));

        return new AgingReport
        {
            ReportType       = side == SubledgerSide.Receivable ? "Receivable" : "Payable",
            AsOfDate         = D(asOf),
            Basis            = basis,
            BucketDays       = buckets.PublicBoundaries,
            BucketLabels     = buckets.Labels,
            BaseCurrencyCode = baseCode,
            Lines            = [.. lines.Skip((page - 1) * pageSize).Take(pageSize)],
            Totals           = totals,
            ByCurrency       = byCurrency,
            DsoOrDpo         = DaysOf(balance, period, 365),
            TotalOutstanding = totals.GrandTotal,
            Page             = page,
            PageSize         = pageSize,
            TotalRecords     = lines.Count,
            TotalPages       = (int)Math.Ceiling(lines.Count / (double)pageSize),
            GeneratedAt      = GeneratedAt
        };
    }

    public async Task<PaginatedResponse<AgingDetailLine>> GetAgingDetailsAsync(
        bool payables, AgingDetailsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var side    = payables ? SubledgerSide.Payable : SubledgerSide.Receivable;
        var asOf    = request.AsOfDate is { } a ? T(a) : Today;
        var basis   = AgingBucketSet.NormalizeBasis(request.Basis);
        var buckets = AgingBucketSet.Parse(request.Buckets);
        if (request.BucketIndex is { } bi && (bi < 0 || bi >= buckets.Count))
            throw new BadRequestException($"Bucket index {bi} is outside 0-{buckets.Count - 1}.");
        var (page, pageSize) = Clamp(request.Page, request.PageSize);

        var rows = await LoadAsync(side, asOf, PartnerOf(side, request.CustomerUuid, request.VendorUuid), false, request.CurrencyCode, ct);
        var details = rows.Select(r => Detail(r, buckets, asOf, basis))
            .Where(d => request.BucketIndex is null || d.BucketIndex == request.BucketIndex)
            .OrderByDescending(d => d.AgeDays).ThenBy(d => d.DocumentNumber, StringComparer.Ordinal)
            .ToList();

        var result = PageOf(details, page, pageSize);
        var names  = await NamesAsync(result.Data.Select(d => d.PartnerId).Distinct().ToList(), ct);
        foreach (var d in result.Data) d.PartnerName = names.GetValueOrDefault(d.PartnerId);
        return result;
    }

    // ══ DSO / DPO (D-12) ════════════════════════════════════════════════════

    public Task<DaysOutstandingResult> GetDsoAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default) =>
        DaysOutstandingAsync(SubledgerSide.Receivable, asOfDate is { } a ? T(a) : Today, periodDays, ct);

    public Task<DaysOutstandingResult> GetDpoAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default) =>
        DaysOutstandingAsync(SubledgerSide.Payable, asOfDate is { } a ? T(a) : Today, periodDays, ct);

    private async Task<DaysOutstandingResult> DaysOutstandingAsync(SubledgerSide side, DateTime asOf, int periodDays, CancellationToken ct)
    {
        CheckPeriod(periodDays);
        var rows    = await LoadAsync(side, asOf, null, includeCredits: true, null, ct);
        var balance = Math.Max(0m, rows.Sum(r => r.SignedOpenBase));
        var amount  = side == SubledgerSide.Receivable
            ? await CreditSalesAsync(asOf, periodDays, ct)
            : await PurchasesAsync(asOf, periodDays, ct);

        return new DaysOutstandingResult
        {
            Measure          = side == SubledgerSide.Receivable ? "DSO" : "DPO",
            AsOfDate         = D(asOf),
            PeriodDays       = periodDays,
            BaseCurrencyCode = await _base.CodeAsync(side, ct),
            Balance          = balance,
            PeriodAmount     = amount,
            Days             = DaysOf(balance, amount, periodDays)
        };
    }

    private static void CheckPeriod(int periodDays)
    {
        if (periodDays is < 1 or > 3660)
            throw new BadRequestException("periodDays must be between 1 and 3660.");
    }

    /// <summary>BR-AG-003/004: balance ÷ period amount × days, 1 dp; 0 when nothing was invoiced.</summary>
    internal static decimal DaysOf(decimal balance, decimal periodAmount, int periodDays) =>
        periodAmount <= 0m ? 0m : Math.Round(balance / periodAmount * periodDays, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// D-12 / D-RPT-5 — credit sales in (asOf − period, asOf]: issued sales invoices (any source but imported opening
    /// balances; never drafts or cancelled ones) less customer credit notes, base at the documents' rate.
    /// </summary>
    internal async Task<decimal> CreditSalesAsync(DateTime asOf, int periodDays, CancellationToken ct)
    {
        var org   = _items.OrganizationId;
        var end   = asOf.Date.AddDays(1);
        var start = end.AddDays(-periodDays);
        var docs  = _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsDelete && i.InvoiceDate >= start && i.InvoiceDate < end
                     && i.SourceType != SalesInvoiceSources.Opening
                     && i.Status != SalesInvoiceStatuses.Draft && i.Status != SalesInvoiceStatuses.Cancelled);

        // Signed (credit notes negative); locked documents in base in SQL, unlocked ones per currency for the D-RPT-6 rule (REV-18).
        var locked = await docs.Where(i => i.BaseGrandTotal != null || i.ExchangeRate != null)
            .SumAsync(i => (decimal?)((i.Status == SalesInvoiceStatuses.CreditNote ? -1m : 1m) * (i.BaseGrandTotal ?? i.GrandTotal * i.ExchangeRate!.Value)), ct) ?? 0m;
        var unlocked = await docs.Where(i => i.BaseGrandTotal == null && i.ExchangeRate == null)
            .GroupBy(i => i.CurrencyCode)
            .Select(g => new { g.Key, Sum = g.Sum(i => (i.Status == SalesInvoiceStatuses.CreditNote ? -1m : 1m) * i.GrandTotal) })
            .ToListAsync(ct);
        return await _base.TotalInBaseAsync(SubledgerSide.Receivable, asOf, locked, unlocked.Select(u => (u.Key, u.Sum)), ct);
    }

    /// <summary>
    /// D-12 — purchases in the period = approved supplier invoices dated in it (COGS is not posted — A44 D-28), base,
    /// excluding imported opening bills.
    /// </summary>
    internal async Task<decimal> PurchasesAsync(DateTime asOf, int periodDays, CancellationToken ct)
    {
        var org   = _items.OrganizationId;
        var end   = asOf.Date.AddDays(1);
        var start = end.AddDays(-periodDays);
        var docs = _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsDelete && i.InvoiceDate >= start && i.InvoiceDate < end
                     && i.MatchStatus == InvoiceMatchStatus.Approved
                     && (i.SourceType == null || i.SourceType != SalesInvoiceSources.Opening));
        var locked = await docs.Where(i => i.BaseTotalAmount != null || i.ExchangeRate != null)
            .SumAsync(i => (decimal?)(i.BaseTotalAmount ?? i.TotalAmount * i.ExchangeRate!.Value), ct) ?? 0m;
        var unlocked = await docs.Where(i => i.BaseTotalAmount == null && i.ExchangeRate == null)
            .GroupBy(i => i.Currency).Select(g => new { g.Key, Sum = g.Sum(i => i.TotalAmount) }).ToListAsync(ct);
        return await _base.TotalInBaseAsync(SubledgerSide.Payable, asOf, locked, unlocked.Select(u => (u.Key, u.Sum)), ct);
    }

    // ══ Balances, open items ════════════════════════════════════════════════

    public Task<PartnerBalanceResult> GetCustomerBalanceAsync(Guid customerId, DateOnly? asOfDate = null, CancellationToken ct = default) =>
        BalanceAsync(SubledgerSide.Receivable, customerId, asOfDate, ct);

    public Task<PartnerBalanceResult> GetVendorBalanceAsync(Guid vendorId, DateOnly? asOfDate = null, CancellationToken ct = default) =>
        BalanceAsync(SubledgerSide.Payable, vendorId, asOfDate, ct);

    private async Task<PartnerBalanceResult> BalanceAsync(SubledgerSide side, Guid partnerId, DateOnly? asOfDate, CancellationToken ct)
    {
        var asOf = asOfDate is { } a ? T(a) : Today;
        var name = await RequirePartnerAsync(side, partnerId, ct);
        var rows = await LoadAsync(side, asOf, partnerId, includeCredits: true, null, ct);

        var debits = rows.Where(r => !r.IsCredit).ToList();
        var oldest = debits.Count == 0 ? (DateTime?)null : debits.Min(r => r.DocumentDate);

        return new PartnerBalanceResult
        {
            PartnerId        = partnerId,
            PartnerName      = name,
            PartnerRole      = RoleOf(side),
            AsOfDate         = D(asOf),
            BaseCurrencyCode = await _base.CodeAsync(side, ct),
            TotalOpen        = debits.Sum(r => r.OpenBase),
            TotalCredits     = rows.Where(r => r.IsCredit).Sum(r => r.OpenBase),
            NetBalance       = rows.Sum(r => r.SignedOpenBase),
            TotalOverdue     = debits.Where(r => r.DueDate.Date < asOf).Sum(r => r.OpenBase),
            OpenItemCount    = rows.Count,
            OldestOpenDate   = oldest is { } o1 ? D(o1) : null,
            DaysOldest       = oldest is { } o ? (asOf - o.Date).Days : null,
            ByCurrency       = rows.GroupBy(r => r.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new PartnerCurrencyBalance
                {
                    CurrencyCode   = g.Key,
                    TotalOpen      = g.Where(r => !r.IsCredit).Sum(r => r.Open),
                    TotalCredits   = g.Where(r => r.IsCredit).Sum(r => r.Open),
                    NetBalance     = g.Sum(r => r.SignedOpen),
                    NetBalanceBase = g.Sum(r => r.SignedOpenBase)
                })
                .ToList()
        };
    }

    public Task<PaginatedResponse<OpenItemDto>> GetCustomerOpenItemsAsync(Guid customerId, OpenItemsFilter filter, CancellationToken ct = default) =>
        OpenItemsAsync(SubledgerSide.Receivable, customerId, filter, ct);

    public Task<PaginatedResponse<OpenItemDto>> GetVendorOpenItemsAsync(Guid vendorId, OpenItemsFilter filter, CancellationToken ct = default) =>
        OpenItemsAsync(SubledgerSide.Payable, vendorId, filter, ct);

    private async Task<PaginatedResponse<OpenItemDto>> OpenItemsAsync(SubledgerSide side, Guid partnerId, OpenItemsFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await RequirePartnerAsync(side, partnerId, ct);
        var asOf = filter.AsOfDate is { } a ? T(a) : Today;
        var (page, pageSize) = Clamp(filter.Page, filter.PageSize);

        var rows  = await LoadAsync(side, asOf, partnerId, filter.IncludeCredits, filter.CurrencyCode, ct);
        var items = rows.Select(r => ToOpenItem(r, asOf)).Where(i => !filter.OverdueOnly || i.IsOverdue).ToList();

        var desc = string.Equals(filter.SortDirection, "Desc", StringComparison.OrdinalIgnoreCase);
        IOrderedEnumerable<OpenItemDto> sorted = (filter.SortBy ?? "DueDate").Trim().ToUpperInvariant() switch
        {
            "DOCUMENTDATE" or "DATE" => desc ? items.OrderByDescending(i => i.DocumentDate) : items.OrderBy(i => i.DocumentDate),
            "AMOUNT"                 => desc ? items.OrderByDescending(i => i.OpenAmountBase) : items.OrderBy(i => i.OpenAmountBase),
            "NUMBER"                 => desc ? items.OrderByDescending(i => i.DocumentNumber, StringComparer.Ordinal)
                                             : items.OrderBy(i => i.DocumentNumber, StringComparer.Ordinal),
            "DUEDATE"                => desc ? items.OrderByDescending(i => i.DueDate) : items.OrderBy(i => i.DueDate),
            _ => throw new BadRequestException($"Unknown sortBy '{filter.SortBy}'. Allowed: DueDate, DocumentDate, Amount, Number.")
        };
        return PageOf(sorted.ThenBy(i => i.DocumentNumber, StringComparer.Ordinal).ToList(), page, pageSize);
    }

    internal static OpenItemDto ToOpenItem(OpenItemRow r, DateTime asOf)
    {
        var overdue = (asOf.Date - r.DueDate.Date).Days;
        return new OpenItemDto
        {
            DocumentType    = r.Type,
            DocumentUuid    = r.DocumentUuid,
            DocumentNumber  = r.DocumentNumber,
            Reference       = r.Reference,
            DocumentDate    = D(r.DocumentDate),
            DueDate         = D(r.DueDate),
            CurrencyCode    = r.CurrencyCode,
            OriginalAmount  = r.Original,
            OpenAmount      = r.SignedOpen,
            OpenAmountBase  = r.SignedOpenBase,
            ExchangeRate    = r.Rate,
            DaysOutstanding = Math.Max(0, (asOf.Date - r.DocumentDate.Date).Days),
            DaysOverdue     = r.IsCredit ? 0 : Math.Max(0, overdue),
            IsOverdue       = !r.IsCredit && overdue > 0,
            IsCredit        = r.IsCredit,
            Status          = r.Status,
            RateMissing     = r.RateMissing
        };
    }

    // ══ Transactions ════════════════════════════════════════════════════════

    /// <summary>
    /// The customer ledger, paged in SQL by business date then posting order (as R2 reads it), with a per-currency
    /// running balance: what was owed before <c>from</c>, plus the in-range rows before the page, plus each row.
    /// </summary>
    public async Task<PaginatedResponse<PartnerTransactionDto>> GetCustomerTransactionsAsync(
        Guid customerId, PartnerTransactionFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await RequirePartnerAsync(SubledgerSide.Receivable, customerId, ct);
        var org = _items.OrganizationId;
        var (page, pageSize) = Clamp(filter.Page, filter.PageSize);
        var (from, endExclusive) = Range(filter.From, filter.To);

        var account = _db.CustomerLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org && e.PartnerId == customerId);
        if (!string.IsNullOrWhiteSpace(filter.CurrencyCode)) account = account.Where(e => e.CurrencyCode == filter.CurrencyCode);

        var inRange = account;
        if (from is { } f)         inRange = inRange.Where(e => e.EntryDate >= f);
        if (endExclusive is { } e) inRange = inRange.Where(x => x.EntryDate < e);

        var running = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (from is { } first)
            foreach (var o in await account.Where(x => x.EntryDate < first).GroupBy(x => x.CurrencyCode)
                         .Select(g => new { g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) }).ToListAsync(ct))
                running[o.Key] = o.Net;

        var total   = await inRange.CountAsync(ct);
        var ordered = inRange.OrderBy(x => x.EntryDate.Date).ThenBy(x => x.SequenceNo);
        var skip    = (page - 1) * pageSize;

        if (skip > 0)
            foreach (var b in await ordered.Take(skip).Select(x => new { x.CurrencyCode, Net = x.DebitAmount - x.CreditAmount })
                         .GroupBy(x => x.CurrencyCode).Select(g => new { g.Key, Net = g.Sum(x => x.Net) }).ToListAsync(ct))
                running[b.Key] = running.GetValueOrDefault(b.Key) + b.Net;

        var rows = await ordered.Skip(skip).Take(pageSize)
            .Select(x => new { x.SequenceNo, x.EntryDate, x.EntryType, x.ReferenceType, x.ReferenceId, x.ReferenceNumber,
                               x.Narration, x.CurrencyCode, x.DebitAmount, x.CreditAmount })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var balance = running.GetValueOrDefault(r.CurrencyCode) + r.DebitAmount - r.CreditAmount;
            running[r.CurrencyCode] = balance;
            return new PartnerTransactionDto
            {
                SequenceNo = r.SequenceNo, EntryDate = r.EntryDate, EntryType = r.EntryType, ReferenceType = r.ReferenceType,
                ReferenceId = r.ReferenceId, ReferenceNumber = r.ReferenceNumber, Narration = r.Narration,
                CurrencyCode = r.CurrencyCode, Debit = r.DebitAmount, Credit = r.CreditAmount, RunningBalance = balance
            };
        }).ToList();

        return new PaginatedResponse<PartnerTransactionDto>
        {
            Data = items, Page = page, PageSize = pageSize, TotalRecords = total, TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    /// <summary>
    /// The supplier ledger. Its rows carry no currency (pre-A35 design), so each is given its document's currency
    /// (invoice, payment, note → its invoice) and the per-currency running balance is computed over the vendor's whole
    /// ledger before paging (bounded by <see cref="MaxVendorLedgerEntries"/> — D-RPT-6).
    /// </summary>
    public async Task<PaginatedResponse<PartnerTransactionDto>> GetVendorTransactionsAsync(
        Guid vendorId, PartnerTransactionFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await RequirePartnerAsync(SubledgerSide.Payable, vendorId, ct);
        var (page, pageSize) = Clamp(filter.Page, filter.PageSize);
        var (from, endExclusive) = Range(filter.From, filter.To);

        var entries = await VendorLedgerAsync(vendorId, ct);
        if (!string.IsNullOrWhiteSpace(filter.CurrencyCode))
            entries = entries.Where(x => string.Equals(x.CurrencyCode, filter.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        var inRange = WithRunningBalance(entries, from, endExclusive, out _);
        return PageOf(inRange, page, pageSize);
    }

    /// <summary>
    /// Business-date order (posting order within a day), a running balance per currency, and the entries in
    /// [from, endExclusive); <paramref name="opening"/> = per currency, the balance before <paramref name="from"/>.
    /// </summary>
    internal static List<PartnerTransactionDto> WithRunningBalance(
        List<PartnerTransactionDto> entries, DateTime? from, DateTime? endExclusive, out Dictionary<string, decimal> opening)
    {
        opening = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var running = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var inRange = new List<PartnerTransactionDto>();
        foreach (var x in entries.OrderBy(x => x.EntryDate.Date).ThenBy(x => x.SequenceNo))
        {
            var balance = running.GetValueOrDefault(x.CurrencyCode) + x.Debit - x.Credit;
            running[x.CurrencyCode] = balance;
            x.RunningBalance = balance;
            if (from is { } f && x.EntryDate < f) { opening[x.CurrencyCode] = balance; continue; }
            if (endExclusive is { } e && x.EntryDate >= e) continue;
            inRange.Add(x);
        }
        return inRange;
    }

    /// <summary>One vendor's supplier-ledger rows in posting order, each with its document's currency.</summary>
    internal async Task<List<PartnerTransactionDto>> VendorLedgerAsync(Guid vendorId, CancellationToken ct)
    {
        var org = _items.OrganizationId;
        var rows = await _db.SupplierLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org && e.SupplierId == vendorId)
            .OrderBy(e => e.SequenceNo)
            .Take(MaxVendorLedgerEntries + 1)
            .Select(e => new PartnerTransactionDto
            {
                SequenceNo = e.SequenceNo, EntryDate = e.EntryDate, EntryType = e.TransactionType, ReferenceType = e.ReferenceType,
                ReferenceId = e.ReferenceId, ReferenceNumber = e.ReferenceNo, Narration = e.Narration,
                Debit = e.DebitAmount, Credit = e.CreditAmount
            })
            .ToListAsync(ct);
        if (rows.Count > MaxVendorLedgerEntries)
            throw new BadRequestException($"This vendor's ledger holds more than {MaxVendorLedgerEntries} entries; use the master payables ledger export.");

        var baseCode   = await _base.CodeAsync(SubledgerSide.Payable, ct);
        var currencies = await VendorDocumentCurrenciesAsync(org, rows.Select(r => r.ReferenceId).Distinct().ToList(), ct);
        foreach (var r in rows) r.CurrencyCode = currencies.GetValueOrDefault(r.ReferenceId) ?? baseCode;
        return rows;
    }

    private async Task<Dictionary<Guid, string>> VendorDocumentCurrenciesAsync(Guid org, List<Guid> ids, CancellationToken ct)
    {
        var map = new Dictionary<Guid, string>();
        var invoiceCurrency = _db.Invoices.IgnoreQueryFilters().Where(i => i.OrganizationId == org);
        foreach (var chunk in ids.Chunk(1000))
        {
            foreach (var i in await invoiceCurrency.AsNoTracking().Where(i => chunk.Contains(i.UUID))
                         .Select(i => new { i.UUID, i.Currency }).ToListAsync(ct))
                map[i.UUID] = i.Currency;

            foreach (var p in await _db.SupplierPayments.IgnoreQueryFilters().AsNoTracking()
                         .Where(p => p.OrganizationId == org && chunk.Contains(p.UUID))
                         .Select(p => new { p.UUID, p.CurrencyCode }).ToListAsync(ct))
                map[p.UUID] = p.CurrencyCode;

            foreach (var c in await _db.CreditNotes.IgnoreQueryFilters().AsNoTracking()
                         .Where(c => c.OrganizationId == org && chunk.Contains(c.UUID))
                         .Select(c => new { c.UUID, Currency = invoiceCurrency.Where(i => i.UUID == (c.InvoiceUuid ?? c.AppliedToInvoiceUuid)).Select(i => i.Currency).FirstOrDefault() })
                         .ToListAsync(ct))
                if (c.Currency is not null) map[c.UUID] = c.Currency;

            foreach (var d in await _db.DebitNotes.IgnoreQueryFilters().AsNoTracking()
                         .Where(d => d.OrganizationId == org && chunk.Contains(d.UUID))
                         .Select(d => new { d.UUID, Currency = invoiceCurrency.Where(i => i.UUID == (d.InvoiceUuid ?? d.AppliedToInvoiceUuid)).Select(i => i.Currency).FirstOrDefault() })
                         .ToListAsync(ct))
                if (d.Currency is not null) map[d.UUID] = d.Currency;
        }
        return map;
    }

    // ══ Summaries ═══════════════════════════════════════════════════════════

    public async Task<ArSummaryKpis> GetArSummaryAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default)
    {
        CheckPeriod(periodDays);
        var asOf    = asOfDate is { } a ? T(a) : Today;
        var buckets = AgingBucketSet.From(null);
        var rows    = await LoadAsync(SubledgerSide.Receivable, asOf, null, includeCredits: true, null, ct);
        var lines   = RollUp(rows, buckets, asOf, AgingBucketSet.DueDateBasis, includeDetails: false);
        var debits  = rows.Where(r => !r.IsCredit).ToList();

        var invoiced  = await CreditSalesAsync(asOf, periodDays, ct);
        var collected = await CollectedAsync(asOf, periodDays, ct);
        var net       = rows.Sum(r => r.SignedOpenBase);

        return new ArSummaryKpis
        {
            AsOfDate                  = D(asOf),
            BaseCurrencyCode          = await _base.CodeAsync(SubledgerSide.Receivable, ct),
            PeriodDays                = periodDays,
            TotalOutstanding          = debits.Sum(r => r.OpenBase),
            TotalOverdue              = debits.Where(r => r.DueDate.Date < asOf).Sum(r => r.OpenBase),
            TotalCurrent              = debits.Where(r => r.DueDate.Date >= asOf).Sum(r => r.OpenBase),
            UnappliedCredits          = rows.Where(r => r.IsCredit).Sum(r => r.OpenBase),
            NetReceivable             = net,
            TotalCustomersWithBalance = lines.Count(l => l.NetBalance > 0m),
            TotalCustomersOverdue     = lines.Count(l => l.TotalOverdue > 0m),
            Dso                       = DaysOf(Math.Max(0m, net), invoiced, periodDays),
            DaysBeyondTerms           = WeightedDaysOverdue(debits, asOf),
            InvoicedInPeriod          = invoiced,
            CollectedInPeriod         = collected,
            CollectionRate            = invoiced <= 0m ? 0m : Math.Round(collected / invoiced * 100m, 1, MidpointRounding.AwayFromZero),
            AgingBuckets              = Enumerable.Range(0, buckets.Count).Select(b => lines.Sum(l => l.BucketAmounts[b])).ToArray(),
            AgingLabels               = buckets.Labels
        };
    }

    public async Task<ApSummaryKpis> GetApSummaryAsync(DateOnly? asOfDate, int periodDays = 365, CancellationToken ct = default)
    {
        CheckPeriod(periodDays);
        var asOf    = asOfDate is { } a ? T(a) : Today;
        var buckets = AgingBucketSet.From(null);
        var rows    = await LoadAsync(SubledgerSide.Payable, asOf, null, includeCredits: true, null, ct);
        var lines   = RollUp(rows, buckets, asOf, AgingBucketSet.DueDateBasis, includeDetails: false);
        var debits  = rows.Where(r => !r.IsCredit).ToList();
        var notDue  = debits.Where(r => r.DueDate.Date >= asOf).ToList();
        var net     = rows.Sum(r => r.SignedOpenBase);
        var bought  = await PurchasesAsync(asOf, periodDays, ct);

        return new ApSummaryKpis
        {
            AsOfDate                  = D(asOf),
            BaseCurrencyCode          = await _base.CodeAsync(SubledgerSide.Payable, ct),
            PeriodDays                = periodDays,
            TotalOutstanding          = debits.Sum(r => r.OpenBase),
            TotalOverdue              = debits.Where(r => r.DueDate.Date < asOf).Sum(r => r.OpenBase),
            TotalCurrent              = notDue.Sum(r => r.OpenBase),
            TotalDueThisWeek          = notDue.Where(r => r.DueDate.Date <= asOf.AddDays(7)).Sum(r => r.OpenBase),
            TotalDueThisMonth         = notDue.Where(r => r.DueDate.Date <= asOf.AddDays(30)).Sum(r => r.OpenBase),
            UnappliedCredits          = rows.Where(r => r.IsCredit).Sum(r => r.OpenBase),
            NetPayable                = net,
            TotalVendorsWithBalance   = lines.Count(l => l.NetBalance > 0m),
            TotalVendorsOverdue       = lines.Count(l => l.TotalOverdue > 0m),
            Dpo                       = DaysOf(Math.Max(0m, net), bought, periodDays),
            DiscountOpportunityAmount = _discounts is null ? 0m : await _discounts.GetDiscountOpportunityBaseAsync(D(asOf), ct),
            AgingBuckets              = Enumerable.Range(0, buckets.Count).Select(b => lines.Sum(l => l.BucketAmounts[b])).ToArray(),
            AgingLabels               = buckets.Labels
        };
    }

    /// <summary>Customer payments received in the period (not reversed, not bounced by the as-of day, no POS non-cash), base.</summary>
    private async Task<decimal> CollectedAsync(DateTime asOf, int periodDays, CancellationToken ct)
    {
        var org   = _items.OrganizationId;
        var end   = asOf.Date.AddDays(1);
        var start = end.AddDays(-periodDays);
        var bounces = _db.CustomerLedgerEntries.IgnoreQueryFilters().Where(e =>
            e.OrganizationId == org && e.EntryType == CustomerLedgerEntryTypes.Payment && e.ReferenceType == "CustomerPayment"
            && e.DebitAmount > 0m && e.EntryDate < end);

        var payments = _db.CustomerPayments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && p.PaymentDate >= start && p.PaymentDate < end
                     && p.Status != CustomerPaymentStatuses.Reversed
                     && p.PaymentMethod != CustomerPaymentMethods.StoreCredit && p.PaymentMethod != CustomerPaymentMethods.Exchange
                     && !bounces.Any(b => b.ReferenceId == p.UUID));
        var locked = await payments.Where(p => p.AmountBase != null || p.ExchangeRate != null)
            .SumAsync(p => (decimal?)(p.AmountBase ?? p.Amount * p.ExchangeRate!.Value), ct) ?? 0m;
        var unlocked = await payments.Where(p => p.AmountBase == null && p.ExchangeRate == null)
            .GroupBy(p => p.CurrencyCode).Select(g => new { g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
        return await _base.TotalInBaseAsync(SubledgerSide.Receivable, asOf, locked, unlocked.Select(u => (u.Key, u.Sum)), ct);
    }

    private static decimal WeightedDaysOverdue(List<OpenItemRow> debits, DateTime asOf)
    {
        var overdue = debits.Where(r => r.DueDate.Date < asOf).ToList();
        var amount  = overdue.Sum(r => r.OpenBase);
        if (amount <= 0m) return 0m;
        var weighted = overdue.Sum(r => r.OpenBase * (asOf - r.DueDate.Date).Days);
        return Math.Round(weighted / amount, 1, MidpointRounding.AwayFromZero);
    }

    // ══ Shared ══════════════════════════════════════════════════════════════

    internal async Task<List<OpenItemRow>> LoadAsync(
        SubledgerSide side, DateTime asOf, Guid? partnerId, bool includeCredits, string? currency, CancellationToken ct)
    {
        var rows = await _items.ForSideAsync(side, asOf, partnerId, includeCredits, ct);
        if (rows.Count > MaxOpenItems)
            throw new BadRequestException($"More than {MaxOpenItems} documents are open; filter the report to a partner or currency.");

        rows = SubledgerOpenItems.WithBaseCode(rows, await _base.CodeAsync(side, ct));
        if (!string.IsNullOrWhiteSpace(currency))
            rows = rows.Where(r => string.Equals(r.CurrencyCode, currency.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return await _base.ResolveRatesAsync(rows, side, asOf, ct);
    }

    internal Task<string> BaseCodeAsync(SubledgerSide side, CancellationToken ct) => _base.CodeAsync(side, ct);

    internal static AgingDetailLine Detail(OpenItemRow r, AgingBucketSet buckets, DateTime asOf, string basis)
    {
        var age   = AgingBucketSet.AgeOf(r.DocumentDate, r.DueDate, asOf, basis);
        var index = buckets.IndexOf(age);
        return new AgingDetailLine
        {
            DocumentType   = r.Type,
            DocumentUuid   = r.DocumentUuid,
            DocumentNumber = r.DocumentNumber,
            Reference      = r.Reference,
            PartnerId      = r.PartnerId,
            DocumentDate   = D(r.DocumentDate),
            DueDate        = D(r.DueDate),
            CurrencyCode   = r.CurrencyCode,
            OriginalAmount = r.Original,
            OpenAmount     = r.Open,
            OpenAmountBase = r.OpenBase,
            ExchangeRate   = r.Rate,
            DaysOverdue    = Math.Max(0, (asOf.Date - r.DueDate.Date).Days),
            AgeDays        = age,
            BucketIndex    = index,
            BucketLabel    = buckets.Labels[index],
            Status         = r.Status,
            RateMissing    = r.RateMissing
        };
    }

    /// <summary>Per partner: debit items into buckets (base), credits beside them; partners with only credits too.</summary>
    internal static List<AgingPartnerLine> RollUp(
        List<OpenItemRow> rows, AgingBucketSet buckets, DateTime asOf, string basis, bool includeDetails)
    {
        var lines = new List<AgingPartnerLine>();
        foreach (var g in rows.GroupBy(r => r.PartnerId))
        {
            var line    = new AgingPartnerLine { PartnerId = g.Key, BucketAmounts = new decimal[buckets.Count] };
            var details = includeDetails ? new List<AgingDetailLine>() : null;
            DateTime? oldest = null;
            foreach (var r in g)
            {
                if (r.IsCredit) { line.UnappliedCredits += r.OpenBase; continue; }

                var d = Detail(r, buckets, asOf, basis);
                line.BucketAmounts[d.BucketIndex] += r.OpenBase;
                line.TotalOutstanding += r.OpenBase;
                line.OpenItemCount++;
                if (r.DueDate.Date < asOf.Date)
                {
                    line.TotalOverdue += r.OpenBase;
                    line.DaysBeyondTerms = Math.Max(line.DaysBeyondTerms ?? 0, d.DaysOverdue);
                }
                if (oldest is null || r.DocumentDate < oldest) oldest = r.DocumentDate;
                details?.Add(d);
            }
            line.OldestOpenDate = oldest is { } o ? D(o) : null;
            line.NetBalance     = line.TotalOutstanding - line.UnappliedCredits;
            line.OldestBucket   = Array.FindLastIndex(line.BucketAmounts, x => x != 0m);
            if (details is not null)
                line.Details = [.. details.OrderByDescending(d => d.AgeDays).ThenBy(d => d.DocumentNumber, StringComparer.Ordinal)];
            lines.Add(line);
        }
        return lines;
    }

    internal static List<AgingPartnerLine> Sort(List<AgingPartnerLine> lines, string? sortBy, string? direction)
    {
        var asc  = string.Equals(direction, "Asc", StringComparison.OrdinalIgnoreCase);
        var desc = string.Equals(direction, "Desc", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(direction) && !asc && !desc)
            throw new BadRequestException($"Unknown sortDirection '{direction}'. Allowed: Asc, Desc.");

        IOrderedEnumerable<AgingPartnerLine> ordered = (sortBy ?? "TotalOutstanding").Trim().ToUpperInvariant() switch
        {
            // By name: ascending unless Desc was asked explicitly.
            "PARTNER" => desc
                ? lines.OrderByDescending(l => l.PartnerName ?? "", StringComparer.OrdinalIgnoreCase)
                : lines.OrderBy(l => l.PartnerName is null).ThenBy(l => l.PartnerName, StringComparer.OrdinalIgnoreCase),
            "TOTALOVERDUE" =>
                asc ? lines.OrderBy(l => l.TotalOverdue) : lines.OrderByDescending(l => l.TotalOverdue),
            "OLDESTBUCKET" =>
                asc ? lines.OrderBy(l => l.OldestBucket).ThenBy(l => l.TotalOutstanding)
                    : lines.OrderByDescending(l => l.OldestBucket).ThenByDescending(l => l.TotalOutstanding),
            "TOTALOUTSTANDING" or "" =>
                asc ? lines.OrderBy(l => l.TotalOutstanding) : lines.OrderByDescending(l => l.TotalOutstanding),
            _ => throw new BadRequestException(
                $"Unknown sortBy '{sortBy}'. Allowed: Partner, TotalOutstanding, TotalOverdue, OldestBucket.")
        };
        return [.. ordered.ThenBy(l => l.PartnerId)];
    }

    private async Task NameAsync(List<AgingPartnerLine> lines, CancellationToken ct)
    {
        var info = await PartnersAsync(lines.Select(l => l.PartnerId).ToList(), ct);
        foreach (var l in lines)
            if (info.TryGetValue(l.PartnerId, out var p)) { l.PartnerName = p.Name; l.PartnerCode = p.Code; }
    }

    /// <summary>Name (+ code, email, address when the A48 partner lookup is registered) of each partner.</summary>
    internal async Task<Dictionary<Guid, PartnerSummary>> PartnersAsync(List<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, PartnerSummary>();
        if (ids.Count == 0) return result;

        if (_partners is not null)
        {
            var org = _items.OrganizationId;
            foreach (var (k, p) in await _partners.GetManyAsync(org, ids, null, ct))
                result[k] = new PartnerSummary(p.Name, p.Code, p.Email ?? p.ContactEmail, p.Address);
        }

        var missing = ids.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0 && _names is not null)
            foreach (var chunk in missing.Chunk(2000))
                foreach (var (k, v) in await _names.GetNamesAsync(chunk))
                    result[k] = new PartnerSummary(v, null, null, null);
        return result;
    }

    internal async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(List<Guid> ids, CancellationToken ct) =>
        (await PartnersAsync(ids, ct)).ToDictionary(kv => kv.Key, kv => kv.Value.Name);

    /// <summary>
    /// 404 when the partner is unknown to this organization: not found by the partner lookup (or the name lookup) and
    /// with no Finance history either. Returns the partner's name when known.
    /// </summary>
    internal async Task<string?> RequirePartnerAsync(SubledgerSide side, Guid partnerId, CancellationToken ct)
    {
        if (partnerId == Guid.Empty) throw new NotFoundException("Business partner", partnerId);
        var info = (await PartnersAsync([partnerId], ct)).GetValueOrDefault(partnerId);
        if (info is null && !await HasHistoryAsync(side, partnerId, ct))
            throw new NotFoundException("Business partner", partnerId);
        return info?.Name;
    }

    private async Task<bool> HasHistoryAsync(SubledgerSide side, Guid partnerId, CancellationToken ct)
    {
        var org = _items.OrganizationId;
        return side == SubledgerSide.Receivable
            ? await _db.CustomerLedgerEntries.IgnoreQueryFilters().AnyAsync(e => e.OrganizationId == org && e.PartnerId == partnerId, ct)
              || await _db.SalesInvoices.IgnoreQueryFilters().AnyAsync(i => i.OrganizationId == org && i.PartnerId == partnerId, ct)
            : await _db.SupplierLedgerEntries.IgnoreQueryFilters().AnyAsync(e => e.OrganizationId == org && e.SupplierId == partnerId, ct)
              || await _db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.OrganizationId == org && i.SupplierId == partnerId, ct);
    }

    internal static string RoleOf(SubledgerSide side) => side == SubledgerSide.Receivable ? "Customer" : "Vendor";

    internal static (DateTime? From, DateTime? EndExclusive) Range(DateOnly? from, DateOnly? to)
    {
        DateTime? start = from is { } f ? T(f) : null;
        DateTime? end   = to is { } t ? T(t).AddDays(1) : null;
        if (start is { } s && end is { } e && e <= s)
            throw new BadRequestException("The date range ends before it starts.");
        return (start, end);
    }
}

/// <summary>What a report shows of a partner.</summary>
internal sealed record PartnerSummary(string Name, string? Code, string? Email, string? Address);
