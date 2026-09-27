using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;

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
    private readonly IProductLedgerService? _productLedger;
    private readonly DemandDbContext? _demand;
    private readonly IAllocationEngine? _allocation;
    private readonly ILogger<EfGrnInventoryPoster>? _logger;

    /// <param name="productLedger">
    /// A29 §11.2 — when given, each received line is also booked as a PURCHASE on the per-variant product
    /// ledger, inside the same transaction as the stock. Registered by Finance; optional, like the master
    /// product ledger in <see cref="InventoryLedgerService"/>, so a caller without Finance still posts stock.
    /// </param>
    /// <param name="demand">Where the purchase order line's price is read from; needed with <paramref name="productLedger"/>.</param>
    /// <param name="allocation">
    /// A30 §15 / A31 C10 — when given, each receipt is booked against the expected supply registered for its
    /// purchase order line. The allocation *run* itself is no longer triggered automatically here: A31 makes
    /// allocation an explicit, permission-gated action (allocation dashboard, production-order "run allocation",
    /// or the GRN detail page), so a receipt updates supply bookkeeping immediately but stock is only handed
    /// out to waiting demands when someone (or a listener) runs allocation for the variant.
    /// </param>
    public EfGrnInventoryPoster(
        InventoryDbContext inv, IInventoryLedgerService ledger,
        IProductLedgerService? productLedger = null, DemandDbContext? demand = null,
        IAllocationEngine? allocation = null, ILogger<EfGrnInventoryPoster>? logger = null)
    {
        _inv           = inv;
        _ledger        = ledger;
        _productLedger = productLedger;
        _demand        = demand;
        _allocation    = allocation;
        _logger        = logger;
    }

    public async Task PostToInventoryAsync(Grn grn, int approvedBy)
    {
        await PostStockAsync(grn, approvedBy);
        await RegisterReceiptsAsync(grn);
    }

    /// <summary>
    /// After the stock is committed, never inside that transaction: the stock is on the books whether or
    /// not this succeeds, and a failure here must not fail the approval — retrying it would post the stock
    /// twice. A31 C10: this only books the receipt against expected supply (so shortages/SR fulfillment see
    /// it right away); it deliberately does NOT run allocation for the variant. Handing the received stock
    /// to waiting demands is now a separate, explicit action — see the allocation dashboard, the production
    /// order's "run allocation" endpoint, or the GRN detail page's allocation prompt.
    /// </summary>
    private async Task RegisterReceiptsAsync(Grn grn)
    {
        if (_allocation is null || !grn.WarehouseUuid.HasValue) return;

        var received = grn.Lines.Where(l => l.VariantUuid.HasValue && l.PostedQty > 0).ToList();
        if (received.Count == 0) return;

        try
        {
            foreach (var line in received)
                await _allocation.SupplyReceivedAsync(AllocationSupplyType.PurchaseOrder, line.PoLineUuid, null, line.PostedQty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "GRN {GrnNumber}: stock was posted but registering the receipt against expected supply failed.", grn.GrnNumber);
        }
    }

    private async Task PostStockAsync(Grn grn, int approvedBy)
    {
        if (!grn.WarehouseUuid.HasValue) return;

        var warehouse = await _inv.Warehouses
            .FirstOrDefaultAsync(w => w.Uuid == grn.WarehouseUuid.Value);
        if (warehouse is null) return;

        // Validate: any line with effective qty > 0 must be linked to a catalogue product variant
        var unlinked = grn.Lines.Where(l => !l.VariantUuid.HasValue && l.PostedQty > 0).ToList();

        if (unlinked.Count > 0)
        {
            var items = string.Join(", ", unlinked.Select(l => $"'{l.ItemDescription}'"));
            throw new SMS.Shared.Exceptions.UnprocessableEntityException(
                $"Cannot post stock: the following lines are not linked to a catalogue product variant — {items}. " +
                "Edit the GRN to select the matching product/variant for each line before approving.");
        }

        // Read before the transaction opens, on Demand's own connection, so it cannot wait on a lock the
        // transaction holds.
        var purchasePrices = _productLedger is null ? null : await PurchaseOrderPricesAsync(grn);

        var strategy = _inv.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _inv.Database.BeginTransactionAsync();

            foreach (var line in grn.Lines.Where(l => l.VariantUuid.HasValue))
            {
                var effectiveQty = line.PostedQty;

                if (effectiveQty <= 0) continue;

                // PV-005 — stock is tracked per variant per warehouse now, not collapsed to the
                // parent product. Batch/serial tracking flags still live on the parent Product
                // (not per-variant), so those checks read through variant.Product.
                var variant = await _inv.ProductVariants.Include(v => v.Product)
                    .FirstOrDefaultAsync(v => v.Uuid == line.VariantUuid!.Value);
                if (variant is null) continue;
                var product = variant.Product;

                // Look up the correct InventoryItem row for this GRN line.
                // For batch-tracked products, each batch gets its own row (key: VariantId+WarehouseId+BatchNumber).
                // For serial-tracked products, each serial gets its own row (key: VariantId+WarehouseId+SerialNumber).
                // For non-tracked products, a single row per variant/warehouse is used.
                InventoryItem? item;
                if (product.IsBatchTracked && !string.IsNullOrWhiteSpace(line.BatchNumber))
                {
                    var batchNo = line.BatchNumber;
                    item = _inv.InventoryItems.Local
                               .FirstOrDefault(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == batchNo)
                           ?? await _inv.InventoryItems
                               .FirstOrDefaultAsync(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == batchNo);
                }
                else if (product.IsSerialTracked && !string.IsNullOrWhiteSpace(line.SerialNumber))
                {
                    var serial = line.SerialNumber;
                    item = _inv.InventoryItems.Local
                               .FirstOrDefault(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.SerialNumber == serial)
                           ?? await _inv.InventoryItems
                               .FirstOrDefaultAsync(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.SerialNumber == serial);
                }
                else
                {
                    item = _inv.InventoryItems.Local
                               .FirstOrDefault(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == null && i.SerialNumber == null)
                           ?? await _inv.InventoryItems
                               .FirstOrDefaultAsync(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == null && i.SerialNumber == null);
                }

                if (item is null)
                {
                    item = new InventoryItem
                    {
                        Uuid         = Guid.NewGuid(),
                        VariantId    = variant.Id,
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
                    VariantId       = variant.Id,
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

                // §11.2 — GRN approved: PURCHASE, IN, at the purchase order line's price. Joined to the
                // transaction the stock is posted in, so the ledger entry and the stock stand or fall together.
                if (_productLedger is not null)
                {
                    if (!purchasePrices!.TryGetValue(line.PoLineUuid, out var price))
                        throw new SMS.Shared.Exceptions.UnprocessableEntityException(
                            $"Cannot post stock: the purchase order line for '{line.ItemDescription}' could not be found, " +
                            "so what the goods cost is unknown.");

                    await _productLedger.AppendEntryAsync(new ProductLedgerPosting(
                        variant.Uuid, ProductLedgerEntryTypes.Purchase, ProductLedgerDirections.In, effectiveQty, price,
                        "GRN", grn.UUID, grn.GrnNumber, approvedBy,
                        ProductUuid: product.Uuid,
                        PartnerId: grn.SupplierId == Guid.Empty ? null : grn.SupplierId,
                        Narration: $"GRN receipt: {line.ItemDescription}"),
                        tx.GetDbTransaction());
                }
            }

            await _inv.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }

    /// <summary>What each line about to be posted was ordered at, by purchase order line.</summary>
    private async Task<Dictionary<Guid, decimal>> PurchaseOrderPricesAsync(Grn grn)
    {
        var demand = _demand ?? throw new InvalidOperationException(
            "Posting to the product ledger needs the purchase order lines' prices, and no DemandDbContext was given.");

        var wanted = grn.Lines
            .Where(l => l.VariantUuid.HasValue && l.PostedQty > 0m)
            .Select(l => l.PoLineUuid)
            .Distinct()
            .ToList();

        return await demand.PurchaseOrderLines.AsNoTracking()
            .Where(l => wanted.Contains(l.UUID))
            .ToDictionaryAsync(l => l.UUID, l => l.UnitPrice);
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
