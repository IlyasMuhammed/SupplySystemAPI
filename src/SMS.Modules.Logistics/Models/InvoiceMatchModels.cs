namespace SMS.Modules.Logistics.Models;

/// <summary>One line, and what it has been tied to.</summary>
public class InvoiceLineMatchModel
{
    public Guid   LineUuid    { get; set; }
    public int    LineNo      { get; set; }
    public string Description { get; set; } = string.Empty;

    public Guid   InvoiceUuid   { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string CarrierName   { get; set; } = string.Empty;

    public string? AwbNumber            { get; set; }
    public string? ConsignmentReference { get; set; }
    public string? ChargeCode           { get; set; }

    public decimal Amount   { get; set; }
    public string  Currency { get; set; } = string.Empty;

    /// <summary>UNMATCHED, MATCHED, AMBIGUOUS or EXCLUDED.</summary>
    public string MatchStatus { get; set; } = string.Empty;

    /// <summary>AWB, REFERENCE or MANUAL — how strong the claim is.</summary>
    public string? MatchMethod { get; set; }

    public Guid?   MatchedConsignmentUuid   { get; set; }
    public string? MatchedConsignmentNumber { get; set; }

    public DateTime? MatchedAt { get; set; }

    /// <summary>Why it is ambiguous, why it was excluded, or why somebody matched it by hand.</summary>
    public string? MatchNote { get; set; }

    /// <summary>
    /// Set when the consignment this line points at is already charged on another bill. Not a
    /// refusal — a carrier may legitimately bill base carriage and a surcharge separately — but a
    /// double charge looks exactly the same until somebody checks.
    /// </summary>
    public string? DuplicateWarning { get; set; }
}

public class InvoiceMatchResultModel
{
    public Guid   InvoiceUuid   { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;

    public int LineCount { get; set; }
    public int Matched   { get; set; }
    public int Ambiguous { get; set; }
    public int Unmatched { get; set; }
    public int Excluded  { get; set; }

    /// <summary>True when nothing is left for a person to do.</summary>
    public bool IsComplete { get; set; }

    public List<InvoiceLineMatchModel> Lines { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

/// <summary>A consignment a line could plausibly belong to, offered when matching by hand.</summary>
public class MatchCandidateModel
{
    public Guid    ConsignmentUuid   { get; set; }
    public string  ConsignmentNumber { get; set; } = string.Empty;
    public string? MasterAwb         { get; set; }
    public string? CarrierName       { get; set; }
    public string  Status            { get; set; } = string.Empty;

    public decimal? FreightCost     { get; set; }
    public string?  FreightCurrency { get; set; }

    /// <summary>Why this one is being offered.</summary>
    public string Reason { get; set; } = string.Empty;
}

public class MatchLineRequest
{
    public Guid ConsignmentUuid { get; set; }

    /// <summary>
    /// Required. A match made by hand is a weaker claim than one made on an airway bill, and the
    /// person who has to unpick it later needs to know what this one was based on.
    /// </summary>
    public string Note { get; set; } = string.Empty;
}

public class ExcludeLineRequest
{
    /// <summary>Required. "Not a movement charge" is a judgement, and judgements need reasons.</summary>
    public string Reason { get; set; } = string.Empty;
}

public class UnmatchedLineFilter
{
    public Guid?   CarrierUuid { get; set; }
    public Guid?   InvoiceUuid { get; set; }
    /// <summary>Defaults to everything still needing attention — UNMATCHED and AMBIGUOUS.</summary>
    public string? MatchStatus { get; set; }
    public int     Page        { get; set; } = 1;
    public int     PageSize    { get; set; } = 20;
}
