using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// FSD Addendum 24 (ML-005) — one-time go-live import of pre-system supplier payables into the
/// master ledger. Guarded so it can only ever run once (see OpeningBalanceService).
/// </summary>
public interface IOpeningBalanceService
{
    Task<OpeningBalanceImportResult> ImportAsync(OpeningBalanceImportRequest req, int importedBy);
}
