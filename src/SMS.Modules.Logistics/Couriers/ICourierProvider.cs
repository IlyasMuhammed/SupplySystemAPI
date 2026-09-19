namespace SMS.Modules.Logistics.Couriers;

/// <summary>
/// How a carrier call ended.
/// <para>
/// <b>The distinction this enum exists for:</b> a carrier saying "no" is an <em>answer</em>, and a
/// call that never completed is not. The first is final — retrying the identical request gets the
/// identical refusal — while the second may have created a real parcel that nobody has a record
/// of. Collapsing them into one failure, or throwing for both, is how a booking retry ends up
/// putting two labels on one box.
/// </para>
/// </summary>
public enum CourierOutcome
{
    /// <summary>The carrier accepted it. There is an airway bill.</summary>
    Succeeded,

    /// <summary>
    /// The carrier answered, and the answer was no — unserviceable postcode, oversize parcel,
    /// account suspended. Retrying the same request is pointless; something has to change first.
    /// </summary>
    Refused,

    /// <summary>
    /// The call did not resolve: a timeout, a 500, a dropped connection. <b>Whether the carrier
    /// created anything is unknown</b>, which is what the idempotency ledger (T-36) exists to
    /// settle. Never treat this as "it did not happen".
    /// </summary>
    Failed,

    /// <summary>The provider does not offer this operation. See <see cref="CourierCapabilities"/>.</summary>
    Unsupported
}

/// <param name="Line1">Free-form lines; adapters that need structured fields parse or reject.</param>
public sealed record CourierAddress(
    string?  ContactName,
    string?  ContactPhone,
    string?  ContactEmail,
    string?  Line1,
    string?  Line2,
    string?  City,
    string?  State,
    string?  PostalCode,
    string?  CountryIsoCode);

/// <summary>One handling unit, as a carrier needs to see it. Centimetres and kilograms.</summary>
public sealed record CourierPackage(
    string   Barcode,
    string   PackageType,
    decimal? LengthCm,
    decimal? WidthCm,
    decimal? HeightCm,
    decimal? GrossWeightKg,
    decimal? DeclaredValue);

/// <param name="IdempotencyKey">
/// Supplied by the caller, never generated here. It is written to the command ledger <em>before</em>
/// the call, so a retry can ask "did this already happen?" — which only works if the same key is
/// presented again, and an adapter that invented its own would defeat that.
/// </param>
/// <param name="Credentials">
/// Whatever the carrier account holds, already decrypted, as opaque key–value pairs. The contract
/// stays free of any one carrier's notion of an account number or API key.
/// </param>
/// <param name="SuppliedAwbNumber">
/// An airway bill the operator already holds, rather than one the adapter should obtain.
/// <para>
/// This is how the manual path fits the same contract: a carrier with no API is booked by a person
/// — a phone call, a portal, a paper consignment note — who then keys the number in. It is not
/// only for manual carriers, though: a consignment booked on an API carrier's own web portal
/// arrives the same way, and an adapter that can record it saves a second real booking.
/// </para>
/// <para>
/// Adapters that obtain their own numbers ignore it.
/// </para>
/// </param>
public sealed record CourierBookingRequest(
    string                       IdempotencyKey,
    string                       ConsignmentNumber,
    string?                      ServiceCode,
    CourierAddress               ShipFrom,
    CourierAddress               ShipTo,
    IReadOnlyList<CourierPackage> Packages,
    string                       FreightTerms,
    decimal?                     CodAmount,
    string?                      CodCurrency,
    DateTime?                    PickupWindowStart,
    DateTime?                    PickupWindowEnd,
    IReadOnlyList<string>        ReferenceNumbers,
    IReadOnlyDictionary<string, string> Credentials,
    string?                      SuppliedAwbNumber = null);

/// <param name="RawResponse">
/// Kept for diagnostics. When a carrier disputes a booking months later, the parsed fields are
/// never the thing that settles it.
/// </param>
public sealed record CourierBookingResult(
    CourierOutcome Outcome,
    string         ProviderKey,
    string?        AwbNumber          = null,
    string?        CarrierReference   = null,
    string?        TrackingUrl        = null,
    decimal?       Cost               = null,
    string?        CostCurrency       = null,
    CourierLabel?  Label              = null,
    string?        Message            = null,
    string?        CarrierErrorCode   = null,
    string?        RawResponse        = null)
{
    public bool Succeeded => Outcome == CourierOutcome.Succeeded;
}

