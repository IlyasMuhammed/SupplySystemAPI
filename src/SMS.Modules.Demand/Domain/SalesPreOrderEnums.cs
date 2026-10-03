namespace SMS.Modules.Demand.Domain;

// A32 (Sales Pre-Order Pipeline) — every persisted-as-string status/type of the inquiry, quotation and
// sale order extensions, on the same EnumCode<T>/[Code] pattern as SaleOrderEnums.cs. The API contract
// (docs/sales-preorder/API-CONTRACT.md) lists the same codes; keep the two in step.

/// <summary>§3.4. RECEIVED → UNDER_REVIEW → REVIEW_COMPLETE → QUOTED; UNDER_REVIEW/REVIEW_COMPLETE → DECLINED.</summary>
internal enum SaleInquiryStatus
{
    [Code("RECEIVED")]        Received,
    [Code("UNDER_REVIEW")]    UnderReview,
    [Code("REVIEW_COMPLETE")] ReviewComplete,
    [Code("QUOTED")]          Quoted,
    [Code("DECLINED")]        Declined
}

/// <summary>§3.5 — a line's evaluation.</summary>
internal enum SaleInquiryLineStatus
{
    [Code("PENDING")]       Pending,
    [Code("CAN_SUPPLY")]    CanSupply,
    [Code("PARTIAL")]       Partial,
    [Code("CANNOT_SUPPLY")] CannotSupply,
    [Code("UNDER_REVIEW")]  UnderReview
}

/// <summary>§4.4. DRAFT → SENT → ACCEPTED → CONVERTED; SENT → REJECTED | EXPIRED.</summary>
internal enum SaleQuotationStatus
{
    [Code("DRAFT")]     Draft,
    [Code("SENT")]      Sent,
    [Code("ACCEPTED")]  Accepted,
    [Code("REJECTED")]  Rejected,
    [Code("EXPIRED")]   Expired,
    [Code("CONVERTED")] Converted
}

/// <summary>§4.5.</summary>
internal enum SaleQuotationLineType
{
    [Code("NORMAL")]      Normal,
    [Code("ALTERNATIVE")] Alternative,
    [Code("REJECTED")]    Rejected
}

/// <summary>§4.5 — the customer's answer per line.</summary>
internal enum SaleQuotationCustomerResponse
{
    [Code("PENDING")]  Pending,
    [Code("ACCEPTED")] Accepted,
    [Code("REJECTED")] Rejected,
    [Code("COUNTER")]  Counter
}

/// <summary>§5.4 — how a sale order came to exist. PORTAL and INTER_TENANT are reserved (Addendum 33).</summary>
internal enum SaleOrderSourceType
{
    [Code("MANUAL")]         Manual,
    [Code("FROM_QUOTATION")] FromQuotation,
    [Code("PORTAL")]         Portal,
    [Code("INTER_TENANT")]   InterTenant
}

/// <summary>§6.3 — computed per line at read time, never stored (BR-C4-07).</summary>
internal enum SaleOrderDeliveryIndicator
{
    [Code("GREEN")]  Green,
    [Code("BLUE")]   Blue,
    [Code("YELLOW")] Yellow,
    [Code("RED")]    Red,
    [Code("GREY")]   Grey
}

/// <summary>What one reserve/release call on a sale order line did (API-CONTRACT §5).</summary>
internal enum SaleOrderReservationOutcome
{
    /// <summary>Everything asked for is now held.</summary>
    [Code("RESERVED")]           Reserved,
    /// <summary>allowPartial was set and only part of it could be held; that part is held.</summary>
    [Code("PARTIAL")]            Partial,
    /// <summary>Not enough is free and allowPartial was not set: nothing was held. Ask the user, then retry with allowPartial.</summary>
    [Code("NEEDS_CONFIRMATION")] NeedsConfirmation,
    /// <summary>Nothing of this variant is free in any single warehouse: nothing was held.</summary>
    [Code("NONE_AVAILABLE")]     NoneAvailable,
    /// <summary>A release freed stock.</summary>
    [Code("RELEASED")]           Released,
    /// <summary>reserve-all only: the line had nothing left to reserve, or cannot be reserved (drop ship, cancelled…).</summary>
    [Code("SKIPPED")]            Skipped
}
