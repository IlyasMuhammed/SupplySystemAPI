using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A35 D-8 — Demand's answer to "is this currency / this domain's base used by locked documents?" (BR-C1-03/04, BR-C3-03).
/// Locked = a sale order past DRAFT and not CANCELLED, a quotation that was sent, a purchase order APPROVED or later.
/// Explicit organization (IgnoreQueryFilters + OrganizationId): asked by Finance / Tenancy, also from super-admin requests.
/// Deleted sale orders count (they stay on record with their currency); deleted purchase orders do not.
/// </summary>
internal sealed class DemandCurrencyUsageChecker(DemandDbContext db) : ICurrencyUsageChecker
{
    internal static readonly string[] LockedPoStatuses =
        ["APPROVED", "SENT", "PARTIALLY_RECEIVED", "RECEIVED", "PARTIALLY_INVOICED", "CLOSED"];

    private IQueryable<Domain.SaleOrder> LockedOrders(Guid org) =>
        db.SaleOrders.IgnoreQueryFilters().Where(o => o.OrganizationId == org && o.Status != "DRAFT" && o.Status != "CANCELLED");

    private IQueryable<Domain.SaleQuotation> SentQuotations(Guid org) =>
        db.SaleQuotations.IgnoreQueryFilters().Where(q => q.OrganizationId == org && q.SentAt != null);

    private IQueryable<Domain.PurchaseOrder> ApprovedPos(Guid org) =>
        db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.OrganizationId == org && !p.IsDelete && LockedPoStatuses.Contains(p.Status));

    public async Task<string?> DescribeCurrencyUsageAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default)
    {
        if (currencyId == Guid.Empty) return null;
        var orders = await LockedOrders(organizationId).CountAsync(o => o.CurrencyId == currencyId || o.BaseCurrencyId == currencyId, ct);
        var quotes = await SentQuotations(organizationId).CountAsync(q => q.CurrencyId == currencyId || q.BaseCurrencyId == currencyId, ct);
        var pos    = await ApprovedPos(organizationId).CountAsync(p => p.CurrencyId == currencyId || p.BaseCurrencyId == currencyId, ct);
        return Describe(orders, quotes, pos);
    }

    public async Task<string?> DescribeDomainBaseUsageAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        domain switch
        {
            TransactionDomain.Sale => Describe(
                await LockedOrders(organizationId).CountAsync(ct), await SentQuotations(organizationId).CountAsync(ct), 0),
            TransactionDomain.Purchase => Describe(0, 0, await ApprovedPos(organizationId).CountAsync(ct)),
            _ => null
        };

    private static string? Describe(int orders, int quotations, int pos)
    {
        var parts = new List<string>();
        if (orders > 0)     parts.Add($"{orders} confirmed sale order{(orders == 1 ? "" : "s")}");
        if (quotations > 0) parts.Add($"{quotations} sent sale quotation{(quotations == 1 ? "" : "s")}");
        if (pos > 0)        parts.Add($"{pos} approved purchase order{(pos == 1 ? "" : "s")}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
