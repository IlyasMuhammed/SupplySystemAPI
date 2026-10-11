using SMS.Shared.Exceptions;

namespace SMS.Modules.Pos.Models;

// A39 API-CONTRACT §1–§5 (POSR). camelCase on the wire; money 2dp in the org sale base.

/// <summary>A 400 that carries a <c>result</c> (e.g. <c>{ requiresManager, reasons }</c>) — written by <see cref="Controllers.PosRuleResultFilter"/>.</summary>
public sealed class PosRuleException : BadRequestException
{
    public object Result { get; }
    public PosRuleException(string message, object result) : base(message) => Result = result;
}

public sealed class PosManagerRequiredResult
{
    public bool         RequiresManager { get; set; } = true;
    public List<string> Reasons         { get; set; } = [];
}

// ── §1 Reasons & policies ────────────────────────────────────────────────────

public class PosReturnReasonModel
{
    public Guid     Uuid                  { get; set; }
    public string   Code                  { get; set; } = string.Empty;
    public string   Name                  { get; set; } = string.Empty;
    public string?  Description           { get; set; }
    public bool     RequiresNote          { get; set; }
    public bool     RequiresManager       { get; set; }
    public string   AppliesTo             { get; set; } = string.Empty;
    public decimal? RestockingFeeOverride { get; set; }
    public int      SortOrder             { get; set; }
    public bool     IsActive              { get; set; }
    public DateTime ModifiedAt            { get; set; }
}

public class PosReturnReasonUpsertRequest
{
    public string?  Code                  { get; set; }
    public string?  Name                  { get; set; }
    public string?  Description           { get; set; }
    public bool     RequiresNote          { get; set; }
    public bool     RequiresManager       { get; set; }
    public string?  AppliesTo             { get; set; }
    public decimal? RestockingFeeOverride { get; set; }
    public int      SortOrder             { get; set; }
    public bool     IsActive              { get; set; } = true;
}

public class PosReturnPolicyModel
{
    public Guid     Uuid                      { get; set; }
    public Guid?    StoreUuid                 { get; set; }
    public string?  StoreName                 { get; set; }
    public string   Name                      { get; set; } = string.Empty;
    public int?     ReturnWindowDays          { get; set; }
    public int?     ExchangeWindowDays        { get; set; }
    public bool     RequireReceipt            { get; set; }
    public bool     AllowNoReceiptExchange    { get; set; }
    public bool     AllowCrossStoreReturn     { get; set; }
    public bool     CrossStoreRequiresManager { get; set; }
    public decimal  RestockingFeePct          { get; set; }
    public decimal? MaxReturnNoAuth           { get; set; }
    public bool     RefundToOriginalMethod    { get; set; }
    public bool     AllowCashRefund           { get; set; }
    public bool     AllowCreditNoteRefund     { get; set; }
    public int?     CreditNoteExpiryDays      { get; set; }
    public bool     IsActive                  { get; set; }
    public byte[]?  RowVersion                { get; set; }
    public DateTime ModifiedAt                { get; set; }
}

/// <summary>EffectivePolicy — the policy that applies to a store; <c>source</c> STORE or ORG_DEFAULT.</summary>
public class PosEffectivePolicyModel : PosReturnPolicyModel
{
    public string Source { get; set; } = string.Empty;
}

public class PosReturnPolicyUpsertRequest
{
    public string?  Name                      { get; set; }
    public int?     ReturnWindowDays          { get; set; }
    public int?     ExchangeWindowDays        { get; set; }
    public bool     RequireReceipt            { get; set; } = true;
    public bool     AllowNoReceiptExchange    { get; set; }
    public bool     AllowCrossStoreReturn     { get; set; }
    public bool     CrossStoreRequiresManager { get; set; }
    public decimal  RestockingFeePct          { get; set; }
    public decimal? MaxReturnNoAuth           { get; set; }
    public bool     RefundToOriginalMethod    { get; set; }
    public bool     AllowCashRefund           { get; set; } = true;
    public bool     AllowCreditNoteRefund     { get; set; } = true;
    public int?     CreditNoteExpiryDays      { get; set; }
    public bool     IsActive                  { get; set; } = true;
    /// <summary>Null skips the check (first save, or a client that does not care).</summary>
    public byte[]?  RowVersion                { get; set; }
}

public class PosReasonListFilter
{
    public string? AppliesTo       { get; set; }
    public bool    IncludeInactive { get; set; }
}

public class PosReasonSyncModel
{
    public DateTime                   ServerTime { get; set; }
    public DateTime?                  NextSince  { get; set; }
    public List<PosReturnReasonModel> Reasons    { get; set; } = [];
}

// ── §2 Lookup & returnable lines ─────────────────────────────────────────────

public class PosOrderLookupModel
{
    public bool                   Found  { get; set; }
    public PosLookupOrderModel?   Order  { get; set; }
    public PosLookupPolicyModel?  Policy { get; set; }
}

