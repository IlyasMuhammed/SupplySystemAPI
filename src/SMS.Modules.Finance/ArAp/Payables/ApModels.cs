namespace SMS.Modules.Finance.ArAp.Payables;

// A48 AP — request and response shapes of API-CONTRACT.md §6–§9 (camelCase on the wire, enums as strings).

// ── §6 Matching ─────────────────────────────────────────────────────────────

public sealed class RunMatchRequest
{
    /// <summary>ThreeWay | TwoWay; null = three-way when the invoice names a GRN (or the PO has receipts), else two-way.</summary>
    public string? MatchType { get; set; }
}

public sealed class OverrideMatchRequest
{
    public string? Reason { get; set; }
}

public sealed record ToleranceRuleRef(Guid Uuid, string Name);

public sealed record MatchLineModel(
    int LineNo, string ItemDescription, Guid? PoLineUuid, Guid? GrnLineUuid,
    decimal? PoQuantity, decimal? PoUnitPrice, decimal? PoLineTotal, decimal? GrnQuantity,
    decimal InvoiceQuantity, decimal InvoiceUnitPrice, decimal InvoiceLineTotal,
    decimal? QuantityVariance, decimal? PriceVariance, decimal? PriceVariancePercent, decimal? TotalVariance,
    string LineStatus);

public sealed record MatchDetailModel(
    Guid InvoiceUuid, string InvoiceNumber, string? SupplierInvoiceNo, Guid VendorUuid, string VendorName,
    Guid? PoUuid, string? PoNumber, Guid? GrnUuid, string? GrnNumber,
    string MatchStatus, string? MatchType, string? LineMatchResult, bool? WithinTolerance,
    decimal? VariancePercent, decimal VarianceAmount, ToleranceRuleRef? ToleranceRule, bool AutoApproved,
    DateTime? MatchedAt, string? MatchedByName, string? OverrideReason, DateTime? OverriddenAt, string? OverriddenByName,
    string CurrencyCode, decimal Subtotal, decimal TotalAmount,
    IReadOnlyList<MatchLineModel> Lines)
{
    /// <summary>Set when an auto-approval (BR-MT-005) was attempted and refused — the match itself stands.</summary>
    public string? AutoApproveMessage { get; init; }
}

public sealed record MatchSummaryModel(
    Guid InvoiceUuid, string InvoiceNumber, string? SupplierInvoiceNo, Guid VendorUuid, string VendorName,
    Guid? PoUuid, string? PoNumber, string MatchStatus, string? MatchType, string? LineMatchResult,
    bool? WithinTolerance, decimal? VariancePercent, decimal VarianceAmount, bool Overridden,
    string CurrencyCode, decimal TotalAmount, DateOnly InvoiceDate, DateTime? MatchedAt);

public sealed class ToleranceRuleRequest
{
    public string?  Name                     { get; set; }
    public Guid?    VendorUuid               { get; set; }
    public decimal? PriceTolerancePercent    { get; set; }
    public decimal? PriceToleranceAmount     { get; set; }
    public decimal? QuantityTolerancePercent { get; set; }
    public decimal? TotalTolerancePercent    { get; set; }
    public decimal? TotalToleranceAmount     { get; set; }
    public bool?    AutoApproveWithin        { get; set; }
    public bool?    IsDefault                { get; set; }
    public bool?    IsActive                 { get; set; }
}

public sealed record ToleranceRuleModel(
    Guid Uuid, string Name, Guid? VendorUuid, string? VendorName,
    decimal PriceTolerancePercent, decimal PriceToleranceAmount, decimal QuantityTolerancePercent,
    decimal TotalTolerancePercent, decimal TotalToleranceAmount, bool AutoApproveWithin, bool IsDefault, bool IsActive);

// ── §7 Schedules and early-payment terms ────────────────────────────────────

public sealed record PaymentScheduleModel(
    Guid Uuid, Guid InvoiceUuid, string InvoiceNumber, string? SupplierInvoiceNo, Guid VendorUuid, string VendorName,
    string CurrencyCode, DateOnly InvoiceDate, DateOnly DueDate, int DaysUntilDue,
    decimal OriginalAmount, decimal AmountRemaining, string PaymentPriority, string? PaymentMethod,
    bool PaymentBlock, string? BlockReason, string? BlockedByName, DateTime? BlockedAt,
    Guid? EarlyPaymentTermUuid, string? EarlyPaymentTermCode, DateOnly? DiscountDate, decimal? DiscountPercent, decimal? DiscountAmount,
    Guid? ReservedByRunUuid, string? ReservedByRunNumber, Guid? PaidInRunUuid, string? PaidInRunNumber,
    string Status, string RowVersion);

public sealed class BlockScheduleRequest
{
    public string? Reason { get; set; }
}

public sealed class UpdateScheduleRequest
{
    public string? PaymentPriority { get; set; }
    public string? PaymentMethod   { get; set; }
    public string? RowVersion      { get; set; }
}

public sealed class SetScheduleTermRequest
{
    /// <summary>Null (or Guid.Empty) clears the term.</summary>
    public Guid? EarlyPaymentTermUuid { get; set; }
}

