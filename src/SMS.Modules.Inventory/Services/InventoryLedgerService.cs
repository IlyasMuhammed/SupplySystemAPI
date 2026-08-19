using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

internal sealed class InventoryLedgerService : IInventoryLedgerService
{
    private readonly InventoryDbContext _db;
    private readonly ILogger<InventoryLedgerService> _logger;
    private readonly IMasterProductLedgerService? _masterProductLedger;

    public InventoryLedgerService(
        InventoryDbContext db, ILogger<InventoryLedgerService> logger,
        IMasterProductLedgerService? masterProductLedger = null)
    {
        _db                  = db;
        _logger              = logger;
        _masterProductLedger = masterProductLedger;
    }

    public async Task CreateEntryAsync(LedgerEntryCommand cmd, IDbContextTransaction? transaction = null)
    {
        if (cmd.QuantityIn.HasValue && cmd.QuantityOut.HasValue)
            throw new ArgumentException("Exactly one of QuantityIn or QuantityOut must be set, not both.", nameof(cmd));
        if (!cmd.QuantityIn.HasValue && !cmd.QuantityOut.HasValue)
            throw new ArgumentException("Exactly one of QuantityIn or QuantityOut must be set.", nameof(cmd));

        var prevBalance = await _db.InventoryLedgerEntries
            .Where(e => e.VariantId == cmd.VariantId && e.WarehouseId == cmd.WarehouseId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.LedgerId)
            .Select(e => (decimal?)e.BalanceAfter)
            .FirstOrDefaultAsync() ?? 0m;

        var qty          = cmd.QuantityIn ?? cmd.QuantityOut!.Value;
        var balanceAfter = cmd.QuantityIn.HasValue
            ? prevBalance + cmd.QuantityIn.Value
            : prevBalance - cmd.QuantityOut!.Value;

        _db.InventoryLedgerEntries.Add(new InventoryLedgerEntry
        {
            LedgerId         = Guid.NewGuid(),
            VariantId        = cmd.VariantId,
            WarehouseId      = cmd.WarehouseId,
            TransactionDate  = DateTime.UtcNow,
            TransactionType  = cmd.TransactionType,
            ReferenceType    = cmd.ReferenceType,
            ReferenceId      = cmd.ReferenceId,
            ReferenceNumber  = cmd.ReferenceNumber,
            QuantityIn       = cmd.QuantityIn,
            QuantityOut      = cmd.QuantityOut,
            BalanceAfter     = balanceAfter,
            UnitCost         = cmd.UnitCost,
            TransactionValue = qty * cmd.UnitCost,
            Notes            = cmd.Notes,
            CreatedBy        = cmd.CreatedBy,
            CreatedAt        = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Ledger entry queued: variant_id={VariantId} warehouse_id={WarehouseId} transaction_type={TransactionType} balance_after={BalanceAfter}",
            cmd.VariantId, cmd.WarehouseId, cmd.TransactionType, balanceAfter);

        // FSD Addendum 24 (ML-003) — mirror into the master product ledger within the SAME ambient
        // transaction. Only when the caller supplied source/destination context (callers not yet
        // updated simply skip this, same as _masterProductLedger being unregistered in tests).
        if (_masterProductLedger is not null
            && !string.IsNullOrWhiteSpace(cmd.SourceType) && !string.IsNullOrWhiteSpace(cmd.DestinationType))
        {
            var variant = await _db.ProductVariants.AsNoTracking()
                .Include(v => v.Product)
                .FirstOrDefaultAsync(v => v.Id == cmd.VariantId);
            var warehouse = await _db.Warehouses.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == cmd.WarehouseId);

            string? categoryName = null;
            if (variant?.Product.CategoryId is int categoryId)
                categoryName = await _db.ProductCategories.AsNoTracking()
                    .Where(c => c.Id == categoryId)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync();

            // Picks up the ambient transaction whether it was passed in explicitly or set directly
            // on this shared _db instance via UseTransaction (e.g. MivService/MaterialReturnService,
            // which enlist _db into a cross-DbContext transaction without threading it as a param).
            var ambientTransaction = transaction ?? _db.Database.CurrentTransaction;

            // PV-005 — ProductName is denormalised as "{Product} ({Variant})" for single-variant
            // (default) products the variant name is omitted, since it's typically just "Default"
            // and adds no information.
            var productName = variant is null ? string.Empty
                : variant.IsDefault ? variant.Product.Name
                : $"{variant.Product.Name} ({variant.VariantName})";

            await _masterProductLedger.PostMovementAsync(new ProductMovementContext
            {
                VariantId       = cmd.VariantId,
                ProductCode     = variant?.Sku ?? string.Empty,
                ProductName     = productName,
                // PV-008 — distinct fields alongside the folded ProductCode/ProductName above.
                VariantName     = variant?.VariantName,
                Sku             = variant?.Sku,
                CategoryId      = variant?.Product.CategoryId,
                CategoryName    = categoryName,
                WarehouseId     = cmd.WarehouseId,
                WarehouseName   = warehouse?.Name ?? string.Empty,
                TransactionType = cmd.TransactionType,
                ReferenceType   = cmd.ReferenceType,
                ReferenceId     = cmd.ReferenceId,
                ReferenceNumber = cmd.ReferenceNumber,
                QuantityIn      = cmd.QuantityIn,
                QuantityOut     = cmd.QuantityOut,
                UnitCost        = cmd.UnitCost,
                SourceType      = cmd.SourceType!,
                SourceName      = cmd.SourceName,
                DestinationType = cmd.DestinationType!,
                DestinationName = cmd.DestinationName,
                Notes           = cmd.Notes,
                CreatedBy       = cmd.CreatedBy
            }, ambientTransaction?.GetDbTransaction());
        }
        // NOTE: SaveChangesAsync is intentionally NOT called here for _db — the caller owns the
        // transaction. MasterProductLedgerService.PostMovementAsync DOES call SaveChangesAsync on
        // its own FinanceDbContext, but since that context shares the caller's ambient transaction,
        // the write isn't durable until the caller's own SaveChangesAsync + CommitAsync complete.
    }

