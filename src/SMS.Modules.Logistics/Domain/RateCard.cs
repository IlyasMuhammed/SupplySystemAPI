using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// A negotiated tariff — what a carrier has agreed to charge, typed in rather than asked for.
/// <para>
/// <b>Why this exists beside <c>ICourierProvider.RateAsync</c> (decision G9).</b> Most carriers will
/// never answer a rate call: the manual adapter is the common case, not the exception. And even
/// where one does, Phase 4's three-way match needs something to check an invoice <em>against</em> —
/// an invoice reconciled only against what the carrier itself said is not reconciled at all.
/// </para>
/// <para>
/// <b>One card per carrier, service and period.</b> Two cards that could both apply on the same day
/// would make the price depend on row order, so overlapping periods are refused on write. Lanes and
/// weight breaks live <em>inside</em> a card, which is what keeps that rule simple enough to be total.
/// </para>
/// </summary>
internal class RateCard : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int     CarrierId { get; set; }
    public Carrier Carrier   { get; set; } = null!;

    /// <summary>
    /// The <see cref="CarrierService"/> code this card prices, or <b>null for every service</b> the
    /// carrier sells. A service-specific card beats a carrier-wide one, so the general card is a
    /// fallback rather than a competitor.
    /// </summary>
    public string? ServiceCode { get; set; }

    /// <summary>What a human calls it — "DHL Domestic 2026", "Road freight, Q3".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>ISO 4217. A tariff with no currency is a list of numbers.</summary>
    public string Currency { get; set; } = "PKR";

    // ── When it applies ───────────────────────────────────────────────────────

    /// <summary>
    /// Inclusive. Dated because tariffs are renegotiated and last year's consignments must still
    /// price at last year's rates — which is the whole of what makes an old invoice checkable.
    /// </summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Inclusive. Null means open-ended — the current card.</summary>
    public DateTime? EffectiveTo { get; set; }

    // ── Terms that apply across every lane ────────────────────────────────────

    /// <summary>The smallest carriage charge, whatever the weight breaks work out to.</summary>
    public decimal? MinimumCharge { get; set; }

    /// <summary>
    /// A percentage of the base rate, which is how fuel is billed everywhere in this industry. It
    /// is restated monthly, so it lives on the card rather than inside the breaks.
    /// </summary>
    public decimal? FuelSurchargePercent { get; set; }

    public decimal? CodFeePercent { get; set; }
    public decimal? CodFeeMinimum { get; set; }

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<RateCardLane> Lanes { get; set; } = new List<RateCardLane>();
}

/// <summary>
/// Where a set of weight breaks applies. Every criterion is optional, and a null means "anywhere" —
/// so a card can hold one catch-all lane and a handful of specific ones.
/// <para>
/// <b>Country and postcode prefix only.</b> City names are free text that arrive spelled four ways;
/// matching on them looks more precise and is less reliable, and a lane that silently fails to match
/// prices a consignment on the wrong tariff.
/// </para>
/// </summary>
internal class RateCardLane : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int      RateCardId { get; set; }
    public RateCard RateCard   { get; set; } = null!;

    public string? OriginCountryIso      { get; set; }
    public string? OriginPostcodePrefix  { get; set; }

    public string? DestinationCountryIso     { get; set; }
    public string? DestinationPostcodePrefix { get; set; }

    /// <summary>What a human calls it — "Karachi to upcountry", "Domestic", "Export".</summary>
    public string? Name { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<RateCardBreak> Breaks { get; set; } = new List<RateCardBreak>();
}

/// <summary>
/// One weight break: from this weight upward, this is the rate — until the next break.
/// <para>
/// A lane's breaks must start at zero and ascend without repeating, enforced on write. A card with
/// a hole in it is worse than no card: it prices most consignments correctly and one silently wrong.
/// </para>
/// </summary>
internal class RateCardBreak : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int          RateCardLaneId { get; set; }
    public RateCardLane RateCardLane   { get; set; } = null!;

    /// <summary>Inclusive lower bound, in kilograms of <b>chargeable</b> weight (T-44).</summary>
    public decimal FromWeightKg { get; set; }

    /// <summary>See <see cref="Domain.RateBasis"/>. Stored as its code.</summary>
    public string Basis { get; set; } = LogisticsCode.Of(RateBasis.PerKg);

    /// <summary>Per kilogram, or the whole charge, depending on <see cref="Basis"/>.</summary>
    public decimal Amount { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
