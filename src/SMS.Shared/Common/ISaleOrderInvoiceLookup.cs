namespace SMS.Shared.Common;

/// <summary>One sales invoice of a sale order, as the order's own module needs to see it.</summary>
/// <param name="InvoiceNumber">SINV-YYYYMMDD-NNNN.</param>
/// <param name="Status">The invoice's status code (DRAFT, ISSUED, PARTIALLY_PAID, PAID, OVERDUE, CREDIT_NOTE).</param>
public sealed record SaleOrderInvoiceRef(Guid InvoiceUuid, string InvoiceNumber, string Status);

/// <summary>
/// The sales invoices raised against a sale order, read by Demand (which owns the order) from Finance
/// (which owns the invoices) without a project reference — Finance already references Demand, so the
/// other direction would be a cycle. Same arrangement as <see cref="IDeliveryFulfillmentReader"/>.
/// Implemented in SMS.Modules.Finance; tenant-scoped, so another organization's invoices are never seen.
/// Register/resolve optional-safe: a host without Finance simply has no invoices to report.
/// </summary>
public interface ISaleOrderInvoiceLookup
{
    /// <summary>
    /// The order's invoices that still stand — every one that is neither deleted nor CANCELLED, drafts
    /// included — oldest first. Empty when there are none.
    /// </summary>
    Task<IReadOnlyList<SaleOrderInvoiceRef>> GetLiveInvoicesAsync(Guid saleOrderUuid, CancellationToken ct = default);
}
