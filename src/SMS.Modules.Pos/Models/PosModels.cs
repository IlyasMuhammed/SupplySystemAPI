using System.Security.Claims;
using SMS.Shared.Authorization;

namespace SMS.Modules.Pos.Models;

// A38 API-CONTRACT §2–§4 (POS-A). camelCase on the wire; money 2dp in the org sale base.

/// <summary>A38 D-13 — POS permission codes (seeded by XMOD, API-CONTRACT §9).</summary>
public static class PosPermissions
{
    public const string StoreView = "POS_STORE_VIEW", StoreManage = "POS_STORE_MANAGE";
    public const string TerminalView = "POS_TERMINAL_VIEW", TerminalManage = "POS_TERMINAL_MANAGE";
    public const string ShiftOpen = "POS_SHIFT_OPEN", ShiftClose = "POS_SHIFT_CLOSE", ShiftReconcile = "POS_SHIFT_RECONCILE", ShiftView = "POS_SHIFT_VIEW";
    public const string OrderCreate = "POS_ORDER_CREATE", OrderView = "POS_ORDER_VIEW", OrderVoid = "POS_ORDER_VOID", OrderReturn = "POS_ORDER_RETURN",
        OrderDiscount = "POS_ORDER_DISCOUNT";
    public const string CashMovement = "POS_CASH_MOVEMENT", ProductSettings = "POS_PRODUCT_SETTINGS", CustomerSettings = "POS_CUSTOMER_SETTINGS",
        ReportsView = "POS_REPORTS_VIEW";
    // A39 D-9 — aliases of the shared PermissionCodes (XFIN seeds them).
    public const string ReturnCrossStore = PermissionCodes.POS_RETURN_CROSS_STORE, ReturnOverride = PermissionCodes.POS_RETURN_OVERRIDE,
        ReturnPolicyManage = PermissionCodes.POS_RETURN_POLICY_MANAGE, ReturnReasonManage = PermissionCodes.POS_RETURN_REASON_MANAGE,
        ReturnReports = PermissionCodes.POS_RETURN_REPORTS;
    public const string CreditNoteIssue = PermissionCodes.POS_CREDIT_NOTE_ISSUE, CreditNoteRedeem = PermissionCodes.POS_CREDIT_NOTE_REDEEM,
        CreditNoteCancel = PermissionCodes.POS_CREDIT_NOTE_CANCEL, CreditNoteView = PermissionCodes.POS_CREDIT_NOTE_VIEW,
        CreditNoteReports = PermissionCodes.POS_CREDIT_NOTE_REPORTS;

    public static readonly IReadOnlyList<string> All =
    [
        StoreView, StoreManage, TerminalView, TerminalManage, ShiftOpen, ShiftClose, ShiftReconcile, ShiftView, OrderCreate, OrderView,
        OrderVoid, OrderReturn, OrderDiscount, CashMovement, ProductSettings, CustomerSettings, ReportsView,
        ReturnCrossStore, ReturnOverride, ReturnPolicyManage, ReturnReasonManage, ReturnReports,
        CreditNoteIssue, CreditNoteRedeem, CreditNoteCancel, CreditNoteView, CreditNoteReports
    ];
}

/// <summary>Who is calling: the signed-in user and their permission claims (the organization is the ambient tenant).</summary>
public sealed record PosCaller(int UserId, IReadOnlySet<string> Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission);
    public bool HasAny(params string[] permissions) => permissions.Any(Permissions.Contains);
    public static PosCaller From(ClaimsPrincipal user) => new(user.GetUserId(), user.GetPermissions().ToHashSet(StringComparer.OrdinalIgnoreCase));
}

// ── §2 Stores, terminals, staff ──────────────────────────────────────────────

public class PosStoreModel
{
    public Guid      Uuid                      { get; set; }
    public string    Code                      { get; set; } = string.Empty;
    public string    Name                      { get; set; } = string.Empty;
    public Guid      WarehouseUuid             { get; set; }
    public string    WarehouseName             { get; set; } = string.Empty;
    public string?   AddressLine1              { get; set; }
    public string?   AddressLine2              { get; set; }
    public string?   City                      { get; set; }
    public string?   StateProvince             { get; set; }
    public string?   PostalCode                { get; set; }
    public string?   Country                   { get; set; }
    public string?   Phone                     { get; set; }
    public string?   Email                     { get; set; }
    public string    Timezone                  { get; set; } = "UTC";
    public string?   CurrencyCode              { get; set; }
    public bool      TaxInclusivePricing       { get; set; }
    public Guid?     DefaultTaxCodeUuid        { get; set; }
    public string?   DefaultTaxCode            { get; set; }
    public decimal   DefaultTaxPercent         { get; set; }
    public string?   ReceiptHeader             { get; set; }
    public string?   ReceiptFooter             { get; set; }
    public decimal   OfflineCreditTolerancePct { get; set; }
    public int       AutoCloseShiftHours       { get; set; }
    public decimal   VarianceThreshold         { get; set; }
    public bool      IsActive                  { get; set; }
    public int       TerminalCount             { get; set; }
    public int       ActiveShiftCount          { get; set; }
    public DateTime  CreatedAt                 { get; set; }
    public DateTime  ModifiedAt                { get; set; }
    public byte[]?   RowVersion                { get; set; }
}

