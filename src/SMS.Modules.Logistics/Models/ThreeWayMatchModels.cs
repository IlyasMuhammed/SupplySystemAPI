namespace SMS.Modules.Logistics.Models;

/// <summary>One movement, and the three figures for it side by side.</summary>
public class ConsignmentMatchModel
{
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;
    public string? MasterAwb        { get; set; }

    /// <summary>What we were quoted (T-47).</summary>
    public decimal? QuotedAmount { get; set; }

    /// <summary>What the carrier agreed when it accepted the booking (T-52). Often absent.</summary>
    public decimal? BookedAmount { get; set; }

    /// <summary>What it has billed — the sum of the lines matched to this movement.</summary>
    public decimal InvoicedAmount { get; set; }

    /// <summary>
    /// What the bill is measured against: the booking figure where the carrier gave one, the quote
    /// otherwise. Stated, so nobody has to work out which was used.
    /// </summary>
    public decimal? ExpectedAmount { get; set; }
    public string?  ExpectedBasis  { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Billed minus expected. Positive means overcharged.</summary>
    public decimal? VarianceAmount  { get; set; }
    public decimal? VariancePercent { get; set; }

    /// <summary>WITHIN_TOLERANCE, OVERCHARGED, UNDERCHARGED or NOT_ACCRUED.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>WEIGHT, SURCHARGE, SERVICE or UNEXPLAINED. Null within tolerance.</summary>
    public string? VarianceReason { get; set; }

    /// <summary>The evidence behind the reason — which weights, which surcharge codes.</summary>
    public string? VarianceNote { get; set; }

    /// <summary>The lines on this bill that charge this movement.</summary>
    public List<InvoiceLineMatchModel> Lines { get; set; } = [];
}

public class ThreeWayMatchModel
{
    public Guid   InvoiceUuid   { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string CarrierName   { get; set; } = string.Empty;
    public string Currency      { get; set; } = string.Empty;

    /// <summary>RECEIVED, MATCHED or DISPUTED — where the bill stands after the comparison.</summary>
    public string Status { get; set; } = string.Empty;

    public decimal InvoiceTotal { get; set; }

    /// <summary>The sum of what every matched movement was expected to cost.</summary>
    public decimal ExpectedTotal { get; set; }

    /// <summary>Billed minus expected, across the whole bill.</summary>
    public decimal VarianceTotal { get; set; }

    public int ConsignmentCount { get; set; }
    public int WithinTolerance  { get; set; }
    public int Overcharged      { get; set; }
    public int Undercharged     { get; set; }
    public int NotAccrued       { get; set; }

    /// <summary>The amount on lines nobody could tie to a movement. Not in the comparison at all.</summary>
    public decimal UnmatchedAmount { get; set; }
    public int     UnmatchedLines  { get; set; }

    /// <summary>The tolerance the comparison was made with, so a verdict can be reproduced.</summary>
    public decimal TolerancePercent { get; set; }
    public decimal ToleranceAmount  { get; set; }

    /// <summary>True when the whole bill agrees — nothing out of tolerance and nothing unmatched.</summary>
    public bool IsClean { get; set; }

    public List<ConsignmentMatchModel> Consignments { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}
