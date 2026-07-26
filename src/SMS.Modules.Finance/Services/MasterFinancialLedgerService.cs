using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

internal sealed class MasterFinancialLedgerService : IMasterFinancialLedgerService, IMasterLedgerQueryService
{
    private readonly FinanceDbContext _db;

    public MasterFinancialLedgerService(FinanceDbContext db) => _db = db;

    public async Task<MasterFinancialLedger> BuildAndTrackEntryAsync(
        Guid supplierId, string supplierName, string transactionType, string referenceType,
        Guid referenceId, string referenceNo, decimal debitAmount, decimal creditAmount,
        string? narration, int createdBy)
    {
        var last = await _db.MasterFinancialLedgers
            .OrderByDescending(e => e.SequenceNo)
            .FirstOrDefaultAsync();

        var entry = new MasterFinancialLedger
        {
            UUID            = Guid.NewGuid(),
            SequenceNo      = (last?.SequenceNo ?? 0) + 1,
            SupplierId      = supplierId,
            SupplierName    = supplierName,
            TransactionType = transactionType,
            ReferenceType   = referenceType,
            ReferenceId     = referenceId,
            ReferenceNo     = referenceNo,
            EntryDate       = DateTime.UtcNow,
            DebitAmount     = debitAmount,
            CreditAmount    = creditAmount,
            BalanceAfter    = (last?.BalanceAfter ?? 0m) + debitAmount - creditAmount,
            Narration       = narration,
            CreatedBy       = createdBy,
            CreatedDate     = DateTime.UtcNow
        };

        _db.MasterFinancialLedgers.Add(entry);
        return entry;
    }

    // ── ML-002 read side ──────────────────────────────────────────────────────

    private IQueryable<MasterFinancialLedger> ApplyFilter(MasterLedgerFilter filter)
    {
        var q = _db.MasterFinancialLedgers.AsQueryable();

        if (filter.DateFrom.HasValue)   q = q.Where(e => e.EntryDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)     q = q.Where(e => e.EntryDate <= filter.DateTo.Value);
        if (filter.SupplierId.HasValue) q = q.Where(e => e.SupplierId == filter.SupplierId.Value);
        if (filter.TransactionTypes is { Count: > 0 })
            q = q.Where(e => filter.TransactionTypes.Contains(e.TransactionType));
        if (filter.MinAmount.HasValue)
            q = q.Where(e => e.DebitAmount >= filter.MinAmount.Value || e.CreditAmount >= filter.MinAmount.Value);

        return q;
    }

    public async Task<PaginatedResponse<MasterLedgerEntryModel>> GetLedgerAsync(MasterLedgerFilter filter)
    {
        var query = ApplyFilter(filter);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var entities = await query
            .OrderByDescending(e => e.SequenceNo)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PaginatedResponse<MasterLedgerEntryModel>
        {
            Data         = entities.Select(ToModel).ToList(),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<MasterLedgerSummaryModel> GetSummaryAsync(MasterLedgerFilter filter)
    {
        // Org-level running balance is always visible regardless of filters — read fresh from the
        // whole table, never from the filtered query.
        var currentBalance = await _db.MasterFinancialLedgers
            .OrderByDescending(e => e.SequenceNo)
            .Select(e => (decimal?)e.BalanceAfter)
            .FirstOrDefaultAsync() ?? 0m;

        var filtered = ApplyFilter(filter);
        var totalDebits  = await filtered.SumAsync(e => (decimal?)e.DebitAmount) ?? 0m;
        var totalCredits = await filtered.SumAsync(e => (decimal?)e.CreditAmount) ?? 0m;

        return new MasterLedgerSummaryModel
        {
            TotalPayables = currentBalance,
            TotalDebits   = totalDebits,
            TotalCredits  = totalCredits,
            NetMovement   = totalDebits - totalCredits
        };
    }

    public async Task<MasterLedgerBalanceModel> GetCurrentBalanceAsync()
    {
        var last = await _db.MasterFinancialLedgers
            .OrderByDescending(e => e.SequenceNo)
            .FirstOrDefaultAsync();

        return new MasterLedgerBalanceModel
        {
            Balance = last?.BalanceAfter ?? 0m,
            AsOf    = last?.EntryDate ?? DateTime.UtcNow
        };
    }

    private static MasterLedgerEntryModel ToModel(MasterFinancialLedger e) => new()
    {
        Uuid            = e.UUID,
        SequenceNo      = e.SequenceNo,
        SupplierId      = e.SupplierId,
        SupplierName    = e.SupplierName,
        TransactionType = e.TransactionType,
        ReferenceType   = e.ReferenceType,
        ReferenceId     = e.ReferenceId,
        ReferenceNo     = e.ReferenceNo,
        EntryDate       = e.EntryDate,
        DebitAmount     = e.DebitAmount,
        CreditAmount    = e.CreditAmount,
        BalanceAfter    = e.BalanceAfter,
        Narration       = e.Narration,
        CreatedBy       = e.CreatedBy,
        CreatedDate     = e.CreatedDate
    };
}