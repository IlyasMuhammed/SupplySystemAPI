using SMS.Shared.Common;

namespace SMS.Modules.Pos.Domain;

// A38 (docs/pos/ADDENDUM-38-ANALYSIS.md D-1..D-16, API-CONTRACT.md §0). Codes are strings; cross-module references are
// Guid scalars without FKs (warehouse, tax code, customer, variant, product) and users are int auth user ids.

// ── Codes ─────────────────────────────────────────────────────────────────────

internal static class PosShiftStatus
{
    /// <summary>Reserved, unused — opening creates OPEN.</summary>
    public const string Draft      = "DRAFT";
    public const string Open       = "OPEN";
    public const string Closing    = "CLOSING";
    public const string Closed     = "CLOSED";
    public const string Reconciled = "RECONCILED";
    public const string Disputed   = "DISPUTED";

    public static readonly IReadOnlyList<string> All = [Open, Closing, Closed, Reconciled, Disputed];
    public static readonly IReadOnlyList<string> Active = [Open, Closing];

    /// <summary>OPEN or CLOSING: the terminal's one active shift (POS-SHF-01).</summary>
    public static bool IsActive(string status) => status is Open or Closing;
}

internal static class PosCashDirection
{
    public const string In = "IN", Out = "OUT";
    public static bool IsValid(string? v) => v is In or Out;
}

internal static class PosCashReason
{
    public const string FloatTopUp = "FLOAT_TOP_UP", SafeDrop = "SAFE_DROP", PettyCash = "PETTY_CASH", ChangeRun = "CHANGE_RUN", Other = "OTHER";
    public static readonly IReadOnlyList<string> All = [FloatTopUp, SafeDrop, PettyCash, ChangeRun, Other];
}

internal static class PosRole
{
    public const string Cashier = "CASHIER", Supervisor = "SUPERVISOR";
    public static bool IsValid(string? v) => v is Cashier or Supervisor;
}

internal static class PosOrderType
{
    /// <summary>A39 D-6 — EXCHANGE: one order with negative return lines (OriginalLineId set) and positive new lines.</summary>
    public const string Sale = "SALE", Return = "RETURN", Exchange = "EXCHANGE";

    /// <summary>Orders whose non-refund payments are takings (sales and the new part of an exchange).</summary>
    public static bool Sells(string type) => type is Sale or Exchange;
}

internal static class PosOrderStatus
{
    public const string Completed = "COMPLETED", Voided = "VOIDED", Returned = "RETURNED", PartiallyReturned = "PARTIALLY_RETURNED";
}

internal static class PosPaymentStatus
{
    public const string Paid = "PAID", CreditSale = "CREDIT_SALE", PartialCredit = "PARTIAL_CREDIT", Refunded = "REFUNDED";
}

internal static class PosDiscountType
{
    public const string None = "NONE", Percent = "PERCENT", Amount = "AMOUNT";
    public static bool IsValid(string? v) => v is None or Percent or Amount;
}

internal static class PosOrderFlag
{
    public const string Offline = "OFFLINE", OfflineCredit = "OFFLINE_CREDIT", CreditOverLimit = "CREDIT_OVER_LIMIT",
        NegativeStock = "NEGATIVE_STOCK", PriceChanged = "PRICE_CHANGED", DiscountUnauthorized = "DISCOUNT_UNAUTHORIZED";
    // A39 API-CONTRACT §0
    public const string CrossStore = "CROSS_STORE", AuthorizedOffline = "AUTHORIZED_OFFLINE", AuthorizationInvalid = "AUTHORIZATION_INVALID",
        PolicyOverriddenOffline = "POLICY_OVERRIDDEN_OFFLINE";
}

/// <summary>D-7 — per store, per store-local day. Shift SH-…-{seq:00}; orders POS-…-{seq:0000}; returns RET-…-{seq:0000};
/// A39: exchanges EXC-…-{seq:0000}, store credit notes CN-…-{seq:0000}.</summary>
internal static class PosNumberPrefix
{
    public const string Shift = "SH", Order = "POS", Return = "RET", Exchange = "EXC", CreditNote = "CN";
}

internal static class PosSessionEndReason
{
    public const string Ended = "ENDED", Switched = "SWITCHED", ShiftClosed = "SHIFT_CLOSED", ForcedLogout = "FORCED_LOGOUT";
}