    public async Task<decimal> GetCurrentBalanceAsync(int variantId, int warehouseId)
    {
        return await _db.InventoryLedgerEntries
            .Where(e => e.VariantId == variantId && e.WarehouseId == warehouseId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.LedgerId)
            .Select(e => (decimal?)e.BalanceAfter)
            .FirstOrDefaultAsync() ?? 0m;
    }

    public async Task<LedgerPagedResult> GetLedgerAsync(LedgerFilterDto filter)
    {
        var query =
            from entry     in _db.InventoryLedgerEntries
            join variant   in _db.ProductVariants on entry.VariantId equals variant.Id
            join warehouse in _db.Warehouses on entry.WarehouseId equals warehouse.Id
            select new { entry, variant, warehouse };

        if (filter.VariantId.HasValue)
            query = query.Where(x => x.entry.VariantId == filter.VariantId.Value);

        if (filter.WarehouseId.HasValue)
            query = query.Where(x => x.entry.WarehouseId == filter.WarehouseId.Value);

        if (filter.From.HasValue)
            query = query.Where(x => x.entry.CreatedAt >= filter.From.Value);

        if (filter.To.HasValue)
            query = query.Where(x => x.entry.CreatedAt <= filter.To.Value);

        if (!string.IsNullOrWhiteSpace(filter.TransactionType))
            query = query.Where(x => x.entry.TransactionType == filter.TransactionType);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var rows = await query
            .OrderByDescending(x => x.entry.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new InventoryLedgerEntryDto
            {
                LedgerId         = x.entry.LedgerId,
                VariantId        = x.entry.VariantId,
                ProductName      = x.variant.Product.Name,
                VariantSku       = x.variant.Sku,
                WarehouseId      = x.entry.WarehouseId,
                WarehouseName    = x.warehouse.Name,
                TransactionDate  = x.entry.TransactionDate,
                TransactionType  = x.entry.TransactionType,
                ReferenceType    = x.entry.ReferenceType,
                ReferenceId      = x.entry.ReferenceId,
                ReferenceNumber  = x.entry.ReferenceNumber,
                QuantityIn       = x.entry.QuantityIn,
                QuantityOut      = x.entry.QuantityOut,
                BalanceAfter     = x.entry.BalanceAfter,
                UnitCost         = x.entry.UnitCost,
                TransactionValue = x.entry.TransactionValue,
                Notes            = x.entry.Notes,
                CreatedBy        = x.entry.CreatedBy,
                CreatedAt        = x.entry.CreatedAt
            })
            .ToListAsync();

        var stats = await query
            .GroupBy(x => 1)
            .Select(g => new
            {
                TotalValueIn  = g.Sum(x => x.entry.QuantityIn  != null ? x.entry.TransactionValue : 0m),
                TotalValueOut = g.Sum(x => x.entry.QuantityOut != null ? x.entry.TransactionValue : 0m)
            })
            .FirstOrDefaultAsync();

        decimal currentBalance = 0m;
        if (filter.VariantId.HasValue && filter.WarehouseId.HasValue)
            currentBalance = await GetCurrentBalanceAsync(filter.VariantId.Value, filter.WarehouseId.Value);

        return new LedgerPagedResult
        {
            Entries        = rows,
            TotalCount     = total,
            Page           = page,
            PageSize       = pageSize,
            TotalPages     = (int)Math.Ceiling(total / (double)pageSize),
            CurrentBalance = currentBalance,
            TotalValueIn   = stats?.TotalValueIn  ?? 0m,
            TotalValueOut  = stats?.TotalValueOut ?? 0m
        };
    }
}
