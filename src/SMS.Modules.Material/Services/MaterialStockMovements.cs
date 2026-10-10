using System.Data;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Material.Services;

/// <summary>
/// The physical stock side of a material issue, shared by production issues (A30 §16) and service order issues
/// (A36 D-7): take stock out FEFO with one ledger line per inventory row touched, put stock back into the
/// un-batched row, and commit Material + Inventory as one unit. Extracted unchanged from
/// <see cref="ProductionMaterialIssueService"/>; the caller names the transaction type and reference.
/// </summary>
internal static class MaterialStockMovements
{
    /// <summary>What a ledger line is about: the movement's type, the document and who posted it.</summary>
    internal sealed record Reference(string TransactionType, string ReferenceType, Guid ReferenceId, string ReferenceNumber, int CreatedBy);

    /// <summary>Takes <paramref name="quantity"/> out of on-hand stock, FEFO; returns the last unit cost seen.</summary>
    public static async Task<decimal> DeductOnHandAsync(
        InventoryDbContext inv, IInventoryLedgerService ledger, Guid variantUuid, Guid warehouseUuid, decimal quantity,
        Reference reference, string notes, CancellationToken ct)
    {
        if (quantity <= 0) return 0m;

        var variant   = await inv.ProductVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Uuid == variantUuid, ct)
            ?? throw new BadRequestException($"Variant {variantUuid} does not exist.");
        var warehouse = await inv.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Uuid == warehouseUuid, ct)
            ?? throw new BadRequestException($"Warehouse {warehouseUuid} does not exist.");

        var rows = await inv.InventoryItems
            .Where(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id)
            .OrderBy(i => i.ExpiryDate ?? DateTime.MaxValue).ThenBy(i => i.Id)
            .ToListAsync(ct);

        var totalFree = rows.Sum(r => Math.Max(0m, r.QtyOnHand));
        if (totalFree < quantity)
            throw new BadRequestException($"Only {totalFree:0.####} of variant {variant.Sku} is on hand in {warehouse.Name}.");

        var remaining = quantity;
        var lastCost  = 0m;
        foreach (var row in rows)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, row.QtyOnHand);
            if (take <= 0) continue;

            row.QtyOnHand   -= take;
            row.LastUpdated  = DateTime.UtcNow;
            lastCost         = row.UnitCost ?? lastCost;
            remaining       -= take;

            await ledger.CreateEntryAsync(new LedgerEntryCommand
            {
                VariantId       = row.VariantId,
                WarehouseId     = row.WarehouseId,
                TransactionType = reference.TransactionType,
                ReferenceType   = reference.ReferenceType,
                ReferenceId     = reference.ReferenceId,
                ReferenceNumber = reference.ReferenceNumber,
                QuantityOut     = take,
                UnitCost        = lastCost,
                Notes           = notes,
                CreatedBy       = reference.CreatedBy
            });
        }

        return lastCost;
    }

    /// <summary>Puts <paramref name="quantity"/> back into the variant's un-batched row; returns the unit cost booked.</summary>
    public static async Task<decimal> ReturnToStockAsync(
        InventoryDbContext inv, IInventoryLedgerService ledger, Guid variantUuid, Guid warehouseUuid, decimal quantity,
        decimal fallbackUnitCost, Reference reference, string notes, CancellationToken ct)
    {
        var variant   = await inv.ProductVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Uuid == variantUuid, ct)
            ?? throw new BadRequestException($"Variant {variantUuid} does not exist.");
        var warehouse = await inv.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Uuid == warehouseUuid, ct)
            ?? throw new BadRequestException($"Warehouse {warehouseUuid} does not exist.");

        var item = await inv.InventoryItems.FirstOrDefaultAsync(i =>
            i.VariantId == variant.Id && i.WarehouseId == warehouse.Id && i.BatchNumber == null && i.SerialNumber == null, ct);
        if (item is null)
        {
            item = new InventoryItem { VariantId = variant.Id, WarehouseId = warehouse.Id, QtyOnHand = 0, QtyReserved = 0, QtyOnOrder = 0, LastUpdated = DateTime.UtcNow };
            inv.InventoryItems.Add(item);
        }

        item.QtyOnHand  += quantity;
        item.LastUpdated = DateTime.UtcNow;

        var unitCost = item.UnitCost ?? fallbackUnitCost;
        await ledger.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId       = variant.Id,
            WarehouseId     = warehouse.Id,
            TransactionType = reference.TransactionType,
            ReferenceType   = reference.ReferenceType,
            ReferenceId     = reference.ReferenceId,
            ReferenceNumber = reference.ReferenceNumber,
            QuantityIn      = quantity,
            UnitCost        = unitCost,
            Notes           = notes,
            CreatedBy       = reference.CreatedBy
        });
        return unitCost;
    }

    /// <summary>
    /// Runs <paramref name="work"/> then saves both contexts. On a relational provider they share one connection and one
    /// transaction (the arrangement MIV uses) so the commit is one thing across two DbContexts. On a non-relational
    /// provider (unit tests' InMemory database) it just runs the work and saves each context in turn.
    /// </summary>
    public static async Task RunAcrossContextsAsync(MaterialDbContext db, InventoryDbContext inv, Func<Task> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await work();
            await db.SaveChangesAsync(ct);
            await inv.SaveChangesAsync(ct);
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await db.Database.OpenConnectionAsync(ct);
            var conn = db.Database.GetDbConnection();
            await using var sqlTx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            db.Database.UseTransaction(sqlTx);
            inv.Database.SetDbConnection(conn);
            inv.Database.UseTransaction(sqlTx);
            try
            {
                await work();

                await db.SaveChangesAsync(ct);
                await inv.SaveChangesAsync(ct);
                await sqlTx.CommitAsync(ct);
            }
            finally
            {
                // A36 — let a later unit of work in the same scope start its own transaction instead of reusing this one.
                db.Database.UseTransaction(null);
                inv.Database.UseTransaction(null);
            }
        });
    }
}
