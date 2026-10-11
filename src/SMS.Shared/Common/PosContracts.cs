namespace SMS.Shared.Common;

// A38 — Point of Sale (docs/pos/ADDENDUM-38-ANALYSIS.md, API-CONTRACT.md). The POS module (SMS.Modules.Pos) talks to the
// rest of the system only through these seams. Every method takes the organization explicitly (the tenant filter is
// off in Hangfire and for super admins) and treats other organizations' rows as absent. Owner: COORD; changes by message.

/// <summary>POS feature codes (A37 registry). <see cref="ModuleCodes"/> holds the module-level ones.</summary>
public static class PosFeatureCodes
{
    public const string Module      = "MODULE_POS";
    public const string Returns     = "FEATURE_POS_RETURNS";
    public const string CreditSales = "FEATURE_POS_CREDIT_SALES";
    public const string Barcode     = "FEATURE_POS_BARCODE";
    public const string MultiStore  = "FEATURE_POS_MULTI_STORE";
    // A39
    public const string CreditNotes       = "FEATURE_POS_CREDIT_NOTES";
    public const string CrossStoreReturns = "FEATURE_POS_CROSS_STORE_RETURNS";
}

/// <summary>POS tender methods (API-CONTRACT §0). ON_ACCOUNT = credit sale (no customer payment is recorded).</summary>
public static class PosPaymentMethod
{
    public const string Cash = "CASH", Card = "CARD", Mobile = "MOBILE", BankTransfer = "BANK_TRANSFER", Cheque = "CHEQUE", OnAccount = "ON_ACCOUNT";
    /// <summary>A39 D-3 — internal tenders with no cash movement: store credit issued/redeemed, and the value carried
    /// from an exchange's return part to its new sale. Finance records them as payment/refund methods of the same code.</summary>
    public const string StoreCredit = "STORE_CREDIT", Exchange = "EXCHANGE";
}

// ─── Finance (FIN implements) ───────────────────────────────────────────────────────────────────────────────────────

/// <summary>One sold line, amounts already computed by the POS calculator (API-CONTRACT §1) — Finance stores them as given.
/// Invariant: LineTotal = NetAmount + TaxAmount; NetAmount is exclusive of tax and after every discount.</summary>
public sealed record CounterSaleLine(
    Guid SourceLineUuid, Guid VariantUuid, string Description, decimal Quantity, decimal UnitPrice,
    decimal DiscountAmount, decimal NetAmount, Guid? TaxCodeUuid, string? TaxCode, decimal TaxPercent, decimal TaxAmount, decimal LineTotal);

/// <summary>A tender net of change. ON_ACCOUNT tenders create no payment: that part stays on the customer's balance.</summary>
public sealed record CounterSaleTender(Guid SourceUuid, string Method, decimal Amount, string? Reference);

public sealed record CounterSaleRequest(
    Guid OrganizationId, Guid SourceUuid, string SourceNumber, Guid CustomerPartnerUuid, DateTime SaleDateUtc,
    IReadOnlyList<CounterSaleLine> Lines, IReadOnlyList<CounterSaleTender> Tenders, int UserId, string? Notes = null);

public sealed record CounterSaleResult(Guid SalesInvoiceUuid, string InvoiceNumber, decimal GrandTotal, IReadOnlyList<Guid> PaymentUuids);

/// <summary>Returned quantity of one original sale line (Finance resolves amounts proportionally from the original invoice line).</summary>
public sealed record CounterReturnLine(Guid OriginalSourceLineUuid, Guid SourceLineUuid, decimal Quantity, decimal NetAmount, decimal TaxAmount, decimal LineTotal);

/// <summary>Refund tenders. ON_ACCOUNT = reduce the customer's open balance (credit-sale returns) instead of paying out.</summary>
public sealed record CounterReturnRequest(
    Guid OrganizationId, Guid SourceUuid, string SourceNumber, Guid OriginalSalesInvoiceUuid, DateTime ReturnDateUtc,
    IReadOnlyList<CounterReturnLine> Lines, IReadOnlyList<CounterSaleTender> Refunds, string Reason, int UserId);

public sealed record CounterReturnResult(Guid CreditNoteUuid, string CreditNoteNumber, decimal Total, IReadOnlyList<Guid> RefundUuids);

/// <summary>
/// A38 D-8/D-10 — a POS sale as an issued sales invoice (no sale order / delivery) with one customer payment per tender,
/// allocated to it; returns as a customer credit note with refunds; void = full credit note + reversal of its payments.
/// Every call is idempotent on SourceUuid (a repeat returns the first result). Product-ledger COGS for POS may run the
/// running quantity below zero (D-9). Throws BadRequestException with a user-facing message on a rule refusal.
/// </summary>
public interface ICounterSaleService
{
    Task<CounterSaleResult> PostSaleAsync(CounterSaleRequest request, CancellationToken ct = default);
    Task<CounterReturnResult> PostReturnAsync(CounterReturnRequest request, CancellationToken ct = default);
    Task<CounterReturnResult> VoidSaleAsync(Guid organizationId, Guid salesInvoiceUuid, Guid sourceUuid, string reason, int userId, CancellationToken ct = default);
}

// ─── Inventory (INV implements) ─────────────────────────────────────────────────────────────────────────────────────

public static class PosStockTransactionType
{
    public const string Sale = "POS_SALE", Return = "POS_RETURN", Void = "POS_VOID";
}

public sealed record PosStockLine(Guid VariantUuid, decimal Quantity);

