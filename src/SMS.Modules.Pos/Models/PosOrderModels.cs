namespace SMS.Modules.Pos.Models;

// A38 API-CONTRACT §5–§7 (POS-B). camelCase on the wire; money 2dp in the org sale base.

// ── §5 Orders & payments ─────────────────────────────────────────────────────

public class PosSyncOrderRequest
{
    public string?          LocalId            { get; set; }
    public Guid             TerminalUuid       { get; set; }
    public Guid             ShiftUuid          { get; set; }
    public Guid?            SessionUuid        { get; set; }
    /// <summary>POSB — the device's id for an offline session (the server creates/reuses one cashier session per id).</summary>
    public string?          SessionLocalId     { get; set; }
    /// <summary>POSB — the cashier who made the sale (offline: may differ from the pushing user). Must be active staff with POS_ORDER_CREATE.</summary>
    public int?             CashierUserId      { get; set; }
    public string?          OfflineNumber      { get; set; }
    /// <summary>ISO-8601 with offset (send UTC, "Z"); stored as UTC.</summary>
    public DateTimeOffset?  CreatedAtLocal     { get; set; }
    public bool             IsOffline          { get; set; }
    /// <summary>Null ⇒ the organization's walk-in customer.</summary>
    public Guid?            CustomerUuid       { get; set; }
    public List<PosSyncLineRequest>    Lines    { get; set; } = [];
    public string?          OrderDiscountType  { get; set; }
    public decimal          OrderDiscountValue { get; set; }
    public string?          Note               { get; set; }
    public List<PosSyncPaymentRequest> Payments { get; set; } = [];
    public bool             OfflineCredit      { get; set; }
    public PosClientTotals? ClientTotals       { get; set; }
}

public class PosSyncLineRequest
{
    public string?  LocalLineId   { get; set; }
    public Guid     VariantUuid   { get; set; }
    public decimal  Quantity      { get; set; }
    public decimal  UnitPrice     { get; set; }
    public string?  DiscountType  { get; set; }
    public decimal  DiscountValue { get; set; }
    public string?  Barcode       { get; set; }
    public string?  Note          { get; set; }
}

public class PosSyncPaymentRequest
{
    public string?  LocalId     { get; set; }
    public string?  Method      { get; set; }
    public decimal  Amount      { get; set; }
    public decimal  ChangeGiven { get; set; }
    public string?  Reference   { get; set; }
    /// <summary>A39 — STORE_CREDIT tender: the store credit note redeemed (online only).</summary>
    public Guid?    CreditNoteUuid { get; set; }
}

