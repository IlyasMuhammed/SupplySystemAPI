using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Models;

namespace SMS.Modules.Logistics.Rating;

/// <summary>What a rate card was asked to price.</summary>
/// <param name="ServiceCode">
/// The service to price. A card written for it wins; failing that the carrier's general card applies.
/// </param>
/// <param name="ChargeableWeightKg">
/// From T-44 — the figure the carrier bills on, not the figure on the scale.
/// </param>
public sealed record RateCardQuoteRequest(
    Guid     CarrierUuid,
    string?  ServiceCode,
    decimal  ChargeableWeightKg,
    string?  OriginCountryIso,
    string?  OriginPostcode,
    string?  DestinationCountryIso,
    string?  DestinationPostcode,
    decimal? CodAmount   = null,
    int?     TransitDays = null,
    DateTime? ShipDate   = null);

/// <summary>
/// Why a card could not price something. Reported rather than swallowed: "no price" and "priced at
/// nothing" look identical on a screen and mean opposite things.
/// </summary>
public enum RateCardQuoteStatus
{
    Quoted,

    /// <summary>The carrier has no card in effect on that date.</summary>
    NoCard,

    /// <summary>A card is in effect, but no lane on it covers this origin and destination.</summary>
    NoLane,

    /// <summary>A lane matched, but it has no weight break at or below this weight.</summary>
    NoBreak,

    /// <summary>The carrier itself is not known.</summary>
    NoCarrier
}

/// <param name="Option">
/// The price, in the same shape a carrier's own quote comes back in — so a caller pricing from a
/// card and a caller pricing from an API are the same caller. Null unless <c>Quoted</c>.
/// </param>
public sealed record RateCardQuote(
    RateCardQuoteStatus Status,
    CourierRateOption?  Option      = null,
    string?             CardName    = null,
    Guid?               CardUuid    = null,
    string?             LaneName    = null,
    string?             Explanation = null)
{
    public bool Quoted => Status == RateCardQuoteStatus.Quoted;
}

/// <summary>
/// Local tariffs: the carriage price for carriers that will not quote, and the figure a carrier's
/// own quote gets checked against for the ones that will (decision G9).
/// </summary>
public interface IRateCardService
{
    Task<Guid> CreateAsync(CreateRateCardRequest req, int userId);
    Task<IReadOnlyList<RateCardModel>> GetForCarrierAsync(Guid carrierUuid, DateTime? on = null);
    Task<RateCardModel?> GetByUuidAsync(Guid uuid, DateTime? on = null);
    Task<bool> PatchAsync(Guid uuid, PatchRateCardRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);

    Task<RateCardQuote> QuoteAsync(RateCardQuoteRequest req, CancellationToken ct = default);
}

internal sealed class RateCardService : IRateCardService
{
    private readonly IRateCardRepository _repo;
    public RateCardService(IRateCardRepository repo) => _repo = repo;

    public Task<Guid> CreateAsync(CreateRateCardRequest req, int userId) => _repo.CreateAsync(req, userId);

    public Task<IReadOnlyList<RateCardModel>> GetForCarrierAsync(Guid carrierUuid, DateTime? on = null) =>
        _repo.GetForCarrierAsync(carrierUuid, on);

    public Task<RateCardModel?> GetByUuidAsync(Guid uuid, DateTime? on = null) => _repo.GetByUuidAsync(uuid, on);

    public Task<bool> PatchAsync(Guid uuid, PatchRateCardRequest req, int userId) =>
        _repo.PatchAsync(uuid, req, userId);

    public Task<bool> DeleteAsync(Guid uuid, int userId) => _repo.DeleteAsync(uuid, userId);

    public async Task<RateCardQuote> QuoteAsync(RateCardQuoteRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var carrierId = await _repo.FindCarrierIdAsync(req.CarrierUuid, ct);

        if (carrierId is null)
            return new RateCardQuote(RateCardQuoteStatus.NoCarrier,
                Explanation: "That carrier does not exist.");

        var shipDate = req.ShipDate ?? DateTime.UtcNow;

        var card = await _repo.ResolveAsync(carrierId.Value, req.ServiceCode, shipDate, ct);

        if (card is null)
            return new RateCardQuote(RateCardQuoteStatus.NoCard,
                Explanation: $"No rate card is in effect for this carrier on {shipDate:yyyy-MM-dd}"
                           + (string.IsNullOrWhiteSpace(req.ServiceCode)
                               ? "."
                               : $", for service '{req.ServiceCode}' or for its services generally."));

        var laneKey = new RateLaneKey(
            req.OriginCountryIso, req.OriginPostcode,
            req.DestinationCountryIso, req.DestinationPostcode);

        var lane = RateCardPricer.BestLane(card.Lanes, laneKey);

        if (lane is null)
            return new RateCardQuote(RateCardQuoteStatus.NoLane, CardName: card.Name, CardUuid: card.UUID,
                Explanation: $"'{card.Name}' has no lane covering "
                           + $"{Describe(req.OriginCountryIso, req.OriginPostcode)} to "
                           + $"{Describe(req.DestinationCountryIso, req.DestinationPostcode)}.");

        var option = RateCardPricer.Price(
            card, laneKey, req.ChargeableWeightKg, req.CodAmount,
            serviceCode: req.ServiceCode ?? card.ServiceCode ?? string.Empty,
            serviceName: card.Name,
            transitDays: req.TransitDays,
            shipDate:    shipDate);

        if (option is null)
            return new RateCardQuote(RateCardQuoteStatus.NoBreak, CardName: card.Name, CardUuid: card.UUID,
                LaneName: lane.Name,
                Explanation: $"Lane '{lane.Name ?? "unnamed"}' on '{card.Name}' has no rate at "
                           + $"{req.ChargeableWeightKg:0.###} kg.");

        return new RateCardQuote(
            RateCardQuoteStatus.Quoted, option, card.Name, card.UUID, lane.Name,
            $"Priced from '{card.Name}'"
          + (string.IsNullOrWhiteSpace(lane.Name) ? "" : $", lane '{lane.Name}'") + ".");
    }

    private static string Describe(string? country, string? postcode) =>
        (country, postcode) switch
        {
            (null or "", null or "") => "an address with no country or postcode",
            (null or "", _)          => $"postcode {postcode}",
            (_, null or "")          => country!,
            _                        => $"{country} {postcode}"
        };
}
