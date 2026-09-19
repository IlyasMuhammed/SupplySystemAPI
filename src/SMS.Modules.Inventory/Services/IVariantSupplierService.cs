using SMS.Modules.Inventory.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Services;

// RC-001 (FSD Addendum 28) — Supplier Rate Card Management.
public interface IVariantSupplierService
{
    Task<Guid> CreateAsync(CreateVariantSupplierRequest req, int createdBy);
    Task<bool> UpdateAsync(Guid uuid, UpdateVariantSupplierRequest req, int modifiedBy);
    Task<VariantSupplierModel?> GetByIdAsync(Guid uuid);
    Task<PaginatedResponse<SupplierRateHistoryModel>> GetHistoryAsync(Guid uuid, RateHistoryFilter filter);

    // RC-007 — sets LastReviewedAt/By without touching the rate; writes one audit history row.
    Task<bool> MarkReviewedAsync(Guid uuid, int reviewedBy);

    // RC-002 — Supplier Rate Card grid.
    Task<PaginatedResponse<RateCardGridRowModel>> GetListAsync(RateCardListFilter filter);
    Task<bool> SetPreferredAsync(Guid uuid, int modifiedBy);

    // RC-003 — Product Comparison View.
    Task<List<RateComparisonRowModel>> GetComparisonAsync(Guid variantUuid);

    // RC-005 — Bulk Rate Adjustment.
    Task<List<BulkAdjustPreviewRowModel>> PreviewBulkAdjustAsync(BulkAdjustRequest req);
    Task<BulkAdjustConfirmResult> ConfirmBulkAdjustAsync(BulkAdjustRequest req, int performedBy);
    Task UndoBulkAdjustAsync(Guid bulkOperationId, int performedBy);
    Task<List<BulkRateOperationModel>> GetRecentBulkOperationsAsync();

    // RC-006 — Excel import/export.
    Task<List<RateCardExportRowModel>> GetExportRowsAsync(Guid supplierId);
    // currencyId: the currency for rows that create a new rate card (the file carries none). Falls
    // back to the organization's base currency; with neither, those rows are reported as errors.
    Task<List<ImportPreviewRowModel>> PreviewImportAsync(Stream file, Guid supplierId, Guid? currencyId = null);
    Task<ImportConfirmResult> ConfirmImportAsync(Stream file, Guid supplierId, int performedBy, Guid? currencyId = null);

    // RC-006 — Copy rates between suppliers.
    Task<List<CopyPreviewRowModel>> PreviewCopyAsync(CopyRatesRequest req);
    Task<CopyConfirmResult> ConfirmCopyAsync(CopyRatesRequest req, int performedBy);
}
