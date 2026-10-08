using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Models;
using SMS.Shared.Authorization;

namespace SMS.Modules.Reports.Services;

public interface IOperationsKpiService
{
    /// <summary>Sales, fulfilment, manufacturing and receivables KPIs over the last <paramref name="windowDays"/> days, limited to what <paramref name="user"/> may see.</summary>
    Task<OperationsKpiModel> GetAsync(ClaimsPrincipal user, int windowDays = 90, CancellationToken ct = default);
}

/// <summary>
/// The KPI dashboard's second half: the modules built after it (sales pre-order, sale orders, fulfilment routes,
/// manufacturing, receivables). Rolling window, so a KPI moves when the business does instead of averaging over all
/// history. Every metric carries its basis and is null with none — the original KPIs' "0 = Alert" for an empty
/// system is exactly what this avoids.
/// <para>
/// Tenant-scoped through each context's query filter, like every report here. Reads only the columns each KPI needs;
/// the arithmetic that crosses modules (sale order date vs delivery issue) is done in memory on those projections.
/// </para>
/// </summary>
internal sealed class OperationsKpiService : IOperationsKpiService
{
    private static readonly string[] QuoteWon      = ["ACCEPTED", "CONVERTED"];
    private static readonly string[] QuoteDecided  = ["ACCEPTED", "CONVERTED", "REJECTED", "EXPIRED"];
    private static readonly string[] InquiryDecided = ["QUOTED", "DECLINED"];
    private static readonly string[] OpenSaleOrder = ["CONFIRMED", "PARTIALLY_FULFILLED"];
    private static readonly string[] DeliveryCompleted = ["DELIVERED", "PARTIALLY_DELIVERED", "CLOSED", "SHORT_CLOSED"];
    private static readonly string[] ProductionFinished = ["COMPLETED", "CLOSED"];
    private static readonly string[] InvoiceOpen   = ["ISSUED", "PARTIALLY_PAID", "OVERDUE"];
    private static readonly string[] InvoiceBilled = ["ISSUED", "PARTIALLY_PAID", "OVERDUE", "PAID"];

    private readonly DemandDbContext    _demand;
    private readonly LogisticsDbContext _logistics;
    private readonly MaterialDbContext  _material;
    private readonly FinanceDbContext   _finance;

    public OperationsKpiService(DemandDbContext demand, LogisticsDbContext logistics, MaterialDbContext material, FinanceDbContext finance)
    {
        _demand    = demand;
        _logistics = logistics;
        _material  = material;
        _finance   = finance;
    }

    public async Task<OperationsKpiModel> GetAsync(ClaimsPrincipal user, int windowDays = 90, CancellationToken ct = default)
    {
        bool Can(params string[] any) => any.Any(user.HasPermission);

        windowDays = Math.Clamp(windowDays, 7, 365);
        var now   = DateTime.UtcNow;
        var today = now.Date;
        var from  = today.AddDays(-windowDays);

        var model = new OperationsKpiModel { From = from, To = now, WindowDays = windowDays };

        if (Can(PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_INQUIRY_VIEW))
            model.Sales = await SalesAsync(Can, from, today, ct);

        if (Can(PermissionCodes.DELIVERY_VIEW))
            model.Fulfilment = await FulfilmentAsync(from, ct);

        if (Can(PermissionCodes.PROD_VIEW))
            model.Manufacturing = await ManufacturingAsync(from, ct);

        if (Can(PermissionCodes.SALES_INVOICE_VIEW))
            model.Receivables = await ReceivablesAsync(from, today, windowDays, ct);

        return model;
    }

    // ── Sales ─────────────────────────────────────────────────────────────────

