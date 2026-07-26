using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

internal sealed class DebtWriteOffService : IDebtWriteOffService
{
    private readonly FinanceDbContext _db;
    private readonly ISupplierLedgerService _ledger;

    public DebtWriteOffService(FinanceDbContext db, ISupplierLedgerService ledger)
    {
        _db     = db;
        _ledger = ledger;
    }

    public async Task<Guid> CreateAsync(CreateWriteOffRequest req, int createdBy)
    {
        if (req.Amount <= 0)
            throw new BadRequestException("Write-off amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(req.Reason))
            throw new BadRequestException("A reason is required for a bad debt write-off.");

        var writeOff = new DebtWriteOff
        {
            UUID         = Guid.NewGuid(),
            SupplierId   = req.SupplierId,
            SupplierName = req.SupplierName,
            Amount       = req.Amount,
            Reason       = req.Reason,
            Status       = "PENDING_APPROVAL",
            CreatedBy    = createdBy,
            CreatedDate  = DateTime.UtcNow
        };

        _db.DebtWriteOffs.Add(writeOff);
        await _db.SaveChangesAsync();
        return writeOff.UUID;
    }

    public async Task<WriteOffModel?> GetByIdAsync(Guid uuid)
    {
        var w = await _db.DebtWriteOffs.AsNoTracking().FirstOrDefaultAsync(x => x.UUID == uuid);
        return w is null ? null : ToModel(w);
    }

    public async Task<bool> ApproveAsync(Guid uuid, int approvedBy)
    {
        var w = await _db.DebtWriteOffs.FirstOrDefaultAsync(x => x.UUID == uuid);
        if (w is null) return false;

        if (w.Status != "PENDING_APPROVAL")
            throw new UnprocessableEntityException(
                $"Only PENDING_APPROVAL write-offs can be approved. Current status: {w.Status}.");

        w.Status     = "APPROVED";
        w.ApprovedBy = approvedBy;
        w.ApprovedAt = DateTime.UtcNow;

        // The ledger credit is posted in the SAME SaveChangesAsync as the status change above
        // (PostEntryAsync performs that save) — the balance is untouched until this action fires,
        // which is exactly what "requires Finance Manager approval before posting" means.
        await _ledger.PostEntryAsync(
            w.SupplierId, "BAD_DEBT_WRITEOFF", "DebtWriteOff", w.UUID, w.UUID.ToString()[..8],
            debitAmount: 0m, creditAmount: w.Amount,
            narration: $"Bad debt write-off: {w.Reason}", createdBy: approvedBy, supplierName: w.SupplierName);

        return true;
    }

    public async Task<bool> RejectAsync(Guid uuid, string reason, int rejectedBy)
    {
        var w = await _db.DebtWriteOffs.FirstOrDefaultAsync(x => x.UUID == uuid);
        if (w is null) return false;

        if (w.Status != "PENDING_APPROVAL")
            throw new UnprocessableEntityException(
                $"Only PENDING_APPROVAL write-offs can be rejected. Current status: {w.Status}.");

        w.Status          = "REJECTED";
        w.RejectionReason = reason;
        await _db.SaveChangesAsync();
        return true;
    }

    private static WriteOffModel ToModel(DebtWriteOff w) => new()
    {
        Uuid            = w.UUID,
        SupplierId      = w.SupplierId,
        SupplierName    = w.SupplierName,
        Amount          = w.Amount,
        Reason          = w.Reason,
        Status          = w.Status,
        CreatedBy       = w.CreatedBy,
        CreatedDate     = w.CreatedDate,
        ApprovedBy      = w.ApprovedBy,
        ApprovedAt      = w.ApprovedAt,
        RejectionReason = w.RejectionReason
    };
}
