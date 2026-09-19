namespace SMS.Modules.Logistics.Models;

/// <summary>What one handling unit is charged on, and why.</summary>
public class PackageWeightModel
{
    public Guid   PackageUuid    { get; set; }
    public string PackageBarcode { get; set; } = string.Empty;
    public string PackageType    { get; set; } = string.Empty;

    public decimal? LengthCm { get; set; }
    public decimal? WidthCm  { get; set; }
    public decimal? HeightCm { get; set; }

    /// <summary>Gross weight, as weighed. Null when nobody has weighed it.</summary>
    public decimal? ActualKg { get; set; }

    /// <summary>L×W×H ÷ divisor. Null when the service does not charge on volume, or the package is unmeasured.</summary>
    public decimal? VolumetricKg { get; set; }

    /// <summary>What the carrier bills on. Null when the package can be rated on nothing at all.</summary>
    public decimal? ChargeableKg { get; set; }

    /// <summary>ACTUAL, VOLUMETRIC, MINIMUM or UNKNOWN — which term decided the figure above.</summary>
    public string Basis { get; set; } = string.Empty;

    /// <summary>The divisor that produced <see cref="VolumetricKg"/>, so the figure can be checked.</summary>
    public int? DivisorUsed { get; set; }

    /// <summary>The longest of the three dimensions, whichever column it was typed into.</summary>
    public decimal? LongestSideCm { get; set; }

    /// <summary>L + 2×(W+H), the limit couriers enforce on long thin parcels.</summary>
    public decimal? LengthPlusGirthCm { get; set; }

    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// A consignment's chargeable weight, package by package, with the service terms that produced it.
/// </summary>
/// <remarks>
/// The terms are returned alongside the figures deliberately: a total with no divisor beside it is
/// a number nobody can check, and checking it is the entire point once a carrier invoice turns up.
/// </remarks>
public class ConsignmentWeightModel
{
    public Guid   ConsignmentUuid   { get; set; }
    public string ConsignmentNumber { get; set; } = string.Empty;

    public string? CarrierName { get; set; }

    /// <summary>The service code the consignment names — which may match nothing.</summary>
    public string? CarrierServiceCode { get; set; }

    /// <summary>The service that code resolved to, or null when it resolved to nothing.</summary>
    public string? ResolvedServiceCode { get; set; }
    public string? ResolvedServiceName { get; set; }

    // ── The terms in force ────────────────────────────────────────────────────

    public int?     DimDivisor          { get; set; }
    public decimal? MinimumChargeableKg { get; set; }
    public decimal? WeightRoundingKg    { get; set; }
    public bool     ChargesVolumetricWeight { get; set; }

    // ── The totals ────────────────────────────────────────────────────────────

    public int     PackageCount      { get; set; }
    public decimal TotalActualKg     { get; set; }
    public decimal TotalVolumetricKg { get; set; }

    /// <summary>The sum of the per-package chargeable weights. Carriers bill piece by piece, not on the sum.</summary>
    public decimal TotalChargeableKg { get; set; }

    /// <summary>
    /// False when any package could not be rated. The total is still returned — it is the honest
    /// total of what <em>is</em> known — but it is a floor, not a quote.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>When these figures were last written onto the packages. Null if never.</summary>
    public DateTime? LastRatedAt { get; set; }

    /// <summary>Consignment-level doubts: no carrier, an unmatched service code, a shared delivery.</summary>
    public List<string> Warnings { get; set; } = [];

    public List<PackageWeightModel> Packages { get; set; } = [];
}
