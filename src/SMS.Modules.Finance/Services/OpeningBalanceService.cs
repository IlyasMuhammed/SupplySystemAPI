using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

internal sealed class OpeningBalanceService : IOpeningBalanceService
{
    private readonly FinanceDbContext _db;
    private readonly ISupplierLedgerService _ledger;

    public OpeningBalanceService(FinanceDbContext db, ISupplierLedgerService ledger)
    {
        _db     = db;
        _ledger = ledger;
    }

    public async Task<OpeningBalanceImportResult> ImportAsync(OpeningBalanceImportRequest req, int importedBy)
    {
        if (req.Suppliers.Count == 0)
            throw new BadRequestException("At least one supplier opening balance is required.");

        // The "system flag" is the data itself — an OPENING_BALANCE entry existing at all IS proof
        // the one-time go-live import already ran. Simpler and can't drift out of sync with a
        // separate flag row.
        var alreadyImported = await _db.MasterFinancialLedgers
            .AnyAsync(e => e.TransactionType == "OPENING_BALANCE");
        if (alreadyImported)
            throw new UnprocessableEntityException(
                "Opening balance import has already been run. This is a one-time go-live migration step and cannot be repeated.");

        var total = 0m;
        foreach (var line in req.Suppliers)
        {
            if (line.Amount <= 0)
                throw new BadRequestException(
                    $"Opening balance amount for supplier '{line.SupplierName}' must be greater than zero.");

            await _ledger.PostEntryAsync(
                line.SupplierId, "OPENING_BALANCE", "OpeningBalance", Guid.NewGuid(),
                $"OB-{line.SupplierId.ToString()[..8]}",
                debitAmount: line.Amount, creditAmount: 0m,
                narration: "Opening balance imported at go-live.", createdBy: importedBy,
                supplierName: line.SupplierName);

            total += line.Amount;
        }

        var masterBalance = await _db.MasterFinancialLedgers
            .OrderByDescending(e => e.SequenceNo)
            .Select(e => (decimal?)e.BalanceAfter)
            .FirstOrDefaultAsync() ?? 0m;

        return new OpeningBalanceImportResult
        {
            EntriesCreated     = req.Suppliers.Count,
            TotalImported      = total,
            MasterBalanceAfter = masterBalance
        };
    }
}
