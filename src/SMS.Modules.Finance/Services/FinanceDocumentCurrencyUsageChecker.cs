using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A35 D-8 — Finance's locked documents, for "is this currency / this domain's base in use?" (BR-C1-04, BR-C3-03):
/// <list type="bullet">
/// <item><b>Sale</b>: sales invoices past DRAFT (issued, paid, overdue, cancelled — their rate was locked at issue).</item>
/// <item><b>Purchase</b>: supplier invoices that were approved (Approved, or Reversed after it — locked at approval).</item>
/// </list>
/// A currency counts as used when it is a locked document's currency or its base. Matching is on the ISO code the
/// documents always carry (so rows from before the A35 columns count too). Explicit organization, query filters off:
/// another organization's documents never count, whoever asks (Tenancy may call this for a super admin or from a job).
/// The rate-currency question is CUR's checker's; this one answers "unused" for it (interface default).
/// </summary>
internal sealed class FinanceDocumentCurrencyUsageChecker : ICurrencyUsageChecker
{
    private static readonly string[] LockedSupplierStatuses = [InvoiceMatchStatus.Approved, InvoiceMatchStatus.Reversed];

    private readonly FinanceDbContext     _db;
    private readonly ICurrencyCodeLookup? _codes;

    public FinanceDocumentCurrencyUsageChecker(FinanceDbContext db, ICurrencyCodeLookup? codes = null)
    {
        _db    = db;
        _codes = codes;
    }

    public async Task<string?> DescribeCurrencyUsageAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default)
    {
        var code = _codes is null ? null : (await _codes.GetCodeAsync(currencyId, ct))?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) return null;

        var sales = await LockedSalesInvoices(organizationId)
            .CountAsync(i => i.CurrencyCode.ToUpper() == code || (i.BaseCurrencyCode != null && i.BaseCurrencyCode.ToUpper() == code), ct);
        var purchases = await LockedSupplierInvoices(organizationId)
            .CountAsync(i => i.Currency.ToUpper() == code || (i.BaseCurrencyCode != null && i.BaseCurrencyCode.ToUpper() == code), ct);

        return Describe(sales, purchases);
    }

    public async Task<string?> DescribeDomainBaseUsageAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        domain switch
        {
            TransactionDomain.Sale     => Describe(await LockedSalesInvoices(organizationId).CountAsync(ct), 0),
            TransactionDomain.Purchase => Describe(0, await LockedSupplierInvoices(organizationId).CountAsync(ct)),
            _                          => null
        };

    private IQueryable<SalesInvoice> LockedSalesInvoices(Guid organizationId) =>
        _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
           .Where(i => i.OrganizationId == organizationId && !i.IsDelete && i.Status != SalesInvoiceStatuses.Draft);

    private IQueryable<Invoice> LockedSupplierInvoices(Guid organizationId) =>
        _db.Invoices.IgnoreQueryFilters().AsNoTracking()
           .Where(i => i.OrganizationId == organizationId && !i.IsDelete && LockedSupplierStatuses.Contains(i.MatchStatus));

    private static string? Describe(int sales, int purchases)
    {
        var parts = new List<string>();
        if (sales > 0)     parts.Add($"{sales} issued sales invoice{(sales == 1 ? "" : "s")}");
        if (purchases > 0) parts.Add($"{purchases} approved supplier invoice{(purchases == 1 ? "" : "s")}");
        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }
}
