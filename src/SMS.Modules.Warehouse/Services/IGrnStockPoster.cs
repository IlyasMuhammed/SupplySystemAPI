using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Warehouse.Domain;

namespace SMS.Modules.Warehouse.Services;

internal interface IGrnStockPoster
{
    Task PostToInventoryAsync(Grn grn, int approvedBy);
}

// Production: posts accepted quantities and ledger entries into inventory atomically.
internal sealed class EfGrnInventoryPoster : IGrnStockPoster
{
    private readonly InventoryDbContext _inv;
    private readonly IInventoryLedgerService _ledger;

    public EfGrnInventoryPoster(InventoryDbContext inv, IInventoryLedgerService ledger)
    {
        _inv    = inv;
        _ledger = ledger;
    }

    public async Task PostToInventoryAsync(Grn grn, int approvedBy)
    {
        if (!grn.WarehouseUuid.HasValue) return;

        var warehouse = await _inv.Warehouses
            .FirstOrDefaultAsync(w => w.Uuid == grn.WarehouseUuid.Value);
        if (warehouse is null) return;

        // Validate: any line with effective qty > 0 must be linked to a catalogue product
        var unlinked = grn.Lines.Where(l =>
        {
            if (l.ProductUuid.HasValue) return false;
            var qty = l.RequiresInspection
                ? l.QtyAccepted
                : Math.Max(0m, l.QtyReceived - l.QtyRejected);
            return qty > 0;
        }).ToList();

        if (unlinked.Count > 0)
        {
            var items = string.Join(", ", unlinked.Select(l => $"'{l.ItemDescription}'"));
            throw new SMS.Shared.Exceptions.UnprocessableEntityException(
                $"Cannot post stock: the following lines are not linked to a catalogue product — {items}. " +
                "Edit the GRN to select the matching product for each line before approving.");
        }

        var strategy = _inv.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _inv.Database.BeginTransactionAsync();

            foreach (var line in grn.Lines.Where(l => l.ProductUuid.HasValue))
            {
                var effectiveQty = line.RequiresInspection
                    ? line.QtyAccepted
                    : Math.Max(0m, line.QtyReceived - line.QtyRejected);

                if (effectiveQty <= 0) continue;

                var product = await _inv.Products
                    .FirstOrDefaultAsync(p => p.Uuid == line.ProductUuid!.Value);
                if (product is null) continue;

                // Look up the correct InventoryItem row for this GRN line.
                // For batch-tracked products, each batch gets its own row (key: ProductId+WarehouseId+BatchNumber).
                // For serial-tracked products, each serial gets its own row (key: ProductId+WarehouseId+SerialNumber).
                // For non-tracked products, a single row per product/warehouse is used.
                InventoryItem? item;
                if (product.IsBatchTracked && !string.IsNullOrWhiteSpace(line.BatchNumber))
                {
                    var batchNo = line.BatchNumber;
                    item = _inv.InventoryItems.Local
                               .FirstOrDefault(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == batchNo)
                           ?? await _inv.InventoryItems
                               .FirstOrDefaultAsync(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == batchNo);
                }
                else if (product.IsSerialTracked && !string.IsNullOrWhiteSpace(line.SerialNumber))
                {
                    var serial = line.SerialNumber;
                    item = _inv.InventoryItems.Local
                               .FirstOrDefault(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id && i.SerialNumber == serial)
                           ?? await _inv.InventoryItems
                               .FirstOrDefaultAsync(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id && i.SerialNumber == serial);
                }
                else
                {
                    item = _inv.InventoryItems.Local
                               .FirstOrDefault(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == null && i.SerialNumber == null)
                           ?? await _inv.InventoryItems
                               .FirstOrDefaultAsync(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == null && i.SerialNumber == null);
                }

                if (item is null)
                {
                    item = new InventoryItem
                    {
                        Uuid         = Guid.NewGuid(),
                        ProductId    = product.Id,
                        WarehouseId  = warehouse.Id,
                        BatchNumber  = product.IsBatchTracked  ? line.BatchNumber  : null,
                        SerialNumber = product.IsSerialTracked ? line.SerialNumber : null,
                        ExpiryDate   = line.ExpiryDate,
                        QtyOnHand    = effectiveQty,
                        UnitCost     = line.UnitCost,
                        LastUpdated  = DateTime.UtcNow
                    };
                    _inv.InventoryItems.Add(item);
                }
                else
                {
                    item.QtyOnHand  += effectiveQty;
                    if (line.ExpiryDate.HasValue) item.ExpiryDate = line.ExpiryDate;
                    if (line.UnitCost.HasValue)   item.UnitCost   = line.UnitCost;
                    item.LastUpdated = DateTime.UtcNow;
                }

                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    ProductId       = product.Id,
                    WarehouseId     = warehouse.Id,
                    TransactionType = "GRN_RECEIPT",
                    ReferenceType   = "GRN",
                    ReferenceId     = grn.UUID,
                    ReferenceNumber = grn.GrnNumber,
                    QuantityIn      = effectiveQty,
                    UnitCost        = line.UnitCost ?? 0m,
                    Notes           = $"GRN receipt: {line.ItemDescription}",
                    CreatedBy       = approvedBy,
                    // FSD Addendum 24 (ML-003): goods flow from the supplier into this warehouse.
                    SourceType      = "SUPPLIER",
                    SourceName      = grn.SupplierName,
                    DestinationType = "WAREHOUSE",
                    DestinationName = warehouse.Name
                }, tx);
            }

            await _inv.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }
}

// Test double: no-op — used when stock posting is not under test.
internal sealed class NullGrnStockPoster : IGrnStockPoster
{
    public Task PostToInventoryAsync(Grn grn, int approvedBy) => Task.CompletedTask;
}

// Test double: always throws — used to verify GRN stays PENDING_APPROVAL when stock post fails.
internal sealed class ThrowingGrnStockPoster : IGrnStockPoster
{
    public Task PostToInventoryAsync(Grn grn, int approvedBy) =>
        throw new InvalidOperationException("Simulated inventory post failure.");
}
