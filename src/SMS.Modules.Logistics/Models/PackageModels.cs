namespace SMS.Modules.Logistics.Models;

/// <summary>One line of what went into a carton.</summary>
public class PackContentRequest
{
    /// <summary>The delivery line these units came from.</summary>
    public Guid DeliveryLineUuid { get; set; }

    public decimal Qty { get; set; }

    /// <summary>
    /// Optional. Left empty, it is inherited from the pick when the line was picked from exactly
    /// one batch — which is the common case, and what a packing list needs printed on it.
    /// </summary>
    public string? BatchNumber  { get; set; }
    public string? SerialNumber { get; set; }
}

public class PackRequest
{
    /// <summary>
    /// The barcode already printed on the carton, if there is one. Left empty, one is issued.
    /// Supplying it matters for pre-printed handling-unit labels, where the physical box already
    /// carries a number that the system has to match rather than replace.
    /// </summary>
    public string? PackageBarcode { get; set; }

    /// <summary>See <c>PackageType</c>. Defaults to BOX.</summary>
    public string? PackageType { get; set; }

    public decimal? LengthCm { get; set; }
    public decimal? WidthCm  { get; set; }
    public decimal? HeightCm { get; set; }

    public decimal? GrossWeightKg { get; set; }
    public decimal? NetWeightKg   { get; set; }

    public decimal? DeclaredValue { get; set; }
    public string?  SealNumber    { get; set; }

    /// <summary>The pallet this carton is being loaded onto, if any.</summary>
    public Guid? ParentPackageUuid { get; set; }

    public List<PackContentRequest> Contents { get; set; } = [];
}

/// <summary>Dimensions, weights and the handful of fields a packer corrects after the fact.</summary>
public class PatchPackageRequest
{
    public string?  PackageType   { get; set; }
    public decimal? LengthCm      { get; set; }
    public decimal? WidthCm       { get; set; }
    public decimal? HeightCm      { get; set; }
    public decimal? GrossWeightKg { get; set; }
    public decimal? NetWeightKg   { get; set; }
    public decimal? DeclaredValue { get; set; }
    public string?  SealNumber    { get; set; }

    /// <summary>Move the carton onto a pallet, or off one — pass an explicit null to unload it.</summary>
    public Guid? ParentPackageUuid { get; set; }
    public bool  ClearParent       { get; set; }
}

public class PackageContentModel
{
    public Guid    UUID             { get; set; }
    public Guid    DeliveryLineUuid { get; set; }
    public int     DeliveryLineNo   { get; set; }
    public Guid?   VariantUuid      { get; set; }
    public string  ItemDescription  { get; set; } = string.Empty;
    public string? UnitOfMeasure    { get; set; }
    public decimal Qty              { get; set; }
    public string? BatchNumber      { get; set; }
    public string? SerialNumber     { get; set; }
}

public class PackageModel
{
    public Guid   UUID           { get; set; }
    public string PackageBarcode { get; set; } = string.Empty;
    public string PackageType    { get; set; } = string.Empty;

    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;

    public decimal? LengthCm { get; set; }
    public decimal? WidthCm  { get; set; }
    public decimal? HeightCm { get; set; }

    public decimal? GrossWeightKg { get; set; }
    public decimal? NetWeightKg   { get; set; }

    /// <summary>Null until a carrier service with a dim divisor rates it (Phase 3).</summary>
    public decimal? DimWeightKg { get; set; }

    /// <summary>L×W×H ÷ 1,000,000, in cubic metres. Null unless all three are known.</summary>
    public decimal? VolumeM3 { get; set; }

    public decimal? DeclaredValue { get; set; }
    public string?  SealNumber    { get; set; }

    public Guid?   ParentPackageUuid    { get; set; }
    public string? ParentPackageBarcode { get; set; }

    /// <summary>Barcodes of the cartons loaded onto this one, when it is a pallet.</summary>
    public List<string> ChildPackageBarcodes { get; set; } = [];

    public bool    IsVoided   { get; set; }
    public string? VoidReason { get; set; }

    public DateTime CreatedDate { get; set; }

    public List<PackageContentModel> Contents { get; set; } = [];
}

/// <summary>
/// What is packed against a delivery and what is still on the floor waiting for a box.
/// </summary>
public class DeliveryPackingModel
{
    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;

    /// <summary>True once every picked unit is in a carton.</summary>
    public bool IsFullyPacked { get; set; }

    public decimal QtyPicked    { get; set; }
    public decimal QtyPacked    { get; set; }
    public decimal QtyUnpacked  { get; set; }

    public decimal TotalGrossWeightKg { get; set; }

    public List<PackingLineModel> Lines    { get; set; } = [];
    public List<PackageModel>     Packages { get; set; } = [];
}

/// <summary>One delivery line's packing progress.</summary>
public class PackingLineModel
{
    public Guid    DeliveryLineUuid { get; set; }
    public int     LineNo           { get; set; }
    public Guid?   VariantUuid      { get; set; }
    public string  ItemDescription  { get; set; } = string.Empty;
    public string? UnitOfMeasure    { get; set; }
    public decimal QtyPicked        { get; set; }
    public decimal QtyPacked        { get; set; }

    /// <summary>Picked but not yet in a box. What the packer still has in front of them.</summary>
    public decimal QtyToPack { get; set; }
}