/// <summary>
/// What a carriage would cost, asked before anything is committed.
/// <para>
/// <b>There is deliberately no idempotency key.</b> Rating is a read: asking twice costs nothing and
/// creates nothing, so it does not go through the command ledger (T-36) and must never be made to.
/// An adapter whose rate call has a side effect has misunderstood this method.
/// </para>
/// </summary>
/// <param name="ServiceCode">
/// The one service to price, or <b>null to price everything the account can offer</b> — which is
/// what rate shopping (T-48) asks for.
/// </param>
/// <param name="ShipDate">
/// When the goods would be handed over. Rates and transit times are dated: a Friday collection and a
/// Monday one do not arrive on the same day, and some surcharges are seasonal. Null means today.
/// </param>
public sealed record CourierRateRequest(
    string                        ConsignmentNumber,
    string?                       ServiceCode,
    CourierAddress                ShipFrom,
    CourierAddress                ShipTo,
    IReadOnlyList<CourierPackage> Packages,
    string                        FreightTerms,
    decimal?                      CodAmount,
    string?                       CodCurrency,
    DateTime?                     ShipDate,
    IReadOnlyDictionary<string, string> Credentials);

/// <summary>
/// One line of a carrier's quote — fuel, remote area, residential delivery, COD handling.
/// <para>
/// Kept apart from the total rather than folded into it, because Phase 4's three-way match compares
/// a carrier's invoice against what was quoted, and an invoice arrives itemised. A total on its own
/// can only ever say <em>that</em> the two disagree, never where.
/// </para>
/// </summary>
public sealed record CourierSurcharge(string Code, string? Description, decimal Amount);

/// <summary>
/// One priced option. <paramref name="TotalAmount"/> is what would actually be billed — every
/// surcharge included.
/// </summary>
/// <param name="ChargeableWeightKg">
/// The weight the carrier priced on, when it says. Worth having even though T-44 works it out
/// locally: where the two disagree, the carrier's is the one that ends up on the invoice, and
/// knowing which divisor it really used is how that gets settled.
/// </param>
public sealed record CourierRateOption(
    string    ServiceCode,
    string?   ServiceName,
    decimal   TotalAmount,
    string    Currency,
    decimal?  BaseAmount         = null,
    IReadOnlyList<CourierSurcharge>? Surcharges = null,
    decimal?  ChargeableWeightKg = null,
    DateTime? EstimatedDelivery  = null,
    int?      TransitDays        = null,
    bool      IsGuaranteed       = false);

/// <param name="Options">
/// Empty on anything but success. One entry per service — never two for the same service code, which
/// would make "which option won" depend on row order.
/// </param>
public sealed record CourierRateResult(
    CourierOutcome                   Outcome,
    string                           ProviderKey,
    IReadOnlyList<CourierRateOption> Options,
    string?                          Message     = null,
    string?                          RawResponse = null)
{
    public bool Succeeded => Outcome == CourierOutcome.Succeeded;
}

/// <param name="ContentType">A real media type — <c>application/pdf</c>, <c>image/png</c>.</param>
public sealed record CourierLabel(byte[] Content, string ContentType, string? FileName = null);

public sealed record CourierLabelResult(
    CourierOutcome Outcome,
    string         ProviderKey,
    CourierLabel?  Label   = null,
    string?        Message = null);

public sealed record CourierCancelRequest(
    string                       IdempotencyKey,
    string                       ConsignmentNumber,
    string                       AwbNumber,
    IReadOnlyDictionary<string, string> Credentials);

public sealed record CourierCancelResult(
    CourierOutcome Outcome,
    string         ProviderKey,
    string?        Message = null);

/// <param name="Milestone">
/// A <c>TrackingMilestone</c> code. Adapters map the carrier's vocabulary onto ours; a carrier
/// status nothing maps to belongs in <paramref name="CarrierStatus"/> rather than being invented
/// as a milestone.
/// </param>
public sealed record CourierTrackingEvent(
    DateTime  OccurredAt,
    string    Milestone,
    string?   CarrierStatus  = null,
    string?   Description    = null,
    string?   Location       = null,
    string?   SignedBy       = null);