// ── Entities ──────────────────────────────────────────────────────────────────

/// <summary>A38 A2 / D-3 — a shop: 1:1 with an Inventory warehouse; currency = the organization's sale base.</summary>
internal class PosStore : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                        { get; set; }
    public Guid     Uuid                      { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId            { get; set; }
    public string   Code                      { get; set; } = string.Empty;
    public string   Name                      { get; set; } = string.Empty;
    public Guid     WarehouseUuid             { get; set; }
    /// <summary>Snapshot of the warehouse name, refreshed on every store save.</summary>
    public string   WarehouseName             { get; set; } = string.Empty;
    public string?  AddressLine1              { get; set; }
    public string?  AddressLine2              { get; set; }
    public string?  City                      { get; set; }
    public string?  StateProvince             { get; set; }
    public string?  PostalCode                { get; set; }
    public string?  Country                   { get; set; }
    public string?  Phone                     { get; set; }
    public string?  Email                     { get; set; }
    /// <summary>IANA or Windows id; drives shift/order business dates and numbers.</summary>
    public string   Timezone                  { get; set; } = "UTC";
    public bool     TaxInclusivePricing       { get; set; }
    /// <summary>Finance TaxCode UUID (sales); code + rate are snapshots refreshed on save (D-11).</summary>
    public Guid?    DefaultTaxCodeUuid        { get; set; }
    public string?  DefaultTaxCode            { get; set; }
    public decimal  DefaultTaxPercent         { get; set; }
    public string?  ReceiptHeader             { get; set; }
    public string?  ReceiptFooter             { get; set; }
    public decimal  OfflineCreditTolerancePct { get; set; }
    public int      AutoCloseShiftHours       { get; set; } = 12;
    public decimal  VarianceThreshold         { get; set; }
    public bool     IsActive                  { get; set; } = true;
    public int      CreatedBy                 { get; set; }
    public DateTime CreatedAt                 { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt                { get; set; } = DateTime.UtcNow;
    public byte[]   RowVersion                { get; set; } = [];

    public List<PosTerminal>   Terminals { get; set; } = [];
    public List<PosStoreStaff> Staff     { get; set; } = [];
}

/// <summary>A38 A3 — a till. Device binding, presence and the PIN lockout (POS-CSH-07) live here.</summary>
internal class PosTerminal : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                { get; set; }
    public Guid      Uuid              { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId    { get; set; }
    public int       StoreId           { get; set; }
    public string    Code              { get; set; } = string.Empty;
    public string    Name              { get; set; } = string.Empty;
    public Guid?     DeviceUuid        { get; set; }
    public DateTime? DeviceBoundAt     { get; set; }
    public DateTime? LastSeenAt        { get; set; }
    public DateTime? LastSyncAt        { get; set; }
    public int       PinFailedAttempts { get; set; }
    public DateTime? PinLockedUntil    { get; set; }
    public bool      IsActive          { get; set; } = true;
    public int       CreatedBy         { get; set; }
    public DateTime  CreatedAt         { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt        { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// D-17 (POSA) — an app-maintained concurrency stamp rotated only by back-office edits (not SQL rowversion), so a
    /// heartbeat, a sync stamp or a PIN failure never makes an open edit form stale.
    /// </summary>
    public byte[]    RowVersion        { get; set; } = NewStamp();

    public PosStore Store { get; set; } = null!;

    public static byte[] NewStamp() => Guid.NewGuid().ToByteArray()[..8];
}

/// <summary>A38 A6 / D-4 — user ↔ store (many-to-many) with the POS role. The PIN itself is on auth.UserAccounts.</summary>
internal class PosStoreStaff : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public int      StoreId        { get; set; }
    public int      UserId         { get; set; }
    public string   PosRole        { get; set; } = Domain.PosRole.Cashier;
    public bool     IsActive       { get; set; } = true;
    public int      CreatedBy      { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt     { get; set; } = DateTime.UtcNow;

    public PosStore Store { get; set; } = null!;
}

/// <summary>A38 A4 / D-5 — OPEN → CLOSING → CLOSED → RECONCILED | DISPUTED. Totals are server-recomputed (PosShiftTotals).</summary>
internal class PosShift : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                   { get; set; }
    public Guid      Uuid                 { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId       { get; set; }
    public int       StoreId              { get; set; }
    public int       TerminalId           { get; set; }
    public string    ShiftNumber          { get; set; } = string.Empty;
    /// <summary>Store-local date the shift was opened on (its number's date).</summary>
    public DateTime  BusinessDate         { get; set; }
    public string    Status               { get; set; } = PosShiftStatus.Open;
    public int       OpenedBy             { get; set; }
    public DateTime  OpenedAt             { get; set; }
    public int?      ClosingStartedBy     { get; set; }
    public DateTime? ClosingStartedAt     { get; set; }
    public int?      ClosedBy             { get; set; }
    public DateTime? ClosedAt             { get; set; }
    public int?      ReconciledBy         { get; set; }
    public DateTime? ReconciledAt         { get; set; }
    public decimal   OpeningFloat         { get; set; }
    public decimal   TotalCashSales       { get; set; }
    public decimal   TotalCardSales       { get; set; }
    public decimal   TotalMobileSales     { get; set; }
    public decimal   TotalOtherSales      { get; set; }
    public decimal   TotalCreditSales     { get; set; }
    public decimal   TotalCashRefunds     { get; set; }
    public decimal   TotalOtherRefunds    { get; set; }
    public decimal   TotalCashIn          { get; set; }
    public decimal   TotalCashOut         { get; set; }
    /// <summary>A39 D-10 — store credit issued as refunds / redeemed as tenders on this shift (no cash effect).</summary>
    public decimal   TotalStoreCreditIssued   { get; set; }
    public decimal   TotalStoreCreditRedeemed { get; set; }
    public decimal   ExpectedCashClosing  { get; set; }
    public decimal?  ActualCashClosing    { get; set; }
    public decimal?  CashVariance         { get; set; }
    public decimal   VarianceThreshold    { get; set; }
    public string?   VarianceNote         { get; set; }
    /// <summary>Append-only reconcile / supervisor notes (POS-SHF-10), one "[yyyy-MM-dd HH:mm UTC] name: text" per line.</summary>
    public string?   ReconcileNotes       { get; set; }
    public int       OrderCount           { get; set; }
    public int       RefundCount          { get; set; }
    public decimal   GrossTotal           { get; set; }
    public decimal   NetTotal             { get; set; }
    public DateTime? TotalsRecalculatedAt { get; set; }
    /// <summary>Set by StaleShiftAlertJob so a stale shift alerts once.</summary>
    public DateTime? StaleAlertedAt       { get; set; }
    public DateTime  ModifiedAt           { get; set; } = DateTime.UtcNow;
    public byte[]    RowVersion           { get; set; } = [];

    public PosStore    Store    { get; set; } = null!;
    public PosTerminal Terminal { get; set; } = null!;
}

/// <summary>A38 A5 — cash in/out of the drawer. Immutable; idempotent on the client's LocalId.</summary>
internal class PosCashMovement : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public int      ShiftId        { get; set; }
    public string   Direction      { get; set; } = PosCashDirection.In;
    public decimal  Amount         { get; set; }
    public string   Reason         { get; set; } = PosCashReason.Other;
    public string?  Note           { get; set; }
    public int      PerformedBy    { get; set; }
    public DateTime PerformedAt    { get; set; } = DateTime.UtcNow;
    public string?  LocalId        { get; set; }

    public PosShift Shift { get; set; } = null!;
}

/// <summary>A38 A7 — a cashier working a shift on its terminal; one active session per terminal (POS-CSH-04).</summary>
internal class PosCashierSession : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public int       ShiftId        { get; set; }
    public int       TerminalId     { get; set; }
    public int       UserId         { get; set; }
    public DateTime  StartedAt      { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt        { get; set; }
    public string?   EndReason      { get; set; }
    public bool      IsActive       { get; set; } = true;
    public string?   LocalId        { get; set; }
    public DateTime  ModifiedAt     { get; set; } = DateTime.UtcNow;

    public PosShift Shift { get; set; } = null!;
}

/// <summary>A38 A8 — a sale or a return (POS-B owns the logic). Saved first; stock/finance posting is tracked per order.</summary>
internal class PosOrder : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                  { get; set; }
    public Guid      Uuid                { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId      { get; set; }
    public string    OrderNumber         { get; set; } = string.Empty;
    public string    LocalId             { get; set; } = string.Empty;
    public string?   OfflineNumber       { get; set; }
    public string    OrderType           { get; set; } = PosOrderType.Sale;
    public string    Status              { get; set; } = PosOrderStatus.Completed;
    public int       StoreId             { get; set; }
    public int       TerminalId          { get; set; }
    public int       ShiftId             { get; set; }
    public int?      SessionId           { get; set; }
    public int       CashierUserId       { get; set; }
    /// <summary>POSB — the user whose token pushed the order (may differ from the cashier named by an offline order).</summary>
    public int?      SyncedByUserId      { get; set; }
    public Guid      CustomerUuid        { get; set; }
    public string    CustomerCode        { get; set; } = string.Empty;
    public string    CustomerName        { get; set; } = string.Empty;
    public bool      IsWalkIn            { get; set; }
    public decimal   Subtotal            { get; set; }
    public string    DiscountType        { get; set; } = PosDiscountType.None;
    public decimal   DiscountValue       { get; set; }
    public decimal   DiscountAmount      { get; set; }
    public decimal   TaxTotal            { get; set; }
    public decimal   GrandTotal          { get; set; }
    public decimal   PaidAmount          { get; set; }
    public decimal   ChangeAmount        { get; set; }
    public decimal   BalanceDue          { get; set; }
    public string    PaymentStatus       { get; set; } = PosPaymentStatus.Paid;
    public bool      TaxInclusive        { get; set; }
    public Guid?     TaxCodeUuid         { get; set; }
    public string?   TaxCode             { get; set; }
    public decimal   TaxPercent          { get; set; }
    public int?      OriginalOrderId     { get; set; }
    public string?   ReturnReason        { get; set; }
    // A39 (M039.4) — returns / exchanges
    public int?      ReturnReasonId      { get; set; }
    public int?      AuthorizedByUserId  { get; set; }
    public DateTime? AuthorizedAt        { get; set; }
    /// <summary>The store credit note this return/exchange issued as its refund (no FK; the note points back with ReturnOrderId).</summary>
    public int?      StoreCreditNoteId   { get; set; }
    /// <summary>D-5 — retained from the refund. RETURN GrandTotal = Σ line totals − fee (= the refund).</summary>
    public decimal   RestockingFeeAmount { get; set; }
    public bool      IsCrossStore        { get; set; }
    /// <summary>The original sale's store when it differs from StoreId (the processing store) — D-7.</summary>
    public int?      OriginalStoreId     { get; set; }
    public string?   VoidReason          { get; set; }
    public int?      VoidedBy            { get; set; }
    public DateTime? VoidedAt            { get; set; }
    public string?   Note                { get; set; }
    public bool      IsOffline           { get; set; }
    /// <summary>Comma-separated <see cref="PosOrderFlag"/> codes.</summary>
    public string?   Flags               { get; set; }
    /// <summary>Client totals as pushed (audit; the server's totals win, API-CONTRACT §5).</summary>
    public decimal?  ClientGrandTotal    { get; set; }
    public bool      StockPosted         { get; set; }
    public bool      FinancePosted       { get; set; }
    public string?   PostingError        { get; set; }
    public int       PostingAttempts     { get; set; }
    public DateTime? LastPostingAttemptAt { get; set; }
    public Guid?     SalesInvoiceUuid    { get; set; }
    public string?   SalesInvoiceNumber  { get; set; }
    public Guid?     CreditNoteUuid      { get; set; }
    public string?   CreditNoteNumber    { get; set; }
    /// <summary>Store-local date of server receipt (the number's date).</summary>
    public DateTime  BusinessDate        { get; set; }
    public DateTime  CreatedAtLocal      { get; set; }
    public DateTime  CreatedAt           { get; set; } = DateTime.UtcNow;
    public DateTime  SyncedAt            { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt          { get; set; } = DateTime.UtcNow;
    public byte[]    RowVersion          { get; set; } = [];

    public List<PosOrderLine> Lines    { get; set; } = [];
    public List<PosPayment>   Payments { get; set; } = [];
    public PosShift           Shift    { get; set; } = null!;
    public PosOrder?          OriginalOrder { get; set; }

    public IReadOnlyList<string> FlagList =>
        string.IsNullOrWhiteSpace(Flags) ? [] : Flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void AddFlag(string flag)
    {
        if (FlagList.Contains(flag)) return;
        Flags = string.Join(',', FlagList.Append(flag));
    }
}

internal class PosOrderLine : ITenantScopedEntity
{
    public int      Id               { get; set; }
    public Guid     Uuid             { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId   { get; set; }
    public int      OrderId          { get; set; }
    public int      LineNumber       { get; set; }
    public string?  LocalLineId      { get; set; }
    public Guid     VariantUuid      { get; set; }
    public Guid     ProductUuid      { get; set; }
    public string   ProductName      { get; set; } = string.Empty;
    public string?  VariantName      { get; set; }
    public string   Sku              { get; set; } = string.Empty;
    public string?  Barcode          { get; set; }
    public decimal  Quantity         { get; set; }
    public decimal  UnitPrice        { get; set; }
    /// <summary>The catalog price when the order arrived (PRICE_CHANGED when it differs).</summary>
    public decimal? CatalogPrice     { get; set; }
    public string   DiscountType     { get; set; } = PosDiscountType.None;
    public decimal  DiscountValue    { get; set; }
    /// <summary>Line discount + the allocated order discount.</summary>
    public decimal  DiscountAmount   { get; set; }
    public decimal  NetAmount        { get; set; }
    public decimal  TaxPercent       { get; set; }
    public decimal  TaxAmount        { get; set; }
    public decimal  LineTotal        { get; set; }
    public int?     OriginalLineId   { get; set; }
    public decimal  ReturnedQuantity { get; set; }
    public string?  Note             { get; set; }

    public PosOrder Order { get; set; } = null!;
}

/// <summary>A38 A10 — a tender (or, IsRefund, a refund). Amounts positive; ShiftId denormalised for the shift totals.</summary>
internal class PosPayment : ITenantScopedEntity
{
    public int      Id                  { get; set; }
    public Guid     Uuid                { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId      { get; set; }
    public int      OrderId             { get; set; }
    public int      ShiftId             { get; set; }
    public string?  LocalId             { get; set; }
    public string   Method              { get; set; } = PosPaymentMethod.Cash;
    public decimal  Amount              { get; set; }
    public decimal  ChangeGiven         { get; set; }
    public string?  Reference           { get; set; }
    public bool     IsRefund            { get; set; }
    public Guid?    CustomerPaymentUuid { get; set; }
    /// <summary>A39 M039.6 — STORE_CREDIT: the note redeemed (tender) or issued (refund).</summary>
    public int?     CreditNoteId        { get; set; }
    public DateTime CreatedAt           { get; set; } = DateTime.UtcNow;

    public PosOrder Order { get; set; } = null!;
    public PosStoreCreditNote? CreditNote { get; set; }
}

/// <summary>A38 A11 / D-6 — POS-only product data; barcode and price stay on the variant.</summary>
internal class PosProductSettings : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public Guid     ProductUuid    { get; set; }
    public string?  PosName        { get; set; }
    public int?     PosCategoryId  { get; set; }
    public string?  Color          { get; set; }
    public string?  ImageUrl       { get; set; }
    public bool     IsWeighable    { get; set; }
    public bool     IsFavorite     { get; set; }
    public int      SortOrder      { get; set; }
    public bool     IsPosActive    { get; set; } = true;
    public int?     ModifiedBy     { get; set; }
    public DateTime ModifiedAt     { get; set; } = DateTime.UtcNow;
}

/// <summary>A38 A12 / D-12 — POS credit settings of a customer (business partner UUID).</summary>
internal class PosCustomerSettings : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                         { get; set; }
    public Guid     OrganizationId             { get; set; }
    public Guid     CustomerUuid               { get; set; }
    public bool     AllowPosCredit             { get; set; }
    public decimal? OfflineCreditLimitOverride { get; set; }
    public decimal? MaxSingleCreditOrder       { get; set; }
    public bool     RequiresIdForCredit        { get; set; }
    public string?  Notes                      { get; set; }
    public int?     ModifiedBy                 { get; set; }
    public DateTime ModifiedAt                 { get; set; } = DateTime.UtcNow;
}

/// <summary>D-7 — the last number handed out per (store, prefix, store-local day).</summary>
internal class PosNumberSequence : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public int      StoreId        { get; set; }
    public string   Prefix         { get; set; } = string.Empty;
    public DateTime BusinessDate   { get; set; }
    public int      LastValue      { get; set; }
    public DateTime ModifiedAt     { get; set; } = DateTime.UtcNow;
}