public class PosLookupOrderModel
{
    public Guid     Uuid              { get; set; }
    public string   OrderNumber       { get; set; } = string.Empty;
    public Guid     StoreUuid         { get; set; }
    public string   StoreName         { get; set; } = string.Empty;
    public string   StoreCode         { get; set; } = string.Empty;
    public DateTime CreatedAt         { get; set; }
    public decimal  GrandTotal        { get; set; }
    public Guid     CustomerUuid      { get; set; }
    public string   CustomerName      { get; set; } = string.Empty;
    public bool     IsWalkIn          { get; set; }
    public string   Status            { get; set; } = string.Empty;
    public string   OrderType         { get; set; } = string.Empty;
    public bool     IsSameStore       { get; set; }
    public int      DaysSincePurchase { get; set; }
    public List<PosLookupPaymentModel> Payments { get; set; } = [];
    public List<PosLookupLineModel>    Lines    { get; set; } = [];
}

public class PosLookupPaymentModel
{
    public string  Method    { get; set; } = string.Empty;
    public decimal Amount    { get; set; }
    public string? Reference { get; set; }
}

public class PosLookupLineModel
{
    public Guid    LineUuid            { get; set; }
    public Guid    VariantUuid         { get; set; }
    public string  ProductName         { get; set; } = string.Empty;
    public string  Sku                 { get; set; } = string.Empty;
    public decimal Quantity            { get; set; }
    public decimal ReturnedQuantity    { get; set; }
    public decimal ReturnableQuantity  { get; set; }
    public decimal UnitPrice           { get; set; }
    public decimal TaxPercent          { get; set; }
    public decimal LineTotal           { get; set; }
    /// <summary>What the rest of the line refunds (lineTotal − Σ already returned) — the remainder rule.</summary>
    public decimal RefundIfAllReturned { get; set; }
}

public class PosLookupPolicyModel : PosEffectivePolicyModel
{
    public bool WithinReturnWindow           { get; set; }
    public bool WithinExchangeWindow         { get; set; }
    public int? WindowDaysRemaining          { get; set; }
    public bool CrossStoreAllowed            { get; set; }
    public bool RequiresManagerForCrossStore { get; set; }
}

// ── §3 Authorization ─────────────────────────────────────────────────────────

public class PosReturnAuthorizeRequest
{
    public Guid         TerminalUuid      { get; set; }
    public Guid         OriginalOrderUuid { get; set; }
    public decimal      Amount            { get; set; }
    public int?         ManagerUserId     { get; set; }
    public string?      Pin               { get; set; }
    /// <summary>What needs the manager (REASON AMOUNT WINDOW CROSS_STORE); only CROSS_STORE lets POS_RETURN_CROSS_STORE suffice.</summary>
    public List<string> Reasons           { get; set; } = [];
}

public class PosReturnAuthorizationModel
{
    public string   AuthorizationToken { get; set; } = string.Empty;
    public int      ManagerUserId      { get; set; }
    public string   ManagerName        { get; set; } = string.Empty;
    public DateTime ExpiresAt          { get; set; }
}

// ── §4 Store credit ──────────────────────────────────────────────────────────

public class PosStoreCreditIssueRequest
{
    public Guid      StoreUuid    { get; set; }
    public Guid?     CustomerUuid { get; set; }
    public decimal   Amount       { get; set; }
    public DateTime? ExpiresAt    { get; set; }
    public string?   Note         { get; set; }
    public string?   LocalId      { get; set; }
}

public class PosStoreCreditModel
{
    public Guid      Uuid                 { get; set; }
    public string    Number               { get; set; } = string.Empty;
    public string?   OfflineNumber        { get; set; }
    public Guid?     CustomerUuid         { get; set; }
    public string?   CustomerName         { get; set; }
    public Guid?     ReturnOrderUuid      { get; set; }
    public string?   ReturnOrderNumber    { get; set; }
    public decimal   OriginalAmount       { get; set; }
    public decimal   RemainingBalance     { get; set; }
    public decimal   RedeemedAmount       { get; set; }
    public string?   CurrencyCode         { get; set; }
    public string    Status               { get; set; } = string.Empty;
    public string    IssuedAtStoreName    { get; set; } = string.Empty;
    public string    IssuedByName         { get; set; } = string.Empty;
    public DateTime  IssuedAt             { get; set; }
    public DateTime? ExpiresAt            { get; set; }
    public DateTime? LastUsedAt           { get; set; }
    public string?   LastUsedAtStoreName  { get; set; }
    public string?   Note                 { get; set; }
    public DateTime  ModifiedAt           { get; set; }
}

public class PosStoreCreditDetailModel : PosStoreCreditModel
{
    public List<PosStoreCreditTxnModel> Transactions { get; set; } = [];
}

public class PosStoreCreditTxnModel
{
    public Guid     Uuid            { get; set; }
    public string   Type            { get; set; } = string.Empty;
    public decimal  Amount          { get; set; }
    public decimal  BalanceAfter    { get; set; }
    public Guid?    OrderUuid       { get; set; }
    public string?  OrderNumber     { get; set; }
    public string   StoreName       { get; set; } = string.Empty;
    public string   PerformedByName { get; set; } = string.Empty;
    public DateTime PerformedAt     { get; set; }
    public string?  Note            { get; set; }
}

public class PosStoreCreditListFilter
{
    public string? Status       { get; set; }
    public Guid?   CustomerUuid { get; set; }
    public Guid?   StoreUuid    { get; set; }
    public string? Search       { get; set; }
    public int     Page         { get; set; } = 1;
    public int     PageSize     { get; set; } = 20;
}

public class PosStoreCreditCancelRequest { public string? Note { get; set; } }
