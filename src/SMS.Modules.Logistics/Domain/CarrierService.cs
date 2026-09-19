using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// One named product a carrier sells — "Overnight", "Economy", "Same Day".
/// <para>
/// <b>The thing <c>CarrierServiceCode</c> should always have pointed at.</b> Since T-08 it has been
/// a free string on a consignment and on a carrier account, matched against nothing: a typo booked
/// a service that does not exist, and no code could answer what a service actually costs or how
/// long it takes.
/// </para>
/// <para>
/// It carries the <see cref="DimDivisor"/>, which is why this comes before dimensional weight
/// (T-44) rather than after it. A dim divisor is not a property of a parcel or of a carrier — it
/// is a term of the specific product being bought, and the same carrier commonly uses 5000 on one
/// service and 6000 on another.
/// </para>
/// <para>
/// Deliberately <b>not</b> a foreign key from <c>Consignment.CarrierServiceCode</c>. Those rows
/// exist already, carrying codes that may match nothing, and a delivery booked months ago under a
/// service since withdrawn must still read. The code stays a string and this table explains it
/// where it can — the same choice made for cross-module UUIDs everywhere else in this module.
/// </para>
/// </summary>
internal class CarrierService : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int     CarrierId { get; set; }
    public Carrier Carrier   { get; set; } = null!;

    /// <summary>The carrier's own code, as it appears in their API and on their invoices.</summary>
    public string ServiceCode { get; set; } = string.Empty;

    /// <summary>What a human calls it.</summary>
    public string ServiceName { get; set; } = string.Empty;

    public string? Description { get; set; }

    // ── How it is priced ──────────────────────────────────────────────────────

    /// <summary>
    /// The divisor in <c>L×W×H ÷ divisor</c>, in centimetres, giving kilograms. Commonly 5000 or
    /// 6000; a smaller number charges more for bulky freight.
    /// <para>
    /// Null means this service does not charge on volume, and chargeable weight is simply the
    /// actual weight. That is a real arrangement — some road freight and most same-day courier
    /// work is priced by actual weight or by the vehicle — so null is a meaningful answer rather
    /// than missing data, and T-44 treats it as one.
    /// </para>
    /// </summary>
    public int? DimDivisor { get; set; }

    /// <summary>
    /// The smallest weight this service will bill, whatever the parcel weighs. An envelope on a
    /// service with a 0.5 kg minimum is charged at 0.5 kg.
    /// </summary>
    public decimal? MinimumChargeableKg { get; set; }

    /// <summary>
    /// The step a chargeable weight is rounded <b>up</b> to — 0.5 for carriers that bill in half
    /// kilos, 1 for those that bill whole ones. Added in T-44: it is the third term of the same
    /// formula as <see cref="DimDivisor"/> and <see cref="MinimumChargeableKg"/>, and without it a
    /// 10.2 kg parcel is quoted at 10.2 kg while every real carrier invoice says 11.
    /// <para>Null means the service bills the exact figure, to three decimal places.</para>
    /// </summary>
    public decimal? WeightRoundingKg { get; set; }

    // ── What it will carry ────────────────────────────────────────────────────

    /// <summary>Refused above this. Null means the service sets no limit of its own.</summary>
    public decimal? MaxWeightKgPerPackage { get; set; }
    public decimal? MaxLengthCm           { get; set; }

    /// <summary>
    /// Sum of length plus girth, the limit couriers actually enforce on long thin parcels —
    /// <c>L + 2×(W+H)</c>. A tube can pass every single-dimension limit and still be refused.
    /// </summary>
    public decimal? MaxLengthPlusGirthCm { get; set; }

    public bool SupportsCod       { get; set; }
    public bool SupportsHazardous { get; set; }

    /// <summary>Working days, for an ETA and for choosing between services on speed.</summary>
    public int? TransitDays { get; set; }

    /// <summary>
    /// Offered when a consignment names no service and the account names no default. Exactly one
    /// per carrier, enforced on write — two would make the service booked depend on row order.
    /// </summary>
    public bool IsDefault { get; set; }

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
