using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// SAP alignment (S-7) — lets Demand refuse to cancel a sale order whose goods have been billed, without a
/// project reference to Finance. Reads through the tenant-filtered context, so only the caller's own
/// organization's invoices are ever seen.
/// </summary>
internal sealed class SaleOrderInvoiceLookup : ISaleOrderInvoiceLookup
{
    private readonly FinanceDbContext _db;

    public SaleOrderInvoiceLookup(FinanceDbContext db) => _db = db;

    public async Task<IReadOnlyList<SaleOrderInvoiceRef>> GetLiveInvoicesAsync(Guid saleOrderUuid, CancellationToken ct = default) =>
        await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.SaleOrderUuid == saleOrderUuid && !i.IsDelete && i.Status != SalesInvoiceStatuses.Cancelled)
            .OrderBy(i => i.Id)
            .Select(i => new SaleOrderInvoiceRef(i.UUID, i.InvoiceNumber, i.Status))
            .ToListAsync(ct);
}
