namespace SMS.Shared.Common;

// A42 — Chart of Accounts, fiscal periods, GL opening balances (docs/general-ledger/ADDENDUM-42-ANALYSIS.md, API-CONTRACT.md).
// The Accounting module (SMS.Modules.Accounting) owns the GL setup; these are the seams other modules use or implement.
// Organization is always explicit. Owner: COORD.

/// <summary>D-3 — the GL sub-feature of MODULE_FINANCE. D-14 — its permission codes (also in PermissionCodes).</summary>
public static class AccountingCodes
{
    public const string FeatureGeneralLedger = "FEATURE_GENERAL_LEDGER";
    public const string GlView = "GL_VIEW", GlAccountManage = "GL_ACCOUNT_MANAGE", GlSetupManage = "GL_SETUP_MANAGE";
    public const string SetupRedirectUrl = "/portal/pages/finance/gl/setup";
}

/// <summary>
/// D-11 — something that pins the organization's opening balance date (A40 Finance org setting). Finance's date service
/// refuses to clear the date, or move it, while any source returns a locked date and the change would move away from it.
/// Accounting returns the conversion date once GL opening balances are posted.
/// </summary>
public interface IOpeningDateLockSource
{
    /// <summary>The date that is locked, or null when this source does not pin it.</summary>
    Task<DateOnly?> GetLockedDateAsync(Guid organizationId, CancellationToken ct = default);
    /// <summary>Shown in the 409 message, e.g. "General ledger opening balances".</summary>
    string Description { get; }
}

/// <summary>D-12 — A40 sub-ledger opening totals (Finance implements), in the organization's base currency. Information only.</summary>
public sealed record OpeningSubledgerTotals(decimal CustomerOpeningTotal, decimal SupplierOpeningTotal, decimal OpeningStockValue, string? CurrencyCode);

public interface IOpeningSubledgerTotals
{
    Task<OpeningSubledgerTotals> GetAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>D-18 — for A43 posting: true when the date falls in an Open period of an open fiscal year (Accounting implements).</summary>
public interface IFiscalPeriodGuard
{
    Task<bool> IsPostingAllowedAsync(Guid organizationId, DateOnly date, CancellationToken ct = default);
}