public sealed record CashFlowWeekModel(
    int Week, DateOnly WeekStart, DateOnly WeekEnd, int ItemCount, decimal DueAmount, decimal BlockedAmount,
    decimal DiscountAvailable, decimal NetIfDiscountsTaken);

/// <summary>C10 cash-flow forecast (tasks 358, 377–380): base-currency amounts by Monday-to-Sunday week.</summary>
public sealed record CashFlowForecastModel(
    DateOnly From, int Weeks, string? BaseCurrencyCode, decimal OverdueAmount, int OverdueCount, decimal OverdueBlockedAmount,
    IReadOnlyList<CashFlowWeekModel> WeekRows, decimal LaterAmount, decimal TotalDue, decimal TotalBlocked, decimal TotalDiscountAvailable);

public sealed class EarlyPaymentTermRequest
{
    public string?  Code            { get; set; }
    public string?  Name            { get; set; }
    public decimal? DiscountPercent { get; set; }
    public int?     DiscountDays    { get; set; }
    public int?     NetDays         { get; set; }
    public bool?    IsActive        { get; set; }
}

public sealed record EarlyPaymentTermModel(Guid Uuid, string Code, string Name, decimal DiscountPercent, int DiscountDays, int NetDays, bool IsActive);

// ── §8 Payment runs ─────────────────────────────────────────────────────────

public sealed class ProposePaymentRunRequest
{
    public DateOnly? PaymentDate          { get; set; }
    public DateOnly? DueDateCutoff        { get; set; }
    public string?   PaymentMethod        { get; set; }
    public Guid?     VendorUuid           { get; set; }
    public string?   CurrencyCode         { get; set; }
    public string?   BankAccount          { get; set; }
    public bool      IncludeDiscountItems { get; set; } = true;
    /// <summary>High = High only, Normal = High + Normal, Low (or null) = all.</summary>
    public string?   MinPriority          { get; set; }
}

public sealed class RemoveRunItemRequest
{
    public string? Reason { get; set; }
}

public sealed class UpdateRunItemRequest
{
    public string? PaymentMethod { get; set; }
}

public sealed class CancelRunRequest
{
    public string? Reason { get; set; }
}

public sealed record PaymentRunItemModel(
    Guid ItemUuid, Guid ScheduleUuid, Guid InvoiceUuid, string InvoiceNumber, string CurrencyCode, DateOnly DueDate,
    decimal OriginalAmount, decimal DiscountAmount, decimal NetPaymentAmount, int DaysUntilDue, bool HasDiscount,
    DateOnly? DiscountDate, string? PaymentMethod, string Status, string? RemovedReason);

public sealed record PaymentRunBatchModel(
    Guid BatchUuid, Guid VendorUuid, string VendorName, string CurrencyCode, string PaymentMethod, string? BankDetailsMasked,
    int ItemCount, decimal TotalAmount, decimal TotalDiscount, decimal NetAmount,
    Guid? SupplierPaymentUuid, string? SupplierPaymentNumber, string Status, string? ErrorMessage,
    IReadOnlyList<PaymentRunItemModel> Items);

public sealed record BlockedScheduleModel(
    Guid ScheduleUuid, Guid InvoiceUuid, string InvoiceNumber, Guid VendorUuid, string VendorName, string CurrencyCode,
    DateOnly DueDate, decimal AmountRemaining, string? BlockReason);

public sealed record PaymentRunSummaryModel(
    Guid Uuid, string RunNumber, DateOnly RunDate, DateOnly PaymentDate, DateOnly DueDateCutoff, string Status,
    string? PaymentMethod, string? BaseCurrencyCode, int TotalVendors, int TotalItems,
    decimal TotalAmount, decimal TotalDiscount, decimal NetPaymentAmount, int FailedBatchCount,
    string? ProposedByName, DateTime? ProposedAt, string? ApprovedByName, DateTime? ApprovedAt, DateTime? CompletedAt);

public sealed record PaymentRunDetailModel(
    PaymentRunSummaryModel Run, string? BankAccount, bool IncludeDiscount, string? SelectionCriteria,
    string? ExecutedByName, DateTime? ExecutedAt, string? CancelledByName, DateTime? CancelledAt, string? CancelReason,
    string? PaymentFileName, DateTime? ExportedAt, string RowVersion,
    IReadOnlyList<PaymentRunBatchModel> VendorGroups, IReadOnlyList<PaymentRunItemModel> RemovedItems,
    IReadOnlyList<BlockedScheduleModel> BlockedItems)
{
    /// <summary>D-AP-12 — e.g. the discount account the GL needs is not assigned (approval is then refused).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PaymentFile(string FileName, string ContentType, byte[] Content);

// ── §9 Debit memos ──────────────────────────────────────────────────────────

public sealed record SupplierNoteApplicationModel(
    Guid Uuid, Guid NoteUuid, string NoteNumber, Guid InvoiceUuid, string InvoiceNumber, decimal Amount,
    DateTime AppliedAt, string? AppliedByName);
