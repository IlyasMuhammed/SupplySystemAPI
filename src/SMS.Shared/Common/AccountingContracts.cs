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

    /// <summary>A47 D-15 — the journal engine's codes (docs/journal-engine/API-CONTRACT.md). Journal types are GL_SETUP_MANAGE.</summary>
    public const string GlJournalCreate = "GL_JOURNAL_CREATE", GlJournalEdit = "GL_JOURNAL_EDIT", GlJournalDelete = "GL_JOURNAL_DELETE",
        GlJournalPost = "GL_JOURNAL_POST", GlJournalReverse = "GL_JOURNAL_REVERSE",
        GlReconcileView = "GL_RECONCILE_VIEW", GlReconcileExecute = "GL_RECONCILE_EXECUTE",
        GlReportsView = "GL_REPORTS_VIEW",
        GlPeriodCloseView = "GL_PERIOD_CLOSE_VIEW", GlPeriodCloseExecute = "GL_PERIOD_CLOSE_EXECUTE", GlPeriodCloseAdmin = "GL_PERIOD_CLOSE_ADMIN";
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

/// <summary>
/// D-18 — for posting: true when the date falls in an Open period of an open fiscal year (Accounting implements). A47 D-11 —
/// an adjustment entry (opening or closing) is also accepted by an AdjustmentOnly period.
/// </summary>
public interface IFiscalPeriodGuard
{
    Task<bool> IsPostingAllowedAsync(Guid organizationId, DateOnly date, CancellationToken ct = default);

    /// <summary>A47 D-11 — <paramref name="isAdjustmentEntry"/>: an opening/closing entry (AdjustmentOnly periods accept it).
    /// The default (an implementer that knows no AdjustmentOnly) applies the regular rule, which an Open period satisfies.</summary>
    Task<bool> IsPostingAllowedAsync(Guid organizationId, DateOnly date, bool isAdjustmentEntry, CancellationToken ct = default) =>
        IsPostingAllowedAsync(organizationId, date, ct);
}
