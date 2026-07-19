using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

internal sealed class SupplierLedgerService : ISupplierLedgerService
{
    private const int MaxAttempts = 5;

    private readonly FinanceDbContext _db;

    public SupplierLedgerService(FinanceDbContext db) => _db = db;

    public async Task<SupplierLedgerEntryModel> PostEntryAsync(
        Guid supplierId, string transactionType, string referenceType, Guid referenceId, string referenceNo,
        decimal debitAmount, decimal creditAmount, string? narration, int createdBy)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var last = await _db.SupplierLedgerEntries
                .Where(e => e.SupplierId == supplierId)
                .OrderByDescending(e => e.SequenceNo)
                .FirstOrDefaultAsync();

            var entry = new SupplierLedgerEntry
            {
                UUID            = Guid.NewGuid(),
                SupplierId      = supplierId,
                SequenceNo      = (last?.SequenceNo ?? 0) + 1,
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

            _db.SupplierLedgerEntries.Add(entry);

            try
            {
                await _db.SaveChangesAsync();
                return ToModel(entry);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Another writer committed the same SequenceNo first (unique index conflict on
                // (SupplierId, SequenceNo)). Detach only this failed entry so any other pending
                // changes on this DbContext survive the retry.
                _db.Entry(entry).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"Failed to post ledger entry for supplier '{supplierId}' after {MaxAttempts} attempts.");
    }

    public async Task<PaginatedResponse<SupplierLedgerEntryModel>> GetLedgerAsync(Guid supplierId, SupplierLedgerFilter filter)
    {
        var query = _db.SupplierLedgerEntries.Where(e => e.SupplierId == supplierId).AsQueryable();

        if (filter.DateFrom.HasValue) query = query.Where(e => e.EntryDate >= filter.DateFrom.Value);
        if (filter.DateTo.HasValue)   query = query.Where(e => e.EntryDate <= filter.DateTo.Value);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(e => e.EntryDate)
            .ThenByDescending(e => e.SequenceNo)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new SupplierLedgerEntryModel
            {
                Uuid            = e.UUID,
                SupplierId      = e.SupplierId,
                SequenceNo      = e.SequenceNo,
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
            })
            .ToListAsync();

        return new PaginatedResponse<SupplierLedgerEntryModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<SupplierBalanceSummary> GetBalanceAsync(Guid supplierId)
    {
        var query = _db.SupplierLedgerEntries.Where(e => e.SupplierId == supplierId);

        var totalDebits  = await query.SumAsync(e => (decimal?)e.DebitAmount) ?? 0m;
        var totalCredits = await query.SumAsync(e => (decimal?)e.CreditAmount) ?? 0m;
        var netBalance   = totalDebits - totalCredits;

        return new SupplierBalanceSummary
        {
            SupplierId             = supplierId,
            TotalDebits            = totalDebits,
            TotalCredits           = totalCredits,
            NetBalance             = netBalance,
            AvailableAdvanceCredit = netBalance < 0 ? -netBalance : 0m
        };
    }

    private static SupplierLedgerEntryModel ToModel(SupplierLedgerEntry e) => new()
    {
        Uuid            = e.UUID,
        SupplierId      = e.SupplierId,
        SequenceNo      = e.SequenceNo,
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
