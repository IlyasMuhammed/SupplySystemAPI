using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

// FSD Addendum 24 (ML-003/ML-004) — write side implements the SMS.Shared.Common contract so
// SMS.Modules.Inventory (and, transitively, Warehouse/Material) can post product movements without
// referencing Finance directly (internal — only the shared interface is public). Read side
// (IMasterProductLedgerQueryService) is public since it only deals in public model types and is
// consumed directly by MasterProductLedgerController, which lives in this same module.
internal sealed class MasterProductLedgerService : IMasterProductLedgerService, IMasterProductLedgerQueryService
{
    private readonly FinanceDbContext _db;

    public MasterProductLedgerService(FinanceDbContext db) => _db = db;

    public async Task PostMovementAsync(ProductMovementContext context, DbTransaction? transaction = null)
    {
        var qty = context.QuantityIn ?? context.QuantityOut ?? 0m;

        var entry = new MasterProductLedger
        {
            LedgerId        = Guid.NewGuid(),
            ProductId       = context.ProductId,
            ProductCode     = context.ProductCode,
            ProductName     = context.ProductName,
            CategoryId      = context.CategoryId,
            CategoryName    = context.CategoryName,
            WarehouseId     = context.WarehouseId,
            WarehouseName   = context.WarehouseName,
            TransactionDate = DateTime.UtcNow,
            TransactionType = context.TransactionType,
            ReferenceType   = context.ReferenceType,
            ReferenceId     = context.ReferenceId,
            ReferenceNumber = context.ReferenceNumber,
            QuantityIn      = context.QuantityIn,
            QuantityOut     = context.QuantityOut,
            UnitCost        = context.UnitCost,
            TotalValue      = qty * context.UnitCost,
            SourceType      = context.SourceType,
            SourceName      = context.SourceName,
            DestinationType = context.DestinationType,
            DestinationName = context.DestinationName,
            Notes           = context.Notes,
            CreatedBy       = context.CreatedBy,
            CreatedDate     = DateTime.UtcNow
        };

        _db.MasterProductLedgers.Add(entry);

        if (transaction is not null)
        {
            // Enlist THIS DbContext (a different type/schema than the caller's) into the caller's
            // already-open ambient transaction, so this insert commits — or rolls back — atomically
            // with the InventoryLedgerEntry and whatever business write triggered it. Same technique
            // already used across DbContexts in MaterialReturnService/MivService.PostAsync.
            // contextOwnsConnection: false — this context must never close a connection it doesn't own.
            _db.Database.SetDbConnection(transaction.Connection!, contextOwnsConnection: false);
            await _db.Database.UseTransactionAsync(transaction);
        }

        await _db.SaveChangesAsync();
    }

    // ── ML-004 read side ─────────────────────────────────────────────────────

    private IQueryable<MasterProductLedger> ApplyFilter(MasterProductLedgerFilter filter)
    {
        var q = _db.MasterProductLedgers.AsQueryable();

        if (filter.DateFrom.HasValue)    q = q.Where(e => e.TransactionDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)      q = q.Where(e => e.TransactionDate <= filter.DateTo.Value);
        if (filter.ProductId.HasValue)   q = q.Where(e => e.ProductId == filter.ProductId.Value);
        if (filter.CategoryId.HasValue)  q = q.Where(e => e.CategoryId == filter.CategoryId.Value);
        if (filter.WarehouseId.HasValue) q = q.Where(e => e.WarehouseId == filter.WarehouseId.Value);
        if (!string.IsNullOrWhiteSpace(filter.TransactionType)) q = q.Where(e => e.TransactionType == filter.TransactionType);
        if (!string.IsNullOrWhiteSpace(filter.SourceType))      q = q.Where(e => e.SourceType == filter.SourceType);
        if (!string.IsNullOrWhiteSpace(filter.DestinationType)) q = q.Where(e => e.DestinationType == filter.DestinationType);

        return q;
    }

    public async Task<PaginatedResponse<MasterProductLedgerEntryModel>> GetProductLedgerAsync(MasterProductLedgerFilter filter)
    {
        var query = ApplyFilter(filter);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var entities = await query
            .OrderByDescending(e => e.TransactionDate)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PaginatedResponse<MasterProductLedgerEntryModel>
        {
            Data         = entities.Select(ToModel).ToList(),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<MasterProductLedgerSummaryModel> GetProductLedgerSummaryAsync(MasterProductLedgerFilter filter)
    {
        var filtered = ApplyFilter(filter);

        var totalReceipts = await filtered.SumAsync(e => (decimal?)(e.QuantityIn ?? 0m)) ?? 0m;
        var totalIssues   = await filtered.SumAsync(e => (decimal?)(e.QuantityOut ?? 0m)) ?? 0m;
        var totalValue    = await filtered.SumAsync(e => (decimal?)e.TotalValue) ?? 0m;

        return new MasterProductLedgerSummaryModel
        {
            TotalReceiptsQty = totalReceipts,
            TotalIssuesQty   = totalIssues,
            NetMovement      = totalReceipts - totalIssues,
            TotalValueMoved  = totalValue
        };
    }

    public async Task<List<MasterProductLedgerEntryModel>> GetProductJourneyAsync(int productId)
    {
        var entities = await _db.MasterProductLedgers
            .Where(e => e.ProductId == productId)
            .OrderBy(e => e.TransactionDate)
            .ThenBy(e => e.Id)
            .ToListAsync();

        return entities.Select(ToModel).ToList();
    }

    private static MasterProductLedgerEntryModel ToModel(MasterProductLedger e) => new()
    {
        LedgerId        = e.LedgerId,
        ProductId       = e.ProductId,
        ProductCode     = e.ProductCode,
        ProductName     = e.ProductName,
        CategoryId      = e.CategoryId,
        CategoryName    = e.CategoryName,
        WarehouseId     = e.WarehouseId,
        WarehouseName   = e.WarehouseName,
        TransactionDate = e.TransactionDate,
        TransactionType = e.TransactionType,
        ReferenceType   = e.ReferenceType,
        ReferenceId     = e.ReferenceId,
        ReferenceNumber = e.ReferenceNumber,
        QuantityIn      = e.QuantityIn,
        QuantityOut     = e.QuantityOut,
        UnitCost        = e.UnitCost,
        TotalValue      = e.TotalValue,
        SourceType      = e.SourceType,
        SourceName      = e.SourceName,
        DestinationType = e.DestinationType,
        DestinationName = e.DestinationName,
        Notes           = e.Notes,
        CreatedBy       = e.CreatedBy,
        CreatedDate     = e.CreatedDate
    };
}