public class PosStoreUpsertRequest
{
    public string?  Code                      { get; set; }
    public string?  Name                      { get; set; }
    public Guid     WarehouseUuid             { get; set; }
    public string?  AddressLine1              { get; set; }
    public string?  AddressLine2              { get; set; }
    public string?  City                      { get; set; }
    public string?  StateProvince             { get; set; }
    public string?  PostalCode                { get; set; }
    public string?  Country                   { get; set; }
    public string?  Phone                     { get; set; }
    public string?  Email                     { get; set; }
    public string?  Timezone                  { get; set; }
    public bool     TaxInclusivePricing       { get; set; }
    public Guid?    DefaultTaxCodeUuid        { get; set; }
    public string?  ReceiptHeader             { get; set; }
    public string?  ReceiptFooter             { get; set; }
    public decimal? OfflineCreditTolerancePct { get; set; }
    public int?     AutoCloseShiftHours       { get; set; }
    public decimal? VarianceThreshold         { get; set; }
    /// <summary>Create: defaults to true. Update: null = unchanged; a change runs the PATCH status rules.</summary>
    public bool?    IsActive                  { get; set; }
    /// <summary>Update only; null skips the check.</summary>
    public byte[]?  RowVersion                { get; set; }
}

public class PosStoreListFilter
{
    public string? Search   { get; set; }
    public bool?   IsActive { get; set; }
    public int     Page     { get; set; } = 1;
    public int     PageSize { get; set; } = 20;
}

public class PosStatusRequest { public bool IsActive { get; set; } }

public class PosTerminalModel
{
    public Guid      Uuid              { get; set; }
    public Guid      StoreUuid         { get; set; }
    public string    StoreName         { get; set; } = string.Empty;
    public string    Code              { get; set; } = string.Empty;
    public string    Name              { get; set; } = string.Empty;
    public bool      DeviceBound       { get; set; }
    public Guid?     DeviceUuid        { get; set; }
    public DateTime? LastSeenAt        { get; set; }
    public DateTime? LastSyncAt        { get; set; }
    public bool      IsOnline          { get; set; }
    public bool      IsActive          { get; set; }
    public Guid?     ActiveShiftUuid   { get; set; }
    public string?   ActiveShiftNumber { get; set; }
    public DateTime? PinLockedUntil    { get; set; }
    public byte[]?   RowVersion        { get; set; }
}

public class PosTerminalCreateRequest
{
    public string? Code { get; set; }
    public string? Name { get; set; }
}

public class PosTerminalUpdateRequest
{
    public string? Code       { get; set; }
    public string? Name       { get; set; }
    public bool    IsActive   { get; set; } = true;
    public byte[]? RowVersion { get; set; }
}

public class PosBindRequest { public Guid DeviceUuid { get; set; } }

public class PosTerminalConfigModel
{
    public PosTerminalModel Terminal   { get; set; } = new();
    public PosStoreModel    Store      { get; set; } = new();
    public DateTime         ServerTime { get; set; }
}

public class PosTerminalStatusModel
{
    public bool      IsOnline        { get; set; }
    public DateTime? LastSeenAt      { get; set; }
    public DateTime? LastSyncAt      { get; set; }
    public bool      DeviceBound     { get; set; }
    public Guid?     ActiveShiftUuid { get; set; }
    public DateTime? PinLockedUntil  { get; set; }
}

