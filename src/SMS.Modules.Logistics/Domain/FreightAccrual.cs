using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// What is owed to a carrier for a movement that has already happened and has not yet been billed.
/// <para>
/// <b>Why it exists.</b> A consignment that has been collected is a liability whether or not an
/// invoice has arrived, and carriers commonly bill weeks later. Without an accrual, freight cost
/// appears in the month the invoice is keyed rather than the month the goods moved, and a period
/// closed on that basis is wrong by however much is in transit.
/// </para>
/// <para>
/// <b>It holds two of the three figures the match needs.</b> The quote (T-47) and what the carrier
/// said when it accepted the booking (<c>CarrierCommand.Cost</c>) are copied here at the moment of
/// dispatch, so they are fixed as of then and sit together — finding F45. The invoiced figure joins
/// them in T-55.
/// </para>
/// </summary>
internal class FreightAccrual : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>One accrual per consignment, enforced. Two would double the liability.</summary>
    public int         ConsignmentId { get; set; }
    public Consignment Consignment   { get; set; } = null!;

    public int?     CarrierId   { get; set; }
    public Carrier? Carrier     { get; set; }

    /// <summary>Denormalized so an accrual stays readable if the carrier is later deactivated.</summary>
    public string? CarrierName { get; set; }

    // ── The figures, as of dispatch ───────────────────────────────────────────

    /// <summary>What the accrual is worth — the quote, which is the only figure always present.</summary>
    public decimal AccruedAmount { get; set; }
    public string  Currency      { get; set; } = string.Empty;

    /// <summary>See <see cref="Domain.RateSource"/> — where the quote came from, stored as its code.</summary>
    public string? QuoteSource { get; set; }

    /// <summary>
    /// What the carrier itself said when it accepted the booking, where it said anything. Null for
    /// a manual booking and for any carrier whose adapter returns no cost.
    /// </summary>
    public decimal? BookedAmount   { get; set; }
    public string?  BookedCurrency { get; set; }

    // ── What the carrier actually billed (T-55) ───────────────────────────────

    /// <summary>
    /// The sum of every invoice line matched to this consignment. The third leg, completing the
    /// row: quoted, agreed at booking, and billed — all beside each other, which is the whole
    /// reason the first two were copied here.
    /// </summary>
    public decimal? InvoicedAmount { get; set; }

    /// <summary>
    /// Billed minus expected, where expected is what the carrier agreed at booking if it said
    /// anything, and the quote otherwise. Positive means overcharged.
    /// </summary>
    public decimal? VarianceAmount { get; set; }

    /// <summary>See <see cref="Domain.VarianceReason"/>. Stored as its code. Null within tolerance.</summary>
    public string? VarianceReason { get; set; }

    /// <summary>The evidence behind the reason — which weights, which surcharge codes.</summary>
    public string? VarianceNote { get; set; }

    /// <summary>The bill that settled it. Null until matched.</summary>
    public int?            CarrierInvoiceId { get; set; }
    public CarrierInvoice? CarrierInvoice   { get; set; }

    public DateTime? MatchedAt { get; set; }

    // ── Where it stands ───────────────────────────────────────────────────────

    /// <summary>See <see cref="FreightAccrualStatus"/>. Stored as its code.</summary>
    public string Status { get; set; } = LogisticsCode.Of(FreightAccrualStatus.Accrued);

    public DateTime AccruedAt { get; set; }
    public int      AccruedBy { get; set; }

    /// <summary>
    /// When the liability stopped being an estimate — because an invoice settled it, or because
    /// somebody reversed it. Null while it is still outstanding.
    /// </summary>
    public DateTime? ReleasedAt { get; set; }

    /// <summary>Required when reversing: an accrual that vanishes without a reason is a hole in a ledger.</summary>
    public string? ReleaseReason { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
