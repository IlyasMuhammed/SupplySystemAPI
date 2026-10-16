using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Settings;

// A44 seams between the gateway side (QBO agent: QuickBooksGateway, object builder, hold/release mechanics) and
// the settings side (CTRL agent: tables, migration, admin API). See docs/financial-posting/ADDENDUM-44-ANALYSIS.md
// D-14 (sync control) and D-15 (provider defaults). CTRL implements the readers; QBO implements the releaser.

/// <summary>Sync-control document types (D-14). One row per connection and type; no row = enabled (BR-29).</summary>
internal static class SyncControlDocumentTypes
{
    public const string Customer      = "Customer";
    public const string Vendor        = "Vendor";
    public const string Item          = "Item";
    public const string SalesInvoice  = "SalesInvoice";
    public const string CreditNote    = "CreditNote";
    public const string Payment       = "Payment";
    public const string PosSale       = "POSSale";
    public const string PosReturn     = "POSReturn";
    public const string PurchaseBill  = "PurchaseBill";
    public const string BillPayment   = "BillPayment";
    public const string VendorCredit  = "VendorCredit";

    public static readonly IReadOnlyList<string> All =
        [Customer, Vendor, Item, SalesInvoice, CreditNote, Payment, PosSale, PosReturn, PurchaseBill, BillPayment, VendorCredit];

    /// <summary>
    /// The sync-control type governing a record. POS documents (SourceType "POS": the POS invoice, its tender
    /// payments, sales receipts) follow POSSale; POS credit notes / refund receipts follow POSReturn.
    /// </summary>
    public static string For(SyncKind kind, string? sourceType)
    {
        var pos = SalesInvoicePayloadSources.IsPos(sourceType);
        return kind switch
        {
            SyncKind.Customer      => Customer,
            SyncKind.Vendor        => Vendor,
            SyncKind.Item          => Item,
            SyncKind.SalesInvoice  => pos ? PosSale : SalesInvoice,
            SyncKind.SalesReceipt  => PosSale,
            SyncKind.Payment       => pos ? PosSale : Payment,
            SyncKind.CreditMemo    => pos ? PosReturn : CreditNote,
            SyncKind.RefundReceipt => pos ? PosReturn : CreditNote,
            SyncKind.Bill          => PurchaseBill,
            SyncKind.BillPayment   => BillPayment,
            SyncKind.VendorCredit  => VendorCredit,
            _                      => kind.ToString()
        };
    }
}

internal enum SyncControlAction { Process, Hold, Skip }

internal sealed record SyncControlDecision(bool IsEnabled, bool HoldWhenDisabled)
{
    public SyncControlAction Action => IsEnabled ? SyncControlAction.Process
                                     : HoldWhenDisabled ? SyncControlAction.Hold
                                     : SyncControlAction.Skip;

    public static readonly SyncControlDecision Enabled = new(true, false);
}

/// <summary>Implemented by CTRL over integration.SyncControlConfigs.</summary>
internal interface ISyncControlReader
{
    Task<SyncControlDecision> CheckAsync(int connectionId, string documentType, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, SyncControlDecision>> GetAllAsync(int connectionId, CancellationToken ct = default);
}

/// <summary>
/// Implemented by QBO (generalised from PosSales): queues every record held for this type, through the
/// sources (IQuickBooksSource re-push) or the stored payload. Returns how many were released. CTRL calls it when
/// a held type is enabled, and the hourly reconciliation calls it for every enabled type.
/// </summary>
internal interface IHeldRecordReleaser
{
    Task<int> ReleaseAsync(int connectionId, string documentType, CancellationToken ct = default);
    Task<int> CountHeldAsync(int connectionId, string documentType, CancellationToken ct = default);
    /// <summary>Holds queued-but-unsent upserts of a type that was just disabled with hold (voids still go).</summary>
    Task<int> HoldQueuedAsync(int connectionId, string documentType, CancellationToken ct = default);
}

/// <summary>Provider default keys (D-15). Method-specific keys are "{key}.{METHOD}", e.g. "DepositToAccountRef.CASH".</summary>
internal static class ProviderDefaultKeys
{
    public const string ClassRef             = "ClassRef";
    public const string DepartmentRef        = "DepartmentRef";
    public const string SalesTermRef         = "SalesTermRef";
    public const string PaymentMethodRef     = "PaymentMethodRef";
    public const string DepositToAccountRef  = "DepositToAccountRef";
    public const string BankAccountRef       = "BankAccountRef";
    public const string RefundFromAccountRef = "RefundFromAccountRef";
    /// <summary>QBO customer id used for walk-in POS documents without a customer.</summary>
    public const string PosDefaultCustomerRef = "PosDefaultCustomerRef";

    public static string ForMethod(string key, string method) => $"{key}.{method.Trim().ToUpperInvariant()}";
}

/// <summary>Implemented by CTRL over integration.ProviderDefaults + DocumentOverrides (overrides win, BR-12).</summary>
internal interface IProviderDefaultsReader
{
    /// <summary>Effective settings for a document type (see <see cref="SyncControlDocumentTypes"/>).</summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(int connectionId, string documentType, CancellationToken ct = default);
}
