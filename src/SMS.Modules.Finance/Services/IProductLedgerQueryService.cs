using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// Reading the product ledger (A29 §11.4). Public and read-only, like <see cref="ICustomerLedgerQueryService"/>:
/// an endpoint or a report may ask what a variant cost and earned, but writing to the ledger is the
/// business action's job (<see cref="SMS.Shared.Common.IProductLedgerService"/>), in its own transaction.
/// </summary>
public interface IProductLedgerQueryService
{
    /// <summary>
    /// A page of the variant's ledger, newest posting first, optionally within a date range or of one kind.
    /// Ordered by <c>SequenceNo</c> — the order the running totals were computed in — so every row's totals
    /// follow from the next row's plus this row's movement. A variant with no entries has an empty page.
    /// </summary>
    Task<PaginatedResponse<ProductLedgerEntryModel>> GetLedgerAsync(Guid variantUuid, ProductLedgerFilter filter);

    /// <summary>
    /// What was bought, what was sold at what cost, and where the variant stands now: the current stock
    /// value and weighted-average cost are the last entry's running figures. A variant with no entries
    /// summarizes as zeros.
    /// </summary>
    Task<ProductLedgerSummaryModel> GetSummaryAsync(Guid variantUuid);

    /// <summary>
    /// Revenue against cost of goods sold by product, most profitable first. Both sides come from the
    /// same sales: an issued invoice's lines give the revenue, its SALE entries on the ledger give the
    /// cost, and a sale counts only if it has both. An invoice issued before the ledger was written to has
    /// no cost to set its revenue against, so it is not in the report rather than in it at a 100% margin.
    /// </summary>
    Task<PaginatedResponse<ProductProfitabilityItemModel>> GetProfitabilityAsync(ProductProfitabilityFilter filter);

    /// <summary>
    /// The same ranking with <b>every</b> product in it, named: what <see cref="GetProfitabilityAsync"/> pages
    /// through, for a report that prints the lot. The filter's paging is ignored.
    /// </summary>
    Task<IReadOnlyList<ProductProfitabilityItemModel>> GetProfitabilityRowsAsync(ProductProfitabilityFilter filter);
}