/// <summary>Direction OUT for POS_SALE, IN for POS_RETURN / POS_VOID.</summary>
public sealed record PosStockPosting(
    Guid OrganizationId, Guid WarehouseUuid, Guid SourceUuid, string SourceNumber, string TransactionType, bool IsOutbound,
    IReadOnlyList<PosStockLine> Lines, int UserId);

/// <summary>Variants whose on-hand went below zero in the store warehouse because of this posting.</summary>
public sealed record PosStockResult(IReadOnlyList<Guid> NegativeVariantUuids);

public sealed record PosStockLevel(Guid VariantUuid, decimal Available);

/// <summary>
/// A38 C9/D-9 — POS stock postings. Outbound postings never refuse for lack of stock: on-hand may go negative (flagged).
/// Idempotent per (SourceUuid, TransactionType). Writes the inventory ledger and the master product ledger
/// (customer ⇄ warehouse) in one transaction.
/// </summary>
public interface IPosStockPoster
{
    Task<PosStockResult> PostAsync(PosStockPosting posting, CancellationToken ct = default);
    Task<IReadOnlyList<PosStockLevel>> GetLevelsAsync(Guid organizationId, Guid warehouseUuid, IReadOnlyCollection<Guid>? variantUuids = null, CancellationToken ct = default);
}

/// <summary>A sellable variant as POS needs it. Price = the variant's SellingPrice (0 when unset).</summary>
public sealed record PosCatalogVariant(
    Guid VariantUuid, Guid ProductUuid, string Sku, string ProductName, string? VariantName, string? Barcode,
    int? CategoryId, string? CategoryName, string? UomCode, decimal Price, string? ImageUrl,
    bool IsActive, bool IsSaleable, bool IsAvailableForPos, DateTime ModifiedAt);

/// <summary>Inventory's read side for POS: variants (with their product) changed strictly after <c>since</c>, oldest first.</summary>
public interface IPosCatalogSource
{
    Task<IReadOnlyList<PosCatalogVariant>> GetChangedAsync(Guid organizationId, DateTime? since, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<PosCatalogVariant>> GetByVariantUuidsAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default);
    Task<IReadOnlyList<PosCatalogVariant>> GetByProductUuidsAsync(Guid organizationId, IReadOnlyCollection<Guid> productUuids, CancellationToken ct = default);
    Task<IReadOnlyList<(int Id, string Name)>> GetCategoriesAsync(Guid organizationId, CancellationToken ct = default);
}

// ─── Auth (XMOD implements) ─────────────────────────────────────────────────────────────────────────────────────────

public sealed record PosUserInfo(int UserId, string Name, string Email, string RoleName, bool IsActive, bool HasPin, IReadOnlyList<string> Permissions);
public sealed record PosPinHash(int UserId, string PinHash);
public sealed record PosTokenPair(string AccessToken, string RefreshToken);

/// <summary>
/// A38 D-4 — cashier PINs on auth.UserAccounts (hashed with the existing password hasher, never logged) and token issue
/// for a PIN sign-in. Store-level rules (uniqueness within a store, terminal lockout) live in the POS module.
/// </summary>
public interface IPosPinService
{
    Task SetPinAsync(Guid organizationId, int userId, string? pin, CancellationToken ct = default);
    Task<bool> VerifyAsync(Guid organizationId, int userId, string pin, CancellationToken ct = default);
    /// <summary>Which of <paramref name="candidateUserIds"/> has this PIN (null = none).</summary>
    Task<int?> FindByPinAsync(Guid organizationId, IReadOnlyCollection<int> candidateUserIds, string pin, CancellationToken ct = default);
    Task<IReadOnlyList<PosPinHash>> GetPinHashesAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default);
    Task<IReadOnlyList<PosUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default);
    Task<IReadOnlyList<PosUserInfo>> SearchUsersAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default);
    /// <summary>Normal JWT + refresh pair for the user (active user, active organization), as a password sign-in would give.</summary>
    Task<PosTokenPair> IssueTokensAsync(Guid organizationId, int userId, CancellationToken ct = default);
}

// ─── Suppliers / customers (XMOD implements) ────────────────────────────────────────────────────────────────────────

public sealed record PosCustomer(
    Guid Uuid, string Code, string Name, string CustomerType, string? Phone, string? Mobile, string? Email,
    decimal CreditLimit, bool IsActive, bool IsSystem, DateTime ModifiedAt);

public interface IPosCustomerSource
{
    Task<IReadOnlyList<PosCustomer>> GetChangedAsync(Guid organizationId, DateTime? since, int limit, CancellationToken ct = default);
    Task<PosCustomer?> GetAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default);
    Task<IReadOnlyList<PosCustomer>> GetManyAsync(Guid organizationId, IReadOnlyCollection<Guid> customerUuids, CancellationToken ct = default);
    /// <summary>The organization's walk-in customer (created when missing).</summary>
    Task<PosCustomer> GetWalkInAsync(Guid organizationId, CancellationToken ct = default);
}

// ─── POS (POS-B implements; others call optionally after commit) ────────────────────────────────────────────────────

/// <summary>
/// A38 C11 — tells POS terminals about changes. Implemented by the POS module (pushes on /hubs/pos); called after commit,
/// best-effort, by Inventory (price/product/stock changes), Suppliers/Finance (customer balance). Resolve optionally.
/// </summary>
public interface IPosChangeListener
{
    Task OnVariantsChangedAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default);
    Task OnPriceChangedAsync(Guid organizationId, Guid variantUuid, decimal? oldPrice, decimal? newPrice, CancellationToken ct = default);
    Task OnStockChangedAsync(Guid organizationId, Guid warehouseUuid, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default);
    Task OnCustomerBalanceChangedAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default);
}
