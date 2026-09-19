namespace SMS.Modules.Logistics.Models;

public class CarrierInvoiceLineRequest
{
    public string  Description { get; set; } = string.Empty;

    /// <summary>The carrier's airway bill. What matching hangs off (T-54).</summary>
    public string? AwbNumber { get; set; }

    /// <summary>Our consignment number, where the carrier echoed it back.</summary>
    public string? ConsignmentReference { get; set; }

    public string? ChargeCode  { get; set; }
    public string? ServiceCode { get; set; }

    /// <summary>The weight the carrier says it billed on, where the bill states it.</summary>
    public decimal? ChargeableWeightKg { get; set; }

    public DateTime? ShipDate { get; set; }

    /// <summary>Negative for a credit line. Zero is refused.</summary>
    public decimal Amount { get; set; }
}

public class CreateCarrierInvoiceRequest
{
    public Guid   CarrierUuid   { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;

    public DateTime  InvoiceDate { get; set; }
    public DateTime? DueDate     { get; set; }

    public string  Currency    { get; set; } = string.Empty;

    /// <summary>What the carrier says the bill comes to. Must equal the sum of the lines.</summary>
    public decimal  TotalAmount { get; set; }
    public decimal? TaxAmount   { get; set; }

    public string? Notes { get; set; }

    public List<CarrierInvoiceLineRequest> Lines { get; set; } = [];
}

public class PatchCarrierInvoiceRequest
{
    public DateTime? InvoiceDate { get; set; }
    public DateTime? DueDate     { get; set; }
    public decimal?  TotalAmount { get; set; }
    public decimal?  TaxAmount   { get; set; }
    public string?   Notes       { get; set; }

    /// <summary>
    /// When given, <b>replaces</b> every line. A bill is corrected as a whole — patching one line
    /// would let the lines and the header disagree for as long as it took to fix the other.
    /// </summary>
    public List<CarrierInvoiceLineRequest>? Lines { get; set; }
}

public class CancelCarrierInvoiceRequest
{
    /// <summary>Required. A bill that disappears without a reason cannot be explained later.</summary>
    public string Reason { get; set; } = string.Empty;
}

// ── Read models ───────────────────────────────────────────────────────────────

public class CarrierInvoiceLineModel
{
    public Guid    UUID        { get; set; }
    public int     LineNo      { get; set; }
    public string  Description { get; set; } = string.Empty;
    public string? AwbNumber   { get; set; }
    public string? ConsignmentReference { get; set; }
    public string? ChargeCode  { get; set; }
    public string? ServiceCode { get; set; }
    public decimal? ChargeableWeightKg { get; set; }
    public DateTime? ShipDate  { get; set; }
    public decimal Amount      { get; set; }
}

public class CarrierInvoiceModel
{
    public Guid   UUID        { get; set; }
    public Guid   CarrierUuid { get; set; }
    public string CarrierName { get; set; } = string.Empty;

    public string   InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate   { get; set; }
    public DateTime? DueDate      { get; set; }

    public string   Currency    { get; set; } = string.Empty;
    public decimal  TotalAmount { get; set; }
    public decimal? TaxAmount   { get; set; }

    /// <summary>The sum of the lines. Equal to the total on any saved bill, by construction.</summary>
    public decimal LineTotal { get; set; }

    /// <summary>RECEIVED or CANCELLED.</summary>
    public string  Status { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Notes  { get; set; }
    public string? CancelReason { get; set; }

    public int      LineCount   { get; set; }
    public DateTime CreatedDate { get; set; }

    public List<CarrierInvoiceLineModel> Lines { get; set; } = [];
}

public class CarrierInvoiceFilter
{
    public Guid?    CarrierUuid { get; set; }
    public string?  Status      { get; set; }
    public string?  Search      { get; set; }
    public DateTime? From       { get; set; }
    public DateTime? To         { get; set; }
    public int      Page        { get; set; } = 1;
    public int      PageSize    { get; set; } = 20;
}

// ── Bringing bills in in bulk ─────────────────────────────────────────────────

public class ImportCarrierInvoicesRequest
{
    public List<CreateCarrierInvoiceRequest> Invoices { get; set; } = [];
}

/// <summary>What became of one bill in an import.</summary>
public class CarrierInvoiceImportResultModel
{
    public string  InvoiceNumber { get; set; } = string.Empty;
    public bool    Accepted      { get; set; }
    public Guid?   UUID          { get; set; }

    /// <summary>Why it was refused. Null when it was accepted.</summary>
    public string? Reason { get; set; }
}

/// <remarks>
/// One bad bill never fails the batch. An import that stops at the first problem leaves somebody
/// re-running it and re-importing everything before it, which is how duplicates get created.
/// </remarks>
public class CarrierInvoiceImportModel
{
    public int Accepted { get; set; }
    public int Refused  { get; set; }
    public List<CarrierInvoiceImportResultModel> Results { get; set; } = [];
}
