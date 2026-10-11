using SMS.Shared.Common;

namespace SMS.Modules.Pos.Domain;

// A39 — returns, exchanges & store credit (docs/pos-returns/ADDENDUM-39-ANALYSIS.md D-1..D-12, API-CONTRACT.md).

internal static class PosReasonAppliesTo
{
    public const string Both = "BOTH", Return = "RETURN", Exchange = "EXCHANGE";
    public static bool IsValid(string? v) => v is Both or Return or Exchange;
    /// <summary>A reason usable for <paramref name="kind"/> (RETURN or EXCHANGE).</summary>
    public static bool Allows(string appliesTo, string kind) => appliesTo == Both || appliesTo == kind;
}

internal static class PosStoreCreditStatus
{
    public const string Active = "ACTIVE", FullyRedeemed = "FULLY_REDEEMED", Expired = "EXPIRED", Cancelled = "CANCELLED";
}

internal static class PosStoreCreditTxnType
{
    public const string Issue = "ISSUE", Redeem = "REDEEM", Expire = "EXPIRE", Cancel = "CANCEL", Adjust = "ADJUST";
}

/// <summary>Why a manager must authorize a return (API-CONTRACT §3 <c>result.reasons</c>).</summary>
internal static class PosManagerReason
{
    public const string Reason = "REASON", Amount = "AMOUNT", Window = "WINDOW", CrossStore = "CROSS_STORE";
}

