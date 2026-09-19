using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// Layer B — how the goods are physically packed. SAP calls this a Handling Unit, D365 a License Plate.
/// <para>
/// One row per carton or pallet. This is what makes dimensional-weight rating correct, what a
/// per-parcel label prints from, and what a courier tracks individually on a multi-piece
/// consignment. Without it, rating can only ever use actual weight, which is wrong for anything
/// bulky and light.
/// </para>
/// <para>
/// Dimensions and weights are stored in <b>centimetres and kilograms only</b>. Carriers report a
/// mix of units; adapters convert at the edge so nothing downstream has to ask. Mixed units in a
/// shared table is one of the classic silent-corruption bugs in courier integrations, so the unit
/// is in the column name rather than in a separate field that can disagree.
/// </para>
/// </summary>
internal class ShipmentPackage : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int           DeliveryOrderId { get; set; }
    public DeliveryOrder DeliveryOrder   { get; set; } = null!;

    /// <summary>The handling-unit barcode carried on the physical carton.</summary>
    public string PackageBarcode { get; set; } = string.Empty;

    /// <summary>See <see cref="Domain.PackageType"/>. Stored as its code.</summary>
    public string PackageType { get; set; } = LogisticsCode.Of(Domain.PackageType.Box);

    public decimal? LengthCm { get; set; }
    public decimal? WidthCm  { get; set; }
    public decimal? HeightCm { get; set; }

    public decimal? GrossWeightKg { get; set; }
    public decimal? NetWeightKg   { get; set; }

    /// <summary>
    /// Volumetric weight — <c>L×W×H ÷ divisor</c>, the divisor coming from the carrier service.
    /// Written by T-44, and null until a consignment carrying this package has been rated.
    /// </summary>
    public decimal? DimWeightKg { get; set; }

    /// <summary>
    /// The divisor that produced <see cref="DimWeightKg"/>. Stored beside the number rather than
    /// looked up again, because the stored figure is only explainable if the term that made it is
    /// kept with it — a service may change its divisor, and a past quote must still add up.
    /// </summary>
    public int? DimWeightDivisor { get; set; }

    /// <summary>
    /// What the carrier actually bills on: <c>max(actual, volumetric)</c>, rounded up to the
    /// service's step and floored at its minimum. This is the number every tariff multiplies.
    /// </summary>
    public decimal? ChargeableWeightKg { get; set; }

    /// <summary>
    /// Which term decided it — see <see cref="Domain.ChargeableWeightBasis"/>, stored as its code.
    /// "12 kg" is not a figure anybody can check; "12 kg, because volume beat the scale" is.
    /// </summary>
    public string? ChargeableWeightBasis { get; set; }

    /// <summary>When the three figures above were last worked out.</summary>
    public DateTime? WeightRatedAt { get; set; }

    public decimal? DeclaredValue { get; set; }
    public string?  SealNumber    { get; set; }

    /// <summary>Set when this package sits on a pallet, which is itself a package.</summary>
    public int?             ParentPackageId { get; set; }
    public ShipmentPackage? ParentPackage   { get; set; }

    /// <summary>
    /// A voided package is kept, not deleted — its barcode may already be on a printed label
    /// stuck to a real carton, and reusing it would make two cartons indistinguishable.
    /// </summary>
    public bool    IsVoided   { get; set; }
    public string? VoidReason { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<ShipmentPackage> ChildPackages { get; set; } = new List<ShipmentPackage>();
    public ICollection<PackageContent>  Contents      { get; set; } = new List<PackageContent>();
}

/// <summary>
/// What is inside a package, mapped back to the delivery line it came from. The join that lets a
/// multi-parcel consignment say which units are in which box.
/// </summary>
internal class PackageContent : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int             ShipmentPackageId { get; set; }
    public ShipmentPackage ShipmentPackage   { get; set; } = null!;

    public int               DeliveryOrderLineId { get; set; }
    public DeliveryOrderLine DeliveryOrderLine   { get; set; } = null!;

    public decimal Qty { get; set; }

    public string? BatchNumber  { get; set; }
    public string? SerialNumber { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
