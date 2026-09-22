using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// Answers "where is this warehouse?" for modules that hold only its UUID. A read, and nothing else:
/// changing a warehouse stays with the warehouse screens.
/// </summary>
internal sealed class WarehouseDirectory : IWarehouseDirectory
{
    private readonly InventoryDbContext _db;

    public WarehouseDirectory(InventoryDbContext db) => _db = db;

    public Task<WarehouseContact?> FindAsync(Guid warehouseUuid, CancellationToken ct = default) =>
        _db.Warehouses
            .AsNoTracking()
            .Where(w => w.Uuid == warehouseUuid)
            .Select(w => new WarehouseContact(
                w.Uuid, w.Code, w.Name, w.Address, w.City, w.Country, w.ContactName, w.ContactPhone, w.IsActive))
            .FirstOrDefaultAsync(ct);
}