    private async Task<SalesKpis> SalesAsync(Func<string[], bool> can, DateTime from, DateTime today, CancellationToken ct)
    {
        var kpis = new SalesKpis();

        if (can([PermissionCodes.SALE_QUOTATION_VIEW]))
        {
            var decided = await _demand.SaleQuotations
                .Where(q => q.CreatedDate >= from && QuoteDecided.Contains(q.Status))
                .Select(q => q.Status).ToListAsync(ct);
            kpis.QuoteWinRate = KpiMetric.Rate(decided.Count(QuoteWon.Contains), decided.Count);
        }

        if (can([PermissionCodes.SALE_INQUIRY_VIEW]))
        {
            var decided = await _demand.SaleInquiries
                .Where(i => i.ReceivedDate >= from && InquiryDecided.Contains(i.Status))
                .Select(i => i.Status).ToListAsync(ct);
            kpis.InquiryConversionRate = KpiMetric.Rate(decided.Count(s => s == "QUOTED"), decided.Count);
        }

        if (can([PermissionCodes.SALE_ORDER_VIEW]))
        {
            var orders = _demand.SaleOrders.Where(s => !s.IsDeleted);

            var placed = await orders.Where(s => s.OrderDate >= from && s.Status != "DRAFT")
                .Select(s => s.Status).ToListAsync(ct);
            kpis.OrderCancellationRate = KpiMetric.Rate(placed.Count(s => s == "CANCELLED"), placed.Count);

            var open = await orders.Where(s => OpenSaleOrder.Contains(s.Status))
                .Select(s => s.ExpectedDeliveryDate).ToListAsync(ct);
            kpis.LateOpenOrderRate = KpiMetric.Rate(open.Count(d => d.HasValue && d.Value.Date < today), open.Count);
        }

        return kpis;
    }

    // ── Fulfilment ────────────────────────────────────────────────────────────

    private async Task<FulfilmentKpis> FulfilmentAsync(DateTime from, CancellationToken ct)
    {
        var issued = await _logistics.DeliveryOrders
            .Where(d => !d.IsDelete && d.Direction != "INBOUND" && d.GoodsIssuedAt != null && d.GoodsIssuedAt >= from)
            .Select(d => new { d.Id, d.Status, d.GoodsIssuedAt, Due = d.PromisedDate ?? d.RequestedDate, d.SaleOrderUuid })
            .ToListAsync(ct);

        // On time: left on or before the date promised to the customer (a delivery with no date can't be late or early).
        var dated  = issued.Where(d => d.Due.HasValue).ToList();
        var onTime = dated.Count(d => d.GoodsIssuedAt!.Value.Date <= d.Due!.Value.Date);

        // In full: every line of a completed delivery arrived complete.
        var completedIds = issued.Where(d => DeliveryCompleted.Contains(d.Status)).Select(d => d.Id).ToList();
        var shortIds = completedIds.Count == 0 ? [] : await _logistics.DeliveryOrderLines
            .Where(l => completedIds.Contains(l.DeliveryOrderId) && l.QtyDelivered < l.QtyOrdered)
            .Select(l => l.DeliveryOrderId).Distinct().ToListAsync(ct);

        // Order to ship: the sale order's date (Demand) against the delivery's goods issue (Logistics).
        var soUuids = issued.Where(d => d.SaleOrderUuid != null).Select(d => d.SaleOrderUuid!.Value).Distinct().ToList();
        var orderDates = soUuids.Count == 0 ? new Dictionary<Guid, DateTime>() : await _demand.SaleOrders
            .Where(s => soUuids.Contains(s.UUID))
            .ToDictionaryAsync(s => s.UUID, s => s.OrderDate, ct);
        var leadDays = issued
            .Where(d => d.SaleOrderUuid != null && orderDates.ContainsKey(d.SaleOrderUuid.Value))
            .Select(d => (d.GoodsIssuedAt!.Value - orderDates[d.SaleOrderUuid!.Value]).TotalDays)
            .Where(days => days >= 0)
            .ToList();

        return new FulfilmentKpis
        {
            OnTimeShipmentRate = KpiMetric.Rate(onTime, dated.Count),
            InFullRate         = KpiMetric.Rate(completedIds.Count - shortIds.Count, completedIds.Count),
            OrderToShipDays    = KpiMetric.Average(leadDays)
        };
    }