/// <summary>A39 M039.1 / D-4 — return &amp; exchange reasons per organization (9 seeded lazily).</summary>
internal class PosReturnReason : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                    { get; set; }
    public Guid     Uuid                  { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId        { get; set; }
    public string   Code                  { get; set; } = string.Empty;
    public string   Name                  { get; set; } = string.Empty;
    public string?  Description           { get; set; }
    public bool     RequiresNote          { get; set; }
    public bool     RequiresManager       { get; set; }
    public string   AppliesTo             { get; set; } = PosReasonAppliesTo.Both;
    /// <summary>Restocking fee % that replaces the policy's for this reason (0 waives it). Null = the policy's.</summary>
    public decimal? RestockingFeeOverride { get; set; }
    public int      SortOrder             { get; set; }
    public bool     IsActive              { get; set; } = true;
    public DateTime CreatedAt             { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt            { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A39 M039.2 / D-4 — a return policy: StoreId null = the organization default (one per org), else a store override (one per
/// store). Resolution: active store policy → active org default → none ("No return policy configured…").
/// </summary>
internal class PosReturnPolicy : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                        { get; set; }
    public Guid     Uuid                      { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId            { get; set; }
    public int?     StoreId                   { get; set; }
    public string   Name                      { get; set; } = string.Empty;
    /// <summary>Null = no limit; days counted between the store-local sale date and the return date.</summary>
    public int?     ReturnWindowDays          { get; set; }
    /// <summary>Null = the return window; 0 = no exchanges.</summary>
    public int?     ExchangeWindowDays        { get; set; }
    public bool     RequireReceipt            { get; set; } = true;
    public bool     AllowNoReceiptExchange    { get; set; }
    public bool     AllowCrossStoreReturn     { get; set; }
    public bool     CrossStoreRequiresManager { get; set; }
    public decimal  RestockingFeePct          { get; set; }
    /// <summary>Refunds above this need a manager. Null = no ceiling.</summary>
    public decimal? MaxReturnNoAuth           { get; set; }
    public bool     RefundToOriginalMethod    { get; set; }
    public bool     AllowCashRefund           { get; set; } = true;
    public bool     AllowCreditNoteRefund     { get; set; } = true;
    public int?     CreditNoteExpiryDays      { get; set; }
    public bool     IsActive                  { get; set; } = true;
    public int?     ModifiedBy                { get; set; }
    public DateTime CreatedAt                 { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt                { get; set; } = DateTime.UtcNow;
    public byte[]   RowVersion                { get; set; } = [];

    public PosStore? Store { get; set; }
}

/// <summary>A39 M039.10 / D-2 — a store credit voucher ("credit note" in the spec; Finance's SCN- credit notes are different).</summary>
internal class PosStoreCreditNote : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id               { get; set; }
    public Guid      Uuid             { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId   { get; set; }
    public string    Number           { get; set; } = string.Empty;
    /// <summary>The device's CN-{store}-OFF-… number of an offline issue (replaced by <see cref="Number"/> at sync).</summary>
    public string?   OfflineNumber    { get; set; }
    /// <summary>Idempotency of a standalone issue.</summary>
    public string?   LocalId          { get; set; }
    /// <summary>Null = bearer (anyone holding the number may redeem it).</summary>
    public Guid?     CustomerUuid     { get; set; }
    public string?   CustomerName     { get; set; }
    public int       IssuedStoreId    { get; set; }
    public int?      IssuedShiftId    { get; set; }
    public int?      ReturnOrderId    { get; set; }
    public decimal   OriginalAmount   { get; set; }
    public decimal   RemainingBalance { get; set; }
    public string    Status           { get; set; } = PosStoreCreditStatus.Active;
    public int       IssuedBy         { get; set; }
    public DateTime  IssuedAt         { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt        { get; set; }
    public DateTime? LastUsedAt       { get; set; }
    public int?      LastUsedStoreId  { get; set; }
    public string?   Note             { get; set; }
    public int?      CancelledBy      { get; set; }
    public DateTime? CancelledAt      { get; set; }
    public DateTime  ModifiedAt       { get; set; } = DateTime.UtcNow;
    /// <summary>Concurrency: two tills redeeming the same note — the second save fails (no double spend).</summary>
    public byte[]    RowVersion       { get; set; } = [];

    public PosOrder? ReturnOrder { get; set; }
    public List<PosStoreCreditTransaction> Transactions { get; set; } = [];

    public bool IsExpiredAt(DateTime utc) => ExpiresAt is { } e && e <= utc;
}

/// <summary>A39 M039.11 — one row per balance change. Amount signed (+issue/adjust up, −redeem/expire/cancel).</summary>
internal class PosStoreCreditTransaction : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public int      CreditNoteId   { get; set; }
    public string   Type           { get; set; } = PosStoreCreditTxnType.Issue;
    public decimal  Amount         { get; set; }
    public decimal  BalanceAfter   { get; set; }
    public int?     OrderId        { get; set; }
    public int      StoreId        { get; set; }
    public int      PerformedBy    { get; set; }
    public DateTime PerformedAt    { get; set; } = DateTime.UtcNow;
    public string?  Note           { get; set; }

    public PosStoreCreditNote CreditNote { get; set; } = null!;
    public PosOrder? Order { get; set; }
}

/// <summary>
/// A39 D-8 — a one-time manager authorization for one return: only the SHA-256 of the token is stored; 5 minutes; bound to
/// the original order, the terminal and an amount ceiling. <see cref="UsedAt"/> is a concurrency token, so a token can be
/// consumed once even by two concurrent returns.
/// </summary>
internal class PosReturnAuthorization : ITenantScopedEntity
{
    public int       Id              { get; set; }
    public Guid      OrganizationId  { get; set; }
    public string    TokenHash       { get; set; } = string.Empty;
    public int       TerminalId      { get; set; }
    public int       OriginalOrderId { get; set; }
    public int       ManagerUserId   { get; set; }
    public int       RequestedBy     { get; set; }
    public decimal   AmountCeiling   { get; set; }
    /// <summary>The manager holds POS_RETURN_OVERRIDE (any reason) / POS_RETURN_CROSS_STORE (cross-store only).</summary>
    public bool      CanOverride     { get; set; }
    public bool      CanCrossStore   { get; set; }
    public DateTime  CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime  ExpiresAt       { get; set; }
    public DateTime? UsedAt          { get; set; }
    public int?      UsedByOrderId   { get; set; }
}
