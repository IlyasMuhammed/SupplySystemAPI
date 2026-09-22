using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Reports.Services;

/// <summary>
/// The sales reports of A29 §15. Reads the sale orders straight from Demand's context, as the other
/// reports read their modules', and asks Suppliers and Lookups only for what an order stores by id: the
/// customer's name and the currency's code.
/// </summary>
internal sealed class SalesReportService : ISalesReportService
{
    /// <summary>The most orders one printed register carries.</summary>
    internal const int MaxExportRows = 10_000;

    private const int MaxPageSize = 100;

    private static readonly IReadOnlyList<string> Statuses =
        [.. Enum.GetValues<SaleOrderStatus>().Select(EnumCode<SaleOrderStatus>.Of)];

    private static readonly IReadOnlyList<string> DeliveryModes =
        [.. Enum.GetValues<Demand.Domain.DeliveryMode>().Select(EnumCode<Demand.Domain.DeliveryMode>.Of)];

    private readonly DemandDbContext            _demand;
    private readonly ISupplierNameLookupService _names;
    private readonly ILookupsService            _lookups;
    private readonly IPoDocumentTemplateService _templates;
    private readonly TimeProvider               _clock;

    public SalesReportService(
        DemandDbContext demand, ISupplierNameLookupService names, ILookupsService lookups,
        IPoDocumentTemplateService templates, TimeProvider? clock = null)
    {
        _demand    = demand;
        _names     = names;
        _lookups   = lookups;
        _templates = templates;
        _clock     = clock ?? TimeProvider.System;
    }

    public Task<SalesOrderRegisterReport> GetOrderRegisterAsync(SalesOrderRegisterFilter filter) =>
        BuildRegisterAsync(filter, forExport: false);

    public Task<SalesOrderRegisterReport> GetOrderRegisterForExportAsync(SalesOrderRegisterFilter filter) =>
        BuildRegisterAsync(filter, forExport: true);

    // ── R1 Sales Order Register ──────────────────────────────────────────────

    private async Task<SalesOrderRegisterReport> BuildRegisterAsync(SalesOrderRegisterFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.DateFrom is { } f && filter.DateTo is { } t && f.Date > t.Date)
            throw new BadRequestException("The register's start date is after its end date.");

        var status = Normalize(filter.Status, Statuses, "a sale order status");
        var mode   = Normalize(filter.DeliveryMode, DeliveryModes, "a delivery mode");

        var query = _demand.SaleOrders.AsNoTracking().Where(o => !o.IsDeleted);

        if (filter.DateFrom is { } from)  query = query.Where(o => o.OrderDate >= from.Date);
        if (filter.DateTo is { } to)      query = query.Where(o => o.OrderDate < to.Date.AddDays(1));
        if (status is not null)           query = query.Where(o => o.Status == status);
        if (mode is not null)             query = query.Where(o => o.DeliveryMode == mode);
        if (filter.PartnerId is { } partner) query = query.Where(o => o.PartnerId == partner);

        var total = await query.CountAsync();

        if (forExport && total > MaxExportRows)
            throw new BadRequestException(
                $"{total} orders match, more than the {MaxExportRows} one document carries. Narrow the date range or add a filter.");

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, total))
            : (Math.Max(1, filter.Page), Math.Clamp(filter.PageSize, 1, MaxPageSize));

        var sums = await query
            .GroupBy(o => o.CurrencyId)
            .Select(g => new
            {
                CurrencyId = g.Key,
                Count      = g.Count(),
                Subtotal   = g.Sum(x => x.Subtotal),
                Discount   = g.Sum(x => x.DiscountAmount),
                Tax        = g.Sum(x => x.TaxAmount),
                Grand      = g.Sum(x => x.GrandTotal)
            })
            .ToListAsync();

        var rows = await query
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.UUID, o.SoNumber, o.OrderDate, o.ExpectedDeliveryDate, o.PartnerId, o.Status, o.DeliveryMode, o.CurrencyId,
                LineCount = o.Lines.Count, o.Subtotal, o.DiscountAmount, o.TaxAmount, o.GrandTotal
            })
            .ToListAsync();

        var partnerIds = rows.Select(r => r.PartnerId).Concat(filter.PartnerId is { } p ? [p] : []).Distinct().ToList();
        var names      = partnerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : new Dictionary<Guid, string>(await _names.GetNamesAsync(partnerIds));

        var currencies = _lookups.GetCurrencies()
            .Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().Code!.Trim());
        string CodeOf(Guid currencyId) => currencies.GetValueOrDefault(currencyId) ?? "?";

        var template = await _templates.GetActiveAsync();

        return new SalesOrderRegisterReport
        {
            CompanyName = string.IsNullOrWhiteSpace(template?.CompanyName) ? null : template.CompanyName.Trim(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new SalesOrderRegisterCriteria
            {
                DateFrom     = filter.DateFrom?.Date,
                DateTo       = filter.DateTo?.Date,
                Status       = status,
                PartnerId    = filter.PartnerId,
                CustomerName = filter.PartnerId is { } named ? names.GetValueOrDefault(named) : null,
                DeliveryMode = mode
            },
            Items = [.. rows.Select(r => new SalesOrderRegisterItem
            {
                Uuid                 = r.UUID,
                SoNumber             = r.SoNumber,
                OrderDate            = r.OrderDate,
                ExpectedDeliveryDate = r.ExpectedDeliveryDate,
                PartnerId            = r.PartnerId,
                CustomerName         = names.GetValueOrDefault(r.PartnerId),
                Status               = r.Status,
                DeliveryMode         = r.DeliveryMode,
                CurrencyCode         = CodeOf(r.CurrencyId),
                LineCount            = r.LineCount,
                Subtotal             = r.Subtotal,
                DiscountAmount       = r.DiscountAmount,
                TaxAmount            = r.TaxAmount,
                GrandTotal           = r.GrandTotal
            })],
            // Two currency ids the catalog no longer knows are both "?", and one row.
            Totals = [.. sums
                .GroupBy(s => CodeOf(s.CurrencyId))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new SalesOrderRegisterTotal
                {
                    CurrencyCode   = g.Key,
                    OrderCount     = g.Sum(s => s.Count),
                    Subtotal       = g.Sum(s => s.Subtotal),
                    DiscountAmount = g.Sum(s => s.Discount),
                    TaxAmount      = g.Sum(s => s.Tax),
                    GrandTotal     = g.Sum(s => s.Grand)
                })],
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>A filter word in the orders' own upper case, or a refusal that lists what is valid.</summary>
    private static string? Normalize(string? value, IReadOnlyList<string> valid, string what)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized)) return null;

        return valid.Contains(normalized)
            ? normalized
            : throw new BadRequestException($"'{value}' is not {what}. Valid: {string.Join(", ", valid)}.");
    }
}