public class PosStaffMemberModel
{
    public int    UserId   { get; set; }
    public string Name     { get; set; } = string.Empty;
    public string Email    { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string PosRole  { get; set; } = string.Empty;
    public bool   HasPin   { get; set; }
    public bool   IsActive { get; set; }
}

public class PosStaffCandidateModel
{
    public int    UserId   { get; set; }
    public string Name     { get; set; } = string.Empty;
    public string Email    { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
}

public class PosStaffUpsertRequest
{
    public string? PosRole { get; set; }
    /// <summary>4–6 digits; null or empty = leave the PIN as it is.</summary>
    public string? Pin     { get; set; }
}

public class PosForceSyncRequest { public string? SyncType { get; set; } }
public class PosForceLogoutRequest { public string? Reason { get; set; } }

// ── §3 PIN sign-in & cashier sessions ────────────────────────────────────────

public class PosPinSignInRequest
{
    public Guid    TerminalUuid { get; set; }
    public int?    UserId       { get; set; }
    public string? Pin          { get; set; }
}

public class PosPinSignInModel
{
    public string            AccessToken  { get; set; } = string.Empty;
    public string            RefreshToken { get; set; } = string.Empty;
    public PosSignedInUser   User         { get; set; } = new();
}

public class PosSignedInUser
{
    public int          UserId      { get; set; }
    public string       Name        { get; set; } = string.Empty;
    public string       PosRole     { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}

public class PosSessionStartRequest
{
    public Guid    ShiftUuid { get; set; }
    public string? LocalId   { get; set; }
}

public class PosCashierSessionModel
{
    public Guid      Uuid         { get; set; }
    public Guid      ShiftUuid    { get; set; }
    public Guid      TerminalUuid { get; set; }
    public int       UserId       { get; set; }
    public string    UserName     { get; set; } = string.Empty;
    public DateTime  StartedAt    { get; set; }
    public DateTime? EndedAt      { get; set; }
    public bool      IsActive     { get; set; }
    public decimal   CashSales    { get; set; }
    public decimal   CardSales    { get; set; }
    public decimal   MobileSales  { get; set; }
    public decimal   CreditSales  { get; set; }
    public decimal   RefundTotal  { get; set; }
    public int       OrderCount   { get; set; }
}

// ── §4 Shifts & cash movements ───────────────────────────────────────────────

public class PosShiftModel
{
    public Guid      Uuid                { get; set; }
    public string    ShiftNumber         { get; set; } = string.Empty;
    public Guid      StoreUuid           { get; set; }
    public string    StoreName           { get; set; } = string.Empty;
    public Guid      TerminalUuid        { get; set; }
    public string    TerminalName        { get; set; } = string.Empty;
    public string    Status              { get; set; } = string.Empty;
    public string    OpenedByName        { get; set; } = string.Empty;
    public DateTime  OpenedAt            { get; set; }
    public DateTime? ClosingStartedAt    { get; set; }
    public string?   ClosedByName        { get; set; }
    public DateTime? ClosedAt            { get; set; }
    public string?   ReconciledByName    { get; set; }
    public DateTime? ReconciledAt        { get; set; }
    public decimal   OpeningFloat        { get; set; }
    public decimal   TotalCashSales      { get; set; }
    public decimal   TotalCardSales      { get; set; }
    public decimal   TotalMobileSales    { get; set; }
    public decimal   TotalOtherSales     { get; set; }
    public decimal   TotalCreditSales    { get; set; }
    public decimal   TotalCashRefunds    { get; set; }
    public decimal   TotalOtherRefunds   { get; set; }
    public decimal   TotalCashIn         { get; set; }
    public decimal   TotalCashOut        { get; set; }
    public decimal   TotalStoreCreditIssued   { get; set; }
    public decimal   TotalStoreCreditRedeemed { get; set; }
    public decimal   ExpectedCashClosing { get; set; }
    public decimal?  ActualCashClosing   { get; set; }
    public decimal?  CashVariance        { get; set; }
    public decimal   VarianceThreshold   { get; set; }
    public string?   VarianceNote        { get; set; }
    public string?   ReconcileNotes      { get; set; }
    public int       OrderCount          { get; set; }
    public int       RefundCount         { get; set; }
    public decimal   GrossTotal          { get; set; }
    public decimal   NetTotal            { get; set; }
    public byte[]?   RowVersion          { get; set; }
}

public class PosShiftDetailModel : PosShiftModel
{
    public List<PosCashierSessionModel> Sessions      { get; set; } = [];
    public List<PosCashMovementModel>   CashMovements { get; set; } = [];
}

public class PosShiftSummaryModel
{
    public PosShiftModel                Shift     { get; set; } = new();
    public List<PosMethodTotalModel>    ByMethod  { get; set; } = [];
    public List<PosCashierSessionModel> ByCashier { get; set; } = [];
}

public class PosMethodTotalModel
{
    public string  Method  { get; set; } = string.Empty;
    public decimal Sales   { get; set; }
    public decimal Refunds { get; set; }
    public decimal Net     { get; set; }
}

public class PosCashMovementModel
{
    public Guid     Uuid            { get; set; }
    public string   Direction       { get; set; } = string.Empty;
    public decimal  Amount          { get; set; }
    public string   Reason          { get; set; } = string.Empty;
    public string?  Note            { get; set; }
    public string   PerformedByName { get; set; } = string.Empty;
    public DateTime PerformedAt     { get; set; }
    public string?  LocalId         { get; set; }
}

public class PosOpenShiftRequest
{
    public decimal  OpeningFloat      { get; set; }
    public decimal? VarianceThreshold { get; set; }
}

public class PosCloseShiftRequest
{
    public decimal? ActualCashClosing { get; set; }
    public string?  VarianceNote      { get; set; }
}

public class PosNoteRequest { public string? Note { get; set; } }

public class PosCashMovementRequest
{
    public string? Direction { get; set; }
    public decimal Amount    { get; set; }
    public string? Reason    { get; set; }
    public string? Note      { get; set; }
    public string? LocalId   { get; set; }
}

public class PosShiftListFilter
{
    public Guid?     StoreUuid    { get; set; }
    public Guid?     TerminalUuid { get; set; }
    public string?   Status       { get; set; }
    public DateTime? From         { get; set; }
    public DateTime? To           { get; set; }
    public int       Page         { get; set; } = 1;
    public int       PageSize     { get; set; } = 20;
}
