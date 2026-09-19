namespace SMS.Modules.Logistics.Models;

public class CreateShippingRuleRequest
{
    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Lowest first, and unique. Two rules sharing one would make the winner depend on row order.</summary>
    public int Priority { get; set; }

    public decimal? MinChargeableWeightKg { get; set; }
    public decimal? MaxChargeableWeightKg { get; set; }

    public string? OriginCountryIso          { get; set; }
    public string? OriginPostcodePrefix      { get; set; }
    public string? DestinationCountryIso     { get; set; }
    public string? DestinationPostcodePrefix { get; set; }

    public decimal? MinDeclaredValue { get; set; }
    public decimal? MaxDeclaredValue { get; set; }

    /// <summary>True for hazardous only, false for non-hazardous only, null for either.</summary>
    public bool? AppliesToHazardous { get; set; }
    public bool? AppliesToCod       { get; set; }

    /// <summary>The carrier to use, or null to let the strategy choose across all of them.</summary>
    public Guid?  CarrierUuid { get; set; }
    public string? ServiceCode { get; set; }

    /// <summary>CHEAPEST (the default) or FASTEST.</summary>
    public string? Strategy { get; set; }
}

public class PatchShippingRuleRequest
{
    public string?  Name        { get; set; }
    public string?  Description { get; set; }
    public int?     Priority    { get; set; }
    public bool?    IsActive    { get; set; }

    public decimal? MinChargeableWeightKg { get; set; }
    public decimal? MaxChargeableWeightKg { get; set; }
    public string?  OriginCountryIso          { get; set; }
    public string?  OriginPostcodePrefix      { get; set; }
    public string?  DestinationCountryIso     { get; set; }
    public string?  DestinationPostcodePrefix { get; set; }
    public decimal? MinDeclaredValue   { get; set; }
    public decimal? MaxDeclaredValue   { get; set; }
    public bool?    AppliesToHazardous { get; set; }
    public bool?    AppliesToCod       { get; set; }

    public Guid?   CarrierUuid { get; set; }
    public string? ServiceCode { get; set; }
    public string? Strategy    { get; set; }

    /// <summary>
    /// Conditions to clear back to "does not care" — a null above means "leave alone". Valid values:
    /// MIN_WEIGHT, MAX_WEIGHT, ORIGIN_COUNTRY, ORIGIN_POSTCODE, DESTINATION_COUNTRY,
    /// DESTINATION_POSTCODE, MIN_VALUE, MAX_VALUE, HAZARDOUS, COD, CARRIER, SERVICE.
    /// </summary>
    public List<string>? ClearConditions { get; set; }
}

public class ShippingRuleModel
{
    public Guid    UUID        { get; set; }
    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int     Priority    { get; set; }
    public bool    IsActive    { get; set; }

    public decimal? MinChargeableWeightKg { get; set; }
    public decimal? MaxChargeableWeightKg { get; set; }
    public string?  OriginCountryIso          { get; set; }
    public string?  OriginPostcodePrefix      { get; set; }
    public string?  DestinationCountryIso     { get; set; }
    public string?  DestinationPostcodePrefix { get; set; }
    public decimal? MinDeclaredValue   { get; set; }
    public decimal? MaxDeclaredValue   { get; set; }
    public bool?    AppliesToHazardous { get; set; }
    public bool?    AppliesToCod       { get; set; }

    public Guid?   CarrierUuid { get; set; }
    public string? CarrierName { get; set; }
    public string? ServiceCode { get; set; }
    public string  Strategy    { get; set; } = string.Empty;

    /// <summary>What the rule says, in a sentence — so a list of rules reads without decoding.</summary>
    public string Summary { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; }
}

/// <summary>One rule that was looked at, and what became of it.</summary>
public class ShippingRuleVerdictModel
{
    public Guid   RuleUuid { get; set; }
    public string Name     { get; set; } = string.Empty;
    public int    Priority { get; set; }

    public bool   Matched { get; set; }

    /// <summary>Why it matched, or why it did not. Never empty.</summary>
    public string Reason { get; set; } = string.Empty;
}

public class ShippingRuleDecisionModel
{
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;

    // ── What the rules were judged against ────────────────────────────────────

    public decimal  ChargeableWeightKg { get; set; }
    public decimal? DeclaredValue      { get; set; }
    public bool     IsHazardous        { get; set; }
    public bool     HasCod             { get; set; }
    public string?  OriginCountryIso      { get; set; }
    public string?  OriginPostcode        { get; set; }
    public string?  DestinationCountryIso { get; set; }
    public string?  DestinationPostcode   { get; set; }

    // ── What fired ────────────────────────────────────────────────────────────

    /// <summary>The rule that decided it. Null when none matched.</summary>
    public ShippingRuleVerdictModel? MatchedRule { get; set; }

    /// <summary>
    /// Every rule considered, in priority order, including the ones that did not match and why.
    /// A decision that says only which rule fired leaves somebody reading conditions in a database
    /// at the moment a parcel has gone out on the wrong carrier.
    /// </summary>
    public List<ShippingRuleVerdictModel> Considered { get; set; } = [];

    /// <summary>What the matched rule narrowed the choice to, in a sentence.</summary>
    public string? Selection { get; set; }

    /// <summary>The option the rule's selection actually comes to. Null when nothing could be priced.</summary>
    public RateShopOptionModel? Recommended { get; set; }

    /// <summary>The full ranked comparison within the rule's constraints.</summary>
    public List<RateShopOptionModel> Options { get; set; } = [];

    public List<RateShopExclusionModel> Excluded { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}