public class PosClientTotals
{
    public decimal Subtotal       { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxTotal       { get; set; }
    public decimal GrandTotal     { get; set; }
}

public class PosSyncBatchRequest { public List<PosSyncOrderRequest> Orders { get; set; } = []; }

public class PosSyncResultModel
{
    public string?       LocalId     { get; set; }
    public bool          Success     { get; set; }
    public Guid?         OrderUuid   { get; set; }
    public string?       OrderNumber { get; set; }
    public string?       Status      { get; set; }
    public List<string>? Flags       { get; set; }
    public List<string>? Warnings    { get; set; }
    public string?       Error       { get; set; }
}

public class PosOrderModel
{
    public Guid      Uuid                { get; set; }
    public string    OrderNumber         { get; set; } = string.Empty;
    public string    LocalId             { get; set; } = string.Empty;
    public string?   OfflineNumber       { get; set; }
    public string    OrderType           { get; set; } = string.Empty;
    public string    Status              { get; set; } = string.Empty;
    public Guid      StoreUuid           { get; set; }
    public string    StoreName           { get; set; } = string.Empty;
    public Guid      TerminalUuid        { get; set; }
    public string    TerminalName        { get; set; } = string.Empty;
    public Guid      ShiftUuid           { get; set; }
    public string    ShiftNumber         { get; set; } = string.Empty;
    public Guid?     SessionUuid         { get; set; }
    public int       CashierUserId       { get; set; }
    public string    CashierName         { get; set; } = string.Empty;
    /// <summary>POSB — who pushed the order (null for returns, which are online).</summary>
    public int?      SyncedByUserId      { get; set; }
    public string?   SyncedByName        { get; set; }
    public Guid      CustomerUuid        { get; set; }
    public string    CustomerCode        { get; set; } = string.Empty;
    public string    CustomerName        { get; set; } = string.Empty;
    public bool      IsWalkIn            { get; set; }
    public List<PosOrderLineModel> Lines    { get; set; } = [];
    public List<PosPaymentModel>   Payments { get; set; } = [];
    public decimal   Subtotal            { get; set; }
    public string    DiscountType        { get; set; } = string.Empty;
    public decimal   DiscountValue       { get; set; }
    public decimal   DiscountAmount      { get; set; }
    public decimal   TaxTotal            { get; set; }
    public decimal   GrandTotal          { get; set; }
    public decimal   PaidAmount          { get; set; }
    public decimal   ChangeAmount        { get; set; }
    public decimal   BalanceDue          { get; set; }
    public string    PaymentStatus       { get; set; } = string.Empty;
    public bool      TaxInclusive        { get; set; }
    public string?   TaxCode             { get; set; }
    public decimal   TaxPercent          { get; set; }
    public Guid?     OriginalOrderUuid   { get; set; }
    public string?   OriginalOrderNumber { get; set; }
    public string?   ReturnReason        { get; set; }
    public string?   VoidReason          { get; set; }
    public string?   Note                { get; set; }
    public bool      IsOffline           { get; set; }
    public List<string> Flags            { get; set; } = [];
    public bool      StockPosted         { get; set; }
    public bool      FinancePosted       { get; set; }
    public string?   PostingError        { get; set; }
    public Guid?     SalesInvoiceUuid    { get; set; }
    public string?   SalesInvoiceNumber  { get; set; }
    public Guid?     CreditNoteUuid      { get; set; }
    public string?   CreditNoteNumber    { get; set; }
    public DateTime  CreatedAt           { get; set; }
    public DateTime  SyncedAt            { get; set; }
    public DateTime  CreatedAtLocal      { get; set; }
    public List<PosOrderReturnRefModel> Returns { get; set; } = [];
    // A39 §3
    public Guid?     ReturnReasonUuid    { get; set; }
    public string?   ReturnReasonName    { get; set; }
    public int?      AuthorizedByUserId  { get; set; }
    public string?   AuthorizedByName    { get; set; }
    public DateTime? AuthorizedAt        { get; set; }
    public decimal   RestockingFeeAmount { get; set; }
    public bool      IsCrossStore        { get; set; }
    public Guid?     OriginalStoreUuid   { get; set; }
    public string?   OriginalStoreName   { get; set; }
    public PosOrderStoreCreditModel? StoreCredit { get; set; }
    /// <summary>EXCHANGE: new total − (return total − fee); &gt; 0 paid by the customer, &lt; 0 refunded.</summary>
    public decimal?  NetAmount           { get; set; }
    /// <summary>EXCHANGE: PAY, EVEN or REFUND.</summary>
    public string?   Settlement          { get; set; }
}

public class PosOrderStoreCreditModel
{
    public Guid      Uuid      { get; set; }
    public string    Number    { get; set; } = string.Empty;
    public decimal   Amount    { get; set; }
    public decimal   Balance   { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class PosOrderLineModel
{
    public Guid     Uuid               { get; set; }
    public int      LineNumber         { get; set; }
    public Guid     VariantUuid        { get; set; }
    public string   ProductName        { get; set; } = string.Empty;
    public string?  VariantName        { get; set; }
    public string   Sku                { get; set; } = string.Empty;
    public string?  Barcode            { get; set; }
    public decimal  Quantity           { get; set; }
    public decimal  UnitPrice          { get; set; }
    public string   DiscountType       { get; set; } = string.Empty;
    public decimal  DiscountValue      { get; set; }
    public decimal  DiscountAmount     { get; set; }
    public decimal  NetAmount          { get; set; }
    public decimal  TaxPercent         { get; set; }
    public decimal  TaxAmount          { get; set; }
    public decimal  LineTotal          { get; set; }
    public Guid?    OriginalLineUuid   { get; set; }
    public decimal  ReturnedQuantity   { get; set; }
    public decimal  ReturnableQuantity { get; set; }
    public string?  Note               { get; set; }
}

public class PosPaymentModel
{
    public Guid     Uuid                { get; set; }
    public string   Method              { get; set; } = string.Empty;
    public decimal  Amount              { get; set; }
    public decimal  ChangeGiven         { get; set; }
    public string?  Reference           { get; set; }
    public bool     IsRefund            { get; set; }
    public Guid?    CustomerPaymentUuid { get; set; }
    public Guid?    CreditNoteUuid      { get; set; }
    public DateTime CreatedAt           { get; set; }
}

public class PosOrderReturnRefModel
{
    public Guid    Uuid        { get; set; }
    public string  OrderNumber { get; set; } = string.Empty;
    public decimal GrandTotal  { get; set; }
}

public class PosOrderListItemModel
{
    public Guid         Uuid          { get; set; }
    public string       OrderNumber   { get; set; } = string.Empty;
    public string       OrderType     { get; set; } = string.Empty;
    public string       Status        { get; set; } = string.Empty;
    public string       StoreName     { get; set; } = string.Empty;
    public string       TerminalName  { get; set; } = string.Empty;
    public string       CashierName   { get; set; } = string.Empty;
    public string       CustomerName  { get; set; } = string.Empty;
    public decimal      GrandTotal    { get; set; }
    public string       PaymentStatus { get; set; } = string.Empty;
    public List<string> Flags         { get; set; } = [];
    public DateTime     CreatedAt     { get; set; }
    public bool         IsOffline     { get; set; }
    public bool         FinancePosted { get; set; }
    public bool         StockPosted   { get; set; }
}

public class PosOrderListFilter
{
    public Guid?     StoreUuid    { get; set; }
    public Guid?     TerminalUuid { get; set; }
    public Guid?     ShiftUuid    { get; set; }
    public Guid?     CustomerUuid { get; set; }
    /// <summary>One code or a comma list.</summary>
    public string?   Status       { get; set; }
    public string?   OrderType    { get; set; }
    public DateTime? From         { get; set; }
    /// <summary>Without a time: inclusive of that day.</summary>
    public DateTime? To           { get; set; }
    public string?   Search       { get; set; }
    public int       Page         { get; set; } = 1;
    public int       PageSize     { get; set; } = 20;
}

public class PosVoidRequest { public string? Reason { get; set; } }

/// <summary>
/// A38 return request, enriched by A39 §3 (D-1). A39 clients send <c>reasonUuid</c> + <c>refundMethod</c>; the A38 shape
/// (<c>reason</c> text + <c>refunds[]</c>) is still accepted (POSR D-13).
/// </summary>
public class PosReturnRequest
{
    public string?  LocalId   { get; set; }
    /// <summary>A38: free-text reason (used when no <see cref="ReasonUuid"/> is sent).</summary>
    public string?  Reason    { get; set; }
    /// <summary>POSB: the OPEN shift that pays the refund; omitted ⇒ the terminal's (else the original terminal's) open shift.</summary>
    public Guid?    ShiftUuid { get; set; }
    public List<PosReturnLineRequest>   Lines   { get; set; } = [];
    /// <summary>A38: explicit refund tenders. A39 clients send <see cref="RefundMethod"/> instead.</summary>
    public List<PosReturnRefundRequest> Refunds { get; set; } = [];
    // A39 §3
    /// <summary>The processing terminal (its OPEN shift pays the refund when <see cref="ShiftUuid"/> is omitted).</summary>
    public Guid?    TerminalUuid           { get; set; }
    public Guid?    SessionUuid            { get; set; }
    public Guid?    ReasonUuid             { get; set; }
    public string?  Note                   { get; set; }
    public string?  AuthorizationToken     { get; set; }
    /// <summary>CASH CARD MOBILE BANK_TRANSFER CHEQUE ON_ACCOUNT STORE_CREDIT — the whole refund.</summary>
    public string?  RefundMethod           { get; set; }
    public string?  RefundReference        { get; set; }
    /// <summary>STORE_CREDIT refund: the note's customer. Omitted ⇒ the original customer, bearer when walk-in.</summary>
    public Guid?    StoreCreditCustomerUuid { get; set; }
    /// <summary>A39 QA (FEPOS sends it, as for A38 sales): the cashier who made the return on the till (offline: may differ from the
    /// pushing user). Honoured when active staff of the processing store with POS_ORDER_RETURN; else booked to the caller + warning.</summary>
    public int?     CashierUserId          { get; set; }
    /// <summary>A39 QA (FEPOS): the device's session id — the return lands in that (already synced) session of the shift.</summary>
    public string?  SessionLocalId         { get; set; }
}

/// <summary>A39 §3 / D-6 — the return part (as <see cref="PosReturnRequest"/>) plus the new items and the net settlement.</summary>
public class PosExchangeRequest : PosReturnRequest
{
    public List<PosSyncLineRequest>    NewLines           { get; set; } = [];
    public string?                     OrderDiscountType  { get; set; }
    public decimal                     OrderDiscountValue { get; set; }
    /// <summary>Net &gt; 0 only (may include STORE_CREDIT with creditNoteUuid, online).</summary>
    public List<PosSyncPaymentRequest> Payments           { get; set; } = [];
}

/// <summary>A39 §3 <c>POST api/pos/returns/sync</c> — a return or exchange made on the till (possibly offline).</summary>
public class PosReturnSyncRequest : PosExchangeRequest
{
    /// <summary>RETURN or EXCHANGE.</summary>
    public string?          Kind               { get; set; }
    public Guid             OriginalOrderUuid  { get; set; }
    public DateTimeOffset?  CreatedAtLocal     { get; set; }
    public bool             IsOffline          { get; set; }
    /// <summary>Offline: the manager whose PIN the device verified (flag AUTHORIZED_OFFLINE; checked at sync).</summary>
    public int?             AuthorizedByUserId { get; set; }
    /// <summary>Offline STORE_CREDIT refund: the device's CN-{store}-OFF-… number (kept as the note's offline number).</summary>
    public string?          StoreCreditOfflineNumber { get; set; }
    /// <summary>Offline order number shown on the device's receipt.</summary>
    public string?          OfflineNumber      { get; set; }
}

public class PosReturnLineRequest
{
    public Guid    OriginalLineUuid { get; set; }
    public decimal Quantity         { get; set; }
}

public class PosReturnRefundRequest
{
    public string?  Method    { get; set; }
    public decimal  Amount    { get; set; }
    public string?  Reference { get; set; }
}

public class PosCustomerCreditModel
{
    public decimal  Balance              { get; set; }
    public decimal  CreditLimit          { get; set; }
    public decimal  Available            { get; set; }
    public bool     AllowPosCredit       { get; set; }
    public decimal? MaxSingleCreditOrder { get; set; }
    public bool     RequiresIdForCredit  { get; set; }
    public bool     IsWalkIn             { get; set; }
}

// ── §6 POS catalog / customer settings ───────────────────────────────────────

public class PosProductSettingsModel
{
    /// <summary>Bulk only (ignored by PUT, which takes it from the route).</summary>
    public Guid?     ProductUuid   { get; set; }
    public string?   PosName       { get; set; }
    public int?      PosCategoryId { get; set; }
    public string?   Color         { get; set; }
    public string?   ImageUrl      { get; set; }
    public bool      IsWeighable   { get; set; }
    public bool      IsFavorite    { get; set; }
    public int       SortOrder     { get; set; }
    public bool      IsPosActive   { get; set; } = true;
    public DateTime? ModifiedAt    { get; set; }
}

public class PosProductSettingsBulkRequest { public List<PosProductSettingsModel> Items { get; set; } = []; }

public class PosBulkResultModel { public int Updated { get; set; } }

public class PosCustomerSettingsModel
{
    public bool      AllowPosCredit             { get; set; }
    public decimal?  OfflineCreditLimitOverride { get; set; }
    public decimal?  MaxSingleCreditOrder       { get; set; }
    public bool      RequiresIdForCredit        { get; set; }
    public string?   Notes                      { get; set; }
    public DateTime? ModifiedAt                 { get; set; }
}

// ── §7 PWA sync ──────────────────────────────────────────────────────────────

public class PosSyncConfigModel
{
    public DateTime           ServerTime      { get; set; }
    public PosTerminalModel   Terminal        { get; set; } = new();
    public PosStoreModel      Store           { get; set; } = new();
    public PosSyncTaxCodeModel TaxCode        { get; set; } = new();
    public List<string>       PaymentMethods  { get; set; } = [];
    public PosSyncFeaturesModel Features      { get; set; } = new();
    public PosSyncCurrencyModel Currency      { get; set; } = new();
}

public class PosSyncTaxCodeModel
{
    public Guid?   Uuid    { get; set; }
    public string? Code    { get; set; }
    public decimal Percent { get; set; }
}

public class PosSyncFeaturesModel
{
    public bool Returns     { get; set; }
    public bool CreditSales { get; set; }
    public bool Barcode     { get; set; }
    public bool MultiStore  { get; set; }
    // A39
    public bool CreditNotes       { get; set; }
    public bool CrossStoreReturns { get; set; }
}

public class PosSyncCurrencyModel
{
    public string? Code     { get; set; }
    public int     Decimals { get; set; } = 2;
}

public class PosCatalogPageModel
{
    public DateTime                 ServerTime { get; set; }
    public DateTime?                NextSince  { get; set; }
    public bool                     HasMore    { get; set; }
    public List<PosCatalogItemModel> Items     { get; set; } = [];
    public List<PosCategoryModel>   Categories { get; set; } = [];
}

public class PosCatalogItemModel
{
    public Guid     VariantUuid  { get; set; }
    public Guid     ProductUuid  { get; set; }
    public string   Sku          { get; set; } = string.Empty;
    public string   Name         { get; set; } = string.Empty;
    public string?  PosName      { get; set; }
    public string?  VariantName  { get; set; }
    public string?  Barcode      { get; set; }
    public int?     CategoryId   { get; set; }
    public string?  CategoryName { get; set; }
    public string?  UomCode      { get; set; }
    public decimal  Price        { get; set; }
    public string?  ImageUrl     { get; set; }
    public string?  Color        { get; set; }
    public bool     IsWeighable  { get; set; }
    public bool     IsFavorite   { get; set; }
    public int      SortOrder    { get; set; }
    public bool     IsActive     { get; set; }
    /// <summary>False for items that hold no stock (prepared food, services): the till does not flag them as out of stock.</summary>
    public bool     IsStockable  { get; set; } = true;
    public DateTime ModifiedAt   { get; set; }
}

public class PosCategoryModel
{
    public int    Id        { get; set; }
    public string Name      { get; set; } = string.Empty;
    public int    SortOrder { get; set; }
}

public class PosStockSnapshotModel
{
    public DateTime                ServerTime { get; set; }
    public List<PosStockItemModel> Items      { get; set; } = [];
}

public class PosStockItemModel
{
    public Guid    VariantUuid { get; set; }
    public decimal Available   { get; set; }
}

public class PosCustomersPageModel
{
    public DateTime                   ServerTime { get; set; }
    public DateTime?                  NextSince  { get; set; }
    public bool                       HasMore    { get; set; }
    public List<PosSyncCustomerModel> Customers  { get; set; } = [];
}

public class PosSyncCustomerModel
{
    public Guid     Uuid                       { get; set; }
    public string   Code                       { get; set; } = string.Empty;
    public string   Name                       { get; set; } = string.Empty;
    public string   CustomerType               { get; set; } = string.Empty;
    public string?  Phone                      { get; set; }
    public string?  Mobile                     { get; set; }
    public string?  Email                      { get; set; }
    public decimal  CreditLimit                { get; set; }
    public decimal  Balance                    { get; set; }
    public bool     IsActive                   { get; set; }
    public bool     IsSystem                   { get; set; }
    public bool     AllowPosCredit             { get; set; }
    public decimal? OfflineCreditLimitOverride { get; set; }
    public decimal? MaxSingleCreditOrder       { get; set; }
    public bool     RequiresIdForCredit        { get; set; }
    public DateTime ModifiedAt                 { get; set; }
}

public class PosSyncUserModel
{
    public int          UserId      { get; set; }
    public string       Name        { get; set; } = string.Empty;
    public string       PosRole     { get; set; } = string.Empty;
    public string       PinHash     { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}
