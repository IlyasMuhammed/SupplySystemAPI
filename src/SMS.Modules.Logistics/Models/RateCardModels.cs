namespace SMS.Modules.Logistics.Models;

public class CreateRateCardRequest
{
    public Guid    CarrierUuid { get; set; }

    /// <summary>The service this card prices, or null for every service the carrier sells.</summary>
    public string? ServiceCode { get; set; }

    public string  Name     { get; set; } = string.Empty;
    public string  Currency { get; set; } = "PKR";

    public DateTime  EffectiveFrom { get; set; }
    public DateTime? EffectiveTo   { get; set; }

    public decimal? MinimumCharge        { get; set; }
    public decimal? FuelSurchargePercent { get; set; }
    public decimal? CodFeePercent        { get; set; }
    public decimal? CodFeeMinimum        { get; set; }

    /// <summary>At least one, each with at least one weight break starting at zero.</summary>
    public List<RateCardLaneRequest> Lanes { get; set; } = [];
}

public class PatchRateCardRequest
{
    public string?   Name                 { get; set; }
    public DateTime? EffectiveFrom        { get; set; }
    public DateTime? EffectiveTo          { get; set; }
    public decimal?  MinimumCharge        { get; set; }
    public decimal?  FuelSurchargePercent { get; set; }
    public decimal?  CodFeePercent        { get; set; }
    public decimal?  CodFeeMinimum        { get; set; }
    public bool?     IsActive             { get; set; }

    /// <summary>
    /// When given, <b>replaces</b> every lane on the card. A tariff is edited as a whole — patching
    /// individual breaks would let a card spend a moment with a hole in it, and a card with a hole
    /// prices most consignments correctly and one silently wrong.
    /// </summary>
    public List<RateCardLaneRequest>? Lanes { get; set; }

    /// <summary>
    /// Terms to clear back to nothing — a null above means "leave alone". Valid values:
    /// EFFECTIVE_TO, MINIMUM_CHARGE, FUEL_SURCHARGE, COD_FEE.
    /// </summary>
    public List<string>? ClearTerms { get; set; }
}

public class RateCardLaneRequest
{
    public string? Name { get; set; }

    /// <summary>Null matches anywhere.</summary>
    public string? OriginCountryIso          { get; set; }
    public string? OriginPostcodePrefix      { get; set; }
    public string? DestinationCountryIso     { get; set; }
    public string? DestinationPostcodePrefix { get; set; }

    public List<RateCardBreakRequest> Breaks { get; set; } = [];
}

public class RateCardBreakRequest
{
    public decimal FromWeightKg { get; set; }

    /// <summary>PER_KG or FLAT.</summary>
    public string  Basis  { get; set; } = "PER_KG";
    public decimal Amount { get; set; }
}

// ── Read models ───────────────────────────────────────────────────────────────

public class RateCardModel
{
    public Guid    UUID        { get; set; }
    public Guid    CarrierUuid { get; set; }
    public string  CarrierName { get; set; } = string.Empty;
    public string? ServiceCode { get; set; }

    public string Name     { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;

    public DateTime  EffectiveFrom { get; set; }
    public DateTime? EffectiveTo   { get; set; }

    public decimal? MinimumCharge        { get; set; }
    public decimal? FuelSurchargePercent { get; set; }
    public decimal? CodFeePercent        { get; set; }
    public decimal? CodFeeMinimum        { get; set; }

    public bool IsActive { get; set; }

    /// <summary>True on the date asked about — stated, so a reader is not left comparing dates.</summary>
    public bool IsInEffect { get; set; }

    public DateTime CreatedDate { get; set; }

    public List<RateCardLaneModel> Lanes { get; set; } = [];
}

public class RateCardLaneModel
{
    public Guid    UUID { get; set; }
    public string? Name { get; set; }

    public string? OriginCountryIso          { get; set; }
    public string? OriginPostcodePrefix      { get; set; }
    public string? DestinationCountryIso     { get; set; }
    public string? DestinationPostcodePrefix { get; set; }

    public List<RateCardBreakModel> Breaks { get; set; } = [];
}

public class RateCardBreakModel
{
    public Guid    UUID         { get; set; }
    public decimal FromWeightKg { get; set; }
    public string  Basis        { get; set; } = string.Empty;
    public decimal Amount       { get; set; }
}
