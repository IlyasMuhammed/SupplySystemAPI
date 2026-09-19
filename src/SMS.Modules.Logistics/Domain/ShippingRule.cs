using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// "Anything under 5 kg going to Lahore ships on the cheapest courier; hazardous goods always go by
/// road with Beta." A standing decision about how goods are shipped, written down once.
/// <para>
/// <b>Rules narrow the choice; they do not replace it.</b> A rule decides which carriers and which
/// service are eligible, and rate shopping (T-48) still does the pricing — so a rule never has to
/// be rewritten when a tariff changes, and the figure it produces is the same figure a person
/// comparing by hand would have seen.
/// </para>
/// <para>
/// <b>Every condition is optional, and all the stated ones must hold.</b> A rule with no conditions
/// matches everything, which is exactly what a catch-all rule at the bottom of the list should do.
/// </para>
/// </summary>
internal class ShippingRule : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Lowest first, and unique within an organization. Two rules sharing a priority would make the
    /// one that fires depend on row order — the same ambiguity carrier service codes exist to end,
    /// and here it would quietly ship goods on the wrong carrier.
    /// </summary>
    public int Priority { get; set; }

    // ── Conditions ────────────────────────────────────────────────────────────

    /// <summary>Inclusive, against the consignment's <b>chargeable</b> weight (T-44).</summary>
    public decimal? MinChargeableWeightKg { get; set; }

    /// <summary>Inclusive. Null means no upper bound.</summary>
    public decimal? MaxChargeableWeightKg { get; set; }

    public string? OriginCountryIso          { get; set; }
    public string? OriginPostcodePrefix      { get; set; }
    public string? DestinationCountryIso     { get; set; }
    public string? DestinationPostcodePrefix { get; set; }

    /// <summary>Against the declared value of everything packed. Inclusive.</summary>
    public decimal? MinDeclaredValue { get; set; }
    public decimal? MaxDeclaredValue { get; set; }

    /// <summary>
    /// True matches only hazardous consignments, false only non-hazardous, null either. Three
    /// states rather than two, because "this rule is for dangerous goods" and "this rule does not
    /// care" are different statements and collapsing them loses the one that matters.
    /// </summary>
    public bool? AppliesToHazardous { get; set; }

    /// <summary>Same three states, for cash on delivery.</summary>
    public bool? AppliesToCod { get; set; }

    // ── What it decides ───────────────────────────────────────────────────────

    /// <summary>The carrier to use. Null means "any carrier", decided by <see cref="Strategy"/>.</summary>
    public int?     CarrierId { get; set; }
    public Carrier? Carrier   { get; set; }

    /// <summary>The service to use. Null means any the chosen carrier sells.</summary>
    public string? ServiceCode { get; set; }

    /// <summary>
    /// CHEAPEST or FASTEST, handed straight to rate shopping. Null falls back to CHEAPEST — a rule
    /// that names a carrier and nothing else still has to pick between that carrier's services.
    /// </summary>
    public string? Strategy { get; set; }

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
