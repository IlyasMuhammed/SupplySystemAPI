namespace SMS.Modules.Logistics.Models;

public class ApproveCarrierInvoiceRequest
{
    /// <summary>
    /// What the company accepts it owes. Defaults to what was billed; anything less needs a note,
    /// and anything more is refused — approving above a bill is invention, not approval.
    /// </summary>
    public decimal? Amount { get; set; }

    public string? Note { get; set; }
}

public class RaiseDisputeRequest
{
    /// <summary>Required. A dispute nobody can restate is a dispute nobody wins.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The carrier's own case or ticket number, so the query can be chased.</summary>
    public string? Reference { get; set; }

    /// <summary>What we expect the carrier to credit. An expectation, not an entitlement.</summary>
    public decimal? ExpectedCreditAmount { get; set; }
}

public class ResolveDisputeRequest
{
    /// <summary>CREDIT_RECEIVED, ACCEPTED_AS_BILLED or WRITTEN_OFF.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// What is now accepted as owed. Required when the carrier credited part of the bill; the other
    /// two outcomes approve it at what was billed.
    /// </summary>
    public decimal? ApprovedAmount { get; set; }

    public string? Note { get; set; }
}

/// <summary>Where a bill stands, and what the company has accepted it owes.</summary>
public class CarrierInvoiceSettlementModel
{
    public Guid   InvoiceUuid   { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string CarrierName   { get; set; } = string.Empty;
    public string Currency      { get; set; } = string.Empty;

    /// <summary>RECEIVED, MATCHED, DISPUTED, APPROVED or CANCELLED.</summary>
    public string Status { get; set; } = string.Empty;

    public decimal  BilledAmount   { get; set; }
    public decimal? ApprovedAmount { get; set; }

    /// <summary>Billed minus approved. What the carrier will not be paid, and why.</summary>
    public decimal? NotAccepted { get; set; }

    public DateTime? ApprovedAt   { get; set; }
    public string?   ApprovalNote { get; set; }

    public DateTime? DisputeRaisedAt      { get; set; }
    public string?   DisputeReason        { get; set; }
    public string?   DisputeReference     { get; set; }
    public decimal?  ExpectedCreditAmount { get; set; }
    public DateTime? DisputeResolvedAt    { get; set; }
    public string?   DisputeOutcome       { get; set; }
    public string?   DisputeResolutionNote { get; set; }

    /// <summary>How many accruals this approval closed. Zero until it is approved.</summary>
    public int AccrualsClosed { get; set; }

    // ── Where it went in Finance (decision G10) ───────────────────────────────

    /// <summary>The payable this bill became. Null until it is approved.</summary>
    public Guid?     PostedInvoiceUuid   { get; set; }
    public string?   PostedInvoiceNumber { get; set; }
    public DateTime? PostedAt            { get; set; }

    public List<string> Warnings { get; set; } = [];
}
