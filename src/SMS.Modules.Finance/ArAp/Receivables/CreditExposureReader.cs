using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp.Receivables;

/// <summary>One customer's exposure parts, base currency (D-13). <see cref="OpenInvoices"/> is net of <see cref="UnappliedCredit"/>, floored at 0.</summary>
internal sealed record ExposureParts(Guid CustomerUuid, decimal OpenOrders, decimal GrossOpenInvoices, decimal UnappliedCredit)
{
    public decimal OpenInvoices => Math.Max(0m, GrossOpenInvoices - UnappliedCredit);
    public decimal Total => OpenOrders + OpenInvoices;
}

/// <summary>
/// A48 AR (tasks 087–089, D-1, D-5, API-CONTRACT §2) — live exposure from the documents, never GL lines and never cached:
/// <list type="bullet">
/// <item><b>Open orders</b>: Demand sale orders CONFIRMED / PARTIALLY_FULFILLED / FULFILLED (INVOICED, CLOSED, DRAFT and
/// CANCELLED are not exposure) — the part of each line not yet invoiced (Quantity − InvoicedQty) of its line total, scaled
/// to the order's grand total (header discounts) and taken to base at the order's locked rate.</item>
/// <item><b>Open invoices</b>: ISSUED / PARTIALLY_PAID / OVERDUE sales invoices' BalanceDue × their locked rate.</item>
/// <item><b>Unapplied credit</b>: RECEIVED payments' unallocated part and credit notes' remaining credit
/// (GrandTotal − CreditedAmount − refunds), each at its own rate; it reduces the invoice part, never below 0.</item>
/// </list>
/// A document with no rate counts 1:1 (A42 D-12 precedent). Every query names the organization explicitly.
/// </summary>
internal sealed class CreditExposureReader
{
    internal static readonly string[] OpenOrderStatuses =
    [
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Fulfilled)
    ];

    internal static readonly string[] OpenInvoiceStatuses =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Overdue];

    private readonly FinanceDbContext _db;
    private readonly DemandDbContext  _demand;

    public CreditExposureReader(FinanceDbContext db, DemandDbContext demand)
    {
        _db     = db;
        _demand = demand;
    }

    public async Task<ExposureParts> ForCustomerAsync(Guid org, Guid customer, CancellationToken ct = default) =>
        (await ForCustomersAsync(org, [customer], ct)).GetValueOrDefault(customer) ?? new ExposureParts(customer, 0m, 0m, 0m);

    /// <param name="customers">Null = every customer with anything open.</param>
    public async Task<Dictionary<Guid, ExposureParts>> ForCustomersAsync(
        Guid org, IReadOnlyCollection<Guid>? customers, CancellationToken ct = default)
    {
        var orders   = await OpenOrdersAsync(org, customers, ct);
        var invoices = await OpenInvoicesAsync(org, customers, ct);
        var credits  = await UnappliedCreditAsync(org, customers, ct);

        var keys = orders.Keys.Concat(invoices.Keys).Concat(credits.Keys).Concat(customers ?? []).Distinct();
        return keys.ToDictionary(k => k, k => new ExposureParts(
            k, orders.GetValueOrDefault(k), invoices.GetValueOrDefault(k), credits.GetValueOrDefault(k)));
    }

    public Task<Dictionary<Guid, decimal>> OpenOrdersAsync(Guid org, IReadOnlyCollection<Guid>? customers, CancellationToken ct) =>
        OpenOrdersCoreAsync(org, customers, null, ct);

    /// <summary>REV-14 — what one order still has open (base), for a sales invoice billing it.</summary>
    public async Task<decimal> OpenOrderValueAsync(Guid org, Guid saleOrderUuid, CancellationToken ct) =>
        (await OpenOrdersCoreAsync(org, null, saleOrderUuid, ct)).Values.Sum();

    private static readonly string CancelledLine = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);

    private async Task<Dictionary<Guid, decimal>> OpenOrdersCoreAsync(
        Guid org, IReadOnlyCollection<Guid>? customers, Guid? saleOrderUuid, CancellationToken ct)
    {
        var query = _demand.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.OrganizationId == org && !o.IsDeleted && OpenOrderStatuses.Contains(o.Status));
        if (customers is not null) query = query.Where(o => customers.Contains(o.PartnerId));
        if (saleOrderUuid is { } so) query = query.Where(o => o.UUID == so);

        var rows = await query.Select(o => new
        {
            o.PartnerId, o.GrandTotal, o.GrandTotalBase, o.ExchangeRate,
            Lines = o.Lines.Select(l => new { l.UUID, l.Quantity, l.InvoicedQty, l.LineTotal, l.Status }).ToList()
        }).ToListAsync(ct);
        if (rows.Count == 0) return [];

        // REV-14 — what Finance has really billed per order line (issued, not cancelled, not a credit note). Demand's
        // InvoicedQty is a best-effort counter written after the issue, so the larger of the two is what is invoiced.
        var lineUuids = rows.SelectMany(r => r.Lines).Select(l => (Guid?)l.UUID).ToList();
        var billed = await _db.SalesInvoiceLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && lineUuids.Contains(l.SoLineUuid) && !l.SalesInvoice.IsDelete
                     && l.SalesInvoice.Status != SalesInvoiceStatuses.Draft && l.SalesInvoice.Status != SalesInvoiceStatuses.Cancelled
                     && l.SalesInvoice.Status != SalesInvoiceStatuses.CreditNote)
            .GroupBy(l => l.SoLineUuid!.Value)
            .Select(g => new { Line = g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Line, x => x.Qty, ct);

        return rows.GroupBy(r => r.PartnerId).ToDictionary(g => g.Key, g => g.Sum(r =>
        {
            var lineSum = r.Lines.Sum(l => l.LineTotal);
            var open    = r.Lines.Where(l => l.Status != CancelledLine).Sum(l =>
            {
                if (l.Quantity <= 0m) return 0m;
                var invoiced = Math.Max(l.InvoicedQty, billed.GetValueOrDefault(l.UUID));
                return l.LineTotal * Math.Clamp((l.Quantity - invoiced) / l.Quantity, 0m, 1m);
            });
            // Header-level discounts: the open part of the lines scaled to the grand total (when the order carries one).
            var openDoc = lineSum <= 0m || r.GrandTotal <= 0m ? open : open * (r.GrandTotal / lineSum);
            var rate    = r.GrandTotalBase is { } b && r.GrandTotal != 0m ? b / r.GrandTotal : r.ExchangeRate ?? 1m;
            return Round(openDoc * rate);
        }));
    }

    public async Task<Dictionary<Guid, decimal>> OpenInvoicesAsync(Guid org, IReadOnlyCollection<Guid>? customers, CancellationToken ct)
    {
        var query = _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsDelete && OpenInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0m);
        if (customers is not null) query = query.Where(i => customers.Contains(i.PartnerId));

        var rows = await query.Select(i => new { i.PartnerId, i.BalanceDue, i.ExchangeRate }).ToListAsync(ct);
        return rows.GroupBy(r => r.PartnerId).ToDictionary(g => g.Key, g => g.Sum(r => Round(r.BalanceDue * (r.ExchangeRate ?? 1m))));
    }

    public async Task<Dictionary<Guid, decimal>> UnappliedCreditAsync(Guid org, IReadOnlyCollection<Guid>? customers, CancellationToken ct)
    {
        var payments = _db.CustomerPayments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && p.Status == CustomerPaymentStatuses.Received);
        if (customers is not null) payments = payments.Where(p => customers.Contains(p.PartnerId));
        var paid = await payments.Select(p => new
        {
            p.PartnerId, p.Amount, p.ExchangeRate,
            Applied = p.Allocations.Sum(a => (decimal?)a.AllocatedAmount) ?? 0m
        }).ToListAsync(ct);

        var notesQuery = _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org && !c.IsDelete && c.Status == SalesInvoiceStatuses.CreditNote);
        if (customers is not null) notesQuery = notesQuery.Where(c => customers.Contains(c.PartnerId));
        var refunds = _db.CustomerRefunds.IgnoreQueryFilters().Where(r => r.OrganizationId == org);
        var notes = await notesQuery.Select(c => new
        {
            c.PartnerId, c.GrandTotal, c.CreditedAmount, c.ExchangeRate,
            Refunded = refunds.Where(r => r.CreditNoteUuid == c.UUID).Sum(r => (decimal?)r.Amount) ?? 0m
        }).ToListAsync(ct);

        var result = new Dictionary<Guid, decimal>();
        foreach (var p in paid.Where(p => p.Amount - p.Applied > 0m))
            result[p.PartnerId] = result.GetValueOrDefault(p.PartnerId) + Round((p.Amount - p.Applied) * (p.ExchangeRate ?? 1m));
        foreach (var n in notes)
        {
            var left = CreditMemoMath.Remaining(n.GrandTotal, n.CreditedAmount, n.Refunded);
            if (left > 0m) result[n.PartnerId] = result.GetValueOrDefault(n.PartnerId) + Round(left * (n.ExchangeRate ?? 1m));
        }
        return result;
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The one formula for what is left of a customer credit note (API-CONTRACT §4; A38 notes carry a positive GrandTotal).</summary>
internal static class CreditMemoMath
{
    public static decimal Remaining(decimal grandTotal, decimal creditedAmount, decimal refunded) =>
        Math.Max(0m, Math.Abs(grandTotal) - creditedAmount - refunded);
}