    // ── Manufacturing ─────────────────────────────────────────────────────────

    private async Task<ManufacturingKpis> ManufacturingAsync(DateTime from, CancellationToken ct)
    {
        var finished = await _material.ProductionOrders
            .Where(p => ProductionFinished.Contains(p.Status) && p.ActualEndDate != null && p.ActualEndDate >= from)
            .Select(p => new
            {
                p.RequiredDate, p.ActualStartDate, p.ActualEndDate,
                p.PlannedQuantity, p.ProducedQuantity, p.AcceptedQuantity
            })
            .ToListAsync(ct);

        var cycle = finished
            .Where(p => p.ActualStartDate.HasValue && p.ActualEndDate >= p.ActualStartDate)
            .Select(p => (p.ActualEndDate!.Value - p.ActualStartDate!.Value).TotalDays)
            .ToList();

        return new ManufacturingKpis
        {
            OnTimeCompletionRate = KpiMetric.Rate(finished.Count(p => p.ActualEndDate!.Value.Date <= p.RequiredDate.Date), finished.Count),
            PlanAttainment       = KpiMetric.Ratio(finished.Sum(p => p.ProducedQuantity), finished.Sum(p => p.PlannedQuantity), finished.Count),
            FirstPassYield       = KpiMetric.Ratio(finished.Sum(p => p.AcceptedQuantity), finished.Sum(p => p.ProducedQuantity), finished.Count),
            CycleTimeDays        = KpiMetric.Average(cycle)
        };
    }

    // ── Receivables ───────────────────────────────────────────────────────────

    private async Task<ReceivablesKpis> ReceivablesAsync(DateTime from, DateTime today, int windowDays, CancellationToken ct)
    {
        var invoices = _finance.SalesInvoices.Where(i => !i.IsDelete);

        var open = await invoices.Where(i => InvoiceOpen.Contains(i.Status) && i.BalanceDue > 0)
            .Select(i => new { i.BalanceDue, i.DueDate, i.CurrencyCode, i.BaseCurrencyCode, i.ExchangeRate })
            .ToListAsync(ct);

        // Money is only comparable in one currency: each invoice's own issue-time rate to the organization's base.
        // An invoice with no rate on file (and not already in base) can't be converted honestly, so it is counted apart.
        decimal? ToBase(decimal amount, string currency, string? baseCurrency, decimal? rate) =>
            rate is { } r ? amount * r : (baseCurrency is null || currency == baseCurrency ? amount : null);

        var converted     = open.Select(i => new { Base = ToBase(i.BalanceDue, i.CurrencyCode, i.BaseCurrencyCode, i.ExchangeRate), i.DueDate }).ToList();
        var usable        = converted.Where(i => i.Base.HasValue).ToList();
        var owed          = usable.Sum(i => i.Base!.Value);
        var overdue       = usable.Where(i => i.DueDate.Date < today).Sum(i => i.Base!.Value);

        var billed = await invoices.Where(i => InvoiceBilled.Contains(i.Status) && i.InvoiceDate >= from)
            .Select(i => new { i.GrandTotal, i.BaseGrandTotal, i.CurrencyCode, i.BaseCurrencyCode })
            .ToListAsync(ct);
        var billedBase = billed
            .Select(i => i.BaseGrandTotal ?? (i.BaseCurrencyCode is null || i.CurrencyCode == i.BaseCurrencyCode ? i.GrandTotal : (decimal?)null))
            .Where(v => v.HasValue).Sum(v => v!.Value);

        return new ReceivablesKpis
        {
            DaysSalesOutstanding = new KpiMetric
            {
                Value = billedBase > 0 ? Math.Round((double)(owed / billedBase) * windowDays, 1) : null,
                Basis = billed.Count
            },
            OverdueRate         = KpiMetric.Ratio(overdue, owed, usable.Count),
            UnconvertedInvoices = converted.Count - usable.Count
        };
    }
}
