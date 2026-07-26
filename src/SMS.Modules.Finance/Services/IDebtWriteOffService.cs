using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// FSD Addendum 24 (ML-005) — bad debt write-offs. Split into Create (PENDING_APPROVAL, no ledger
/// effect yet) and Approve (posts the master-ledger credit) because the FSD requires Finance
/// Manager approval before the write-off actually reduces the payable balance.
/// </summary>
public interface IDebtWriteOffService
{
    Task<Guid> CreateAsync(CreateWriteOffRequest req, int createdBy);
    Task<WriteOffModel?> GetByIdAsync(Guid uuid);

    /// <summary>Posts the BAD_DEBT_WRITEOFF credit entry (via ISupplierLedgerService.PostEntryAsync,
    /// same transaction as ML-001) and marks the write-off APPROVED. Only PENDING_APPROVAL write-offs
    /// can be approved.</summary>
    Task<bool> ApproveAsync(Guid uuid, int approvedBy);

    /// <summary>Marks the write-off REJECTED — no ledger effect. Only PENDING_APPROVAL write-offs
    /// can be rejected.</summary>
    Task<bool> RejectAsync(Guid uuid, string reason, int rejectedBy);
}
