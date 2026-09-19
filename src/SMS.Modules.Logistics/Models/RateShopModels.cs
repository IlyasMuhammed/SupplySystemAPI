namespace SMS.Modules.Logistics.Models;

public class RateShopRequest
{
    /// <summary>The carriers to compare. Empty means every active carrier.</summary>
    public List<Guid>? CarrierUuids { get; set; }

    /// <summary>Restricts the comparison to one service code, where a carrier sells it.</summary>
    public string? ServiceCode { get; set; }

    public DateTime? ShipDate { get; set; }

    /// <summary>
    /// The date the goods have to be there by. Options that arrive later are still returned — under
    /// <c>excluded</c>, with the reason — because the cheapest option that misses a deadline by a
    /// day is exactly what somebody needs to see before accepting the one that does not.
    /// </summary>
    public DateTime? RequiredBy { get; set; }

    /// <summary>CHEAPEST (the default) or FASTEST.</summary>
    public string? Strategy { get; set; }

    /// <summary>Prices from rate cards only, without asking a single carrier.</summary>
    public bool RateCardOnly { get; set; }
}

public class AcceptRateRequest
{
    public Guid    CarrierUuid        { get; set; }
    public Guid?   CarrierAccountUuid { get; set; }
    public string  ServiceCode        { get; set; } = string.Empty;

    /// <summary>CARRIER or RATE_CARD — which of the two quoted the option being accepted.</summary>
    public string? Source { get; set; }

    public DateTime? ShipDate { get; set; }
}

public class RateShopOptionModel
{
    public Guid    CarrierUuid { get; set; }
    public string  CarrierName { get; set; } = string.Empty;

    public Guid?   CarrierAccountUuid { get; set; }
    public string? CarrierAccountName { get; set; }

    public string  ServiceCode { get; set; } = string.Empty;
    public string? ServiceName { get; set; }

    /// <summary>CARRIER or RATE_CARD.</summary>
    public string Source { get; set; } = string.Empty;

    public decimal TotalAmount { get; set; }
    public string  Currency    { get; set; } = string.Empty;
    public decimal? BaseAmount { get; set; }

    public List<ConsignmentChargeModel> Surcharges { get; set; } = [];

    public int?      TransitDays       { get; set; }
    public DateTime? EstimatedDelivery { get; set; }
    public bool      IsGuaranteed      { get; set; }

    /// <summary>1 is the winner. Null when the option could not be ranked — see <see cref="Note"/>.</summary>
    public int? Rank { get; set; }

    /// <summary>What this costs above the winner. Zero on the winner itself.</summary>
    public decimal? MoreThanBest { get; set; }

    /// <summary>The same, as a percentage of the winner. Easier to argue with than an absolute.</summary>
    public decimal? MoreThanBestPercent { get; set; }

    /// <summary>Why this option is where it is — and, on the winner, why it won.</summary>
    public string? Note { get; set; }
}

/// <summary>A carrier or service that produced no comparable option, and why.</summary>
/// <remarks>
/// Returned rather than dropped. A shop that silently omits a carrier looks like a shop that found
/// nothing there, and the two lead to very different next actions.
/// </remarks>
public class RateShopExclusionModel
{
    public Guid    CarrierUuid { get; set; }
    public string  CarrierName { get; set; } = string.Empty;
    public string? ServiceCode { get; set; }
    public string  Reason      { get; set; } = string.Empty;

    /// <summary>The price it would have been, when it was quoted and then ruled out on a constraint.</summary>
    public decimal? TotalAmount { get; set; }
    public string?  Currency    { get; set; }
}

public class RateShopResultModel
{
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;

    public decimal  ChargeableWeightKg { get; set; }
    public DateTime ShipDate           { get; set; }
    public DateTime? RequiredBy        { get; set; }

    /// <summary>CHEAPEST or FASTEST.</summary>
    public string Strategy { get; set; } = string.Empty;

    /// <summary>Ranked options first, then anything that could not be ranked.</summary>
    public List<RateShopOptionModel> Options { get; set; } = [];

    public List<RateShopExclusionModel> Excluded { get; set; } = [];

    /// <summary>The winner, restated so a caller does not have to find rank 1 itself. Null if none.</summary>
    public RateShopOptionModel? Recommended { get; set; }

    /// <summary>Why it won, in one sentence somebody could repeat to a manager.</summary>
    public string? Recommendation { get; set; }

    public List<string> Warnings { get; set; } = [];
}
