using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Import;

/// <summary>
/// A40 QA-R1 — <see cref="IOpeningStockDateSource"/>: the latest TransactionDate of the organization's inventory-ledger
/// OPENING_STOCK entries (posted by <see cref="OpeningStockImportTarget"/> at the opening balance date). Read-only, explicit
/// organization (tenant filter bypassed, organization applied by hand).
/// </summary>
internal sealed class OpeningStockDateSource : IOpeningStockDateSource
{
    private readonly InventoryDbContext _db;

    public OpeningStockDateSource(InventoryDbContext db) => _db = db;

    public async Task<DateOnly?> GetLatestAsync(Guid organizationId, CancellationToken ct = default)
    {
        var latest = await _db.InventoryLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.TransactionType == OpeningStockImportTarget.TransactionType)
            .MaxAsync(e => (DateTime?)e.TransactionDate, ct);
        return latest is { } d ? DateOnly.FromDateTime(d) : null;
    }
}
