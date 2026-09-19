namespace SMS.Modules.Logistics.Models;

public class RateConsignmentRequest
{
    /// <summary>
    /// The service to price. Defaults to whatever the consignment already names, and failing that
    /// to the account's default.
    /// </summary>
    public string? ServiceCode { get; set; }

    /// <summary>The account to ask on. Defaults to the carrier's default account.</summary>
    public Guid? CarrierAccountUuid { get; set; }

    /// <summary>When the goods would be handed over. Rates and transit times are dated. Defaults to today.</summary>
    public DateTime? ShipDate { get; set; }

    /// <summary>
    /// Skips the carrier's rate API and prices from the card. For checking what a tariff says the
    /// carriage should have cost — which is the question Phase 4 asks of every invoice.
    /// </summary>
    public bool RateCardOnly { get; set; }
}

public class ManualRateRequest
{
    public decimal Amount   { get; set; }
    public string  Currency { get; set; } = string.Empty;

    /// <summary>Where the figure came from. Required — a keyed-in price with no provenance is a guess.</summary>
    public string  Note        { get; set; } = string.Empty;
    public string? ServiceCode { get; set; }
}

public class ConsignmentChargeModel
{
    public string  Code        { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Amount      { get; set; }
}

/// <summary>One source that was tried, and what it said. Present whether it worked or not.</summary>
/// <remarks>
/// Both attempts are reported even when the first succeeds, because "the carrier quoted this" and
/// "the carrier would not answer, so the card was used" are different facts about the same number.
/// </remarks>
public class RateAttemptModel
{
    /// <summary>CARRIER or RATE_CARD.</summary>
    public string  Source    { get; set; } = string.Empty;
    public bool    Succeeded { get; set; }
    public string? Message   { get; set; }
}

public class ConsignmentRateModel
{
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;
    public string Status            { get; set; } = string.Empty;

    /// <summary>False when nothing has priced this consignment yet.</summary>
    public bool IsRated { get; set; }

    public decimal?  FreightCost     { get; set; }
    public string?   FreightCurrency { get; set; }
    public DateTime? RatedAt         { get; set; }

    /// <summary>CARRIER, RATE_CARD or MANUAL.</summary>
    public string? Source      { get; set; }
    public string? Note        { get; set; }
    public string? ServiceCode { get; set; }

    public decimal? ChargeableWeightKg { get; set; }

    public int?      TransitDays       { get; set; }
    public DateTime? EstimatedDelivery { get; set; }

    public List<ConsignmentChargeModel> Charges  { get; set; } = [];
    public List<RateAttemptModel>       Attempts { get; set; } = [];

    /// <summary>Doubts about the figure — an unrateable package, a missing address.</summary>
    public List<string> Warnings { get; set; } = [];
}
