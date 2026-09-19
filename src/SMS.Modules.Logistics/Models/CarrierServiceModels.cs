namespace SMS.Modules.Logistics.Models;

public class CreateCarrierServiceRequest
{
    public Guid    CarrierUuid { get; set; }
    public string  ServiceCode { get; set; } = string.Empty;
    public string  ServiceName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Null when the service does not charge on volume — a real answer, not missing data.</summary>
    public int?     DimDivisor            { get; set; }
    public decimal? MinimumChargeableKg   { get; set; }
    /// <summary>The step chargeable weight rounds up to — 0.5 or 1 for most couriers (T-44).</summary>
    public decimal? WeightRoundingKg      { get; set; }
    public decimal? MaxWeightKgPerPackage { get; set; }
    public decimal? MaxLengthCm           { get; set; }
    public decimal? MaxLengthPlusGirthCm  { get; set; }

    public bool SupportsCod       { get; set; }
    public bool SupportsHazardous { get; set; }
    public int? TransitDays       { get; set; }
    public bool IsDefault         { get; set; }
}

public class PatchCarrierServiceRequest
{
    public string?  ServiceName           { get; set; }
    public string?  Description           { get; set; }
    public int?     DimDivisor            { get; set; }
    public decimal? MinimumChargeableKg   { get; set; }
    public decimal? WeightRoundingKg      { get; set; }
    public decimal? MaxWeightKgPerPackage { get; set; }
    public decimal? MaxLengthCm           { get; set; }
    public decimal? MaxLengthPlusGirthCm  { get; set; }
    public bool?    SupportsCod           { get; set; }
    public bool?    SupportsHazardous     { get; set; }
    public int?     TransitDays           { get; set; }
    public bool?    IsDefault             { get; set; }
    public bool?    IsActive              { get; set; }

    /// <summary>
    /// Limits to clear back to "no limit". A null above means "leave alone" on a patch, so this is
    /// the only way to remove one — the same reason `clearOverrides` exists on a carrier account.
    /// Valid values: DIM_DIVISOR, MINIMUM_CHARGEABLE, WEIGHT_ROUNDING, MAX_WEIGHT, MAX_LENGTH,
    /// MAX_GIRTH, TRANSIT_DAYS.
    /// </summary>
    public List<string>? ClearLimits { get; set; }
}

public class CarrierServiceModel
{
    public Guid   UUID        { get; set; }
    public Guid   CarrierUuid { get; set; }
    public string CarrierName { get; set; } = string.Empty;

    public string  ServiceCode { get; set; } = string.Empty;
    public string  ServiceName { get; set; } = string.Empty;
    public string? Description { get; set; }

    public int?     DimDivisor            { get; set; }
    public decimal? MinimumChargeableKg   { get; set; }
    public decimal? WeightRoundingKg      { get; set; }
    public decimal? MaxWeightKgPerPackage { get; set; }
    public decimal? MaxLengthCm           { get; set; }
    public decimal? MaxLengthPlusGirthCm  { get; set; }

    public bool SupportsCod       { get; set; }
    public bool SupportsHazardous { get; set; }
    public int? TransitDays       { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive  { get; set; }

    /// <summary>
    /// False when the service does not price on volume. Stated rather than left for a reader to
    /// infer from a null divisor, because "we do not charge volumetrically" and "nobody has filled
    /// this in" look identical otherwise.
    /// </summary>
    public bool ChargesVolumetricWeight { get; set; }

    public DateTime CreatedDate { get; set; }
}