public sealed record CourierTrackingResult(
    CourierOutcome                      Outcome,
    string                              ProviderKey,
    IReadOnlyList<CourierTrackingEvent> Events,
    string?                             Message = null);

/// <param name="BookedAt">
/// When the booking was made, when known (T-40). Real carriers do not need it. It exists so an adapter
/// with no memory of its own — the simulator — can date events stably: tracking is polled repeatedly,
/// and events whose timestamps move between polls would be stored as new every time.
/// </param>
public sealed record CourierTrackingRequest(
    string                       AwbNumber,
    string?                      ConsignmentNumber,
    IReadOnlyDictionary<string, string> Credentials,
    DateTime?                    BookedAt = null);

/// <summary>
/// What a provider can actually do.
/// <para>
/// Declared rather than discovered, so the booking flow can refuse an impossible request — COD
/// through a carrier that does not collect cash — <em>before</em> it goes out, instead of turning
/// it into a refusal the user has to interpret.
/// </para>
/// </summary>
public sealed record CourierCapabilities(
    bool SupportsBooking      = true,
    bool SupportsCancellation = false,
    bool SupportsLabels       = false,
    bool SupportsTracking     = false,
    bool SupportsPickupBooking = false,
    bool SupportsCod          = false,
    bool SupportsMultiPiece   = true,
    /// <summary>True when the carrier itself deduplicates on the idempotency key we send.</summary>
    bool HonoursIdempotencyKey = false,
    /// <summary>
    /// True when the carrier will quote (T-45). Most will not, which is why local rate cards exist
    /// beside this — decision G9 — rather than instead of it.
    /// </summary>
    bool SupportsRating       = false);

/// <summary>
/// One carrier's integration, behind one shape.
/// <para>
/// Everything a carrier can be asked to do lives here so that the booking flow, the retry ledger
/// and the tracking poll are written once rather than once per carrier. The manual path is an
/// implementation of this too (T-33) — a carrier with no API is still a carrier, and giving it a
/// different code path is how the manual case rots.
/// </para>
/// <para>
/// <b>Adapters return outcomes; they do not throw for business answers.</b> A refusal is data. An
/// exception from one of these methods means the call itself came apart, and the caller must treat
/// the carrier's state as unknown rather than unchanged.
/// </para>
/// <para>
/// Every adapter must pass <c>CourierProviderContractTests</c>. That base is the definition of
/// what implementing this interface means; the interface alone only says what compiles.
/// </para>
/// </summary>
public interface ICourierProvider
{
    /// <summary>
    /// Matched against <c>Carrier.ProviderKey</c>. Stable for the life of the adapter — it is
    /// persisted on carrier rows, so renaming one orphans every carrier configured for it.
    /// </summary>
    string Key { get; }

    /// <summary>Shown when a human picks a provider; not used for resolution.</summary>
    string DisplayName { get; }

    CourierCapabilities Capabilities { get; }

    /// <summary>
    /// What the carriage would cost. A read — it commits nothing and must create nothing.
    /// <para>
    /// The one number that is actually true, including surcharges nobody models locally, which is
    /// why it is asked first and a rate card (T-46) is the fallback rather than the other way round.
    /// A quote still cannot gate a screen, so a caller that cannot wait for the network uses the
    /// card instead — but where a carrier will answer, its answer wins.
    /// </para>
    /// </summary>
    Task<CourierRateResult> RateAsync(CourierRateRequest request, CancellationToken ct = default);

    Task<CourierBookingResult> BookAsync(CourierBookingRequest request, CancellationToken ct = default);

    Task<CourierCancelResult> CancelAsync(CourierCancelRequest request, CancellationToken ct = default);

    /// <summary>
    /// Fetches the label for an already-booked consignment. Some carriers return it inline at
    /// booking, in which case this re-fetches the same artefact rather than issuing a new one.
    /// </summary>
    Task<CourierLabelResult> GetLabelAsync(
        string awbNumber, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default);

    /// <summary>Events oldest first. An adapter that cannot order them has not finished the job.</summary>
    Task<CourierTrackingResult> TrackAsync(CourierTrackingRequest request, CancellationToken ct = default);
}
