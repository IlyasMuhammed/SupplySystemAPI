using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Rating;

/// <summary>Where a consignment is going, as far as a lane cares.</summary>
internal readonly record struct RateLaneKey(
    string? OriginCountryIso,
    string? OriginPostcode,
    string? DestinationCountryIso,
    string? DestinationPostcode);

/// <summary>
/// Prices a consignment from a rate card.
/// <para>
/// A pure function, for the same reason <see cref="ChargeableWeight"/> is: it decides money, and
/// money has to be checkable without a database in the way. Everything it needs is in its arguments,
/// and its answer is the <see cref="CourierRateOption"/> shape a carrier's own quote comes back in —
/// so a caller pricing from a card and a caller pricing from an API are the same caller.
/// </para>
/// </summary>
internal static class RateCardPricer
{
    /// <summary>
    /// The lane that matches best, or null when none does.
    /// <para>
    /// <b>Most specific wins.</b> A lane naming both countries and both postcode prefixes beats one
    /// naming only a destination country, which beats the catch-all. Ties cannot happen: two lanes
    /// with identical criteria are refused on write.
    /// </para>
    /// </summary>
    internal static RateCardLane? BestLane(IEnumerable<RateCardLane> lanes, RateLaneKey key) =>
        lanes.Where(l => !l.IsDelete && Matches(l, key))
             .OrderByDescending(Specificity)
             // Deterministic where specificity ties across different criteria — for instance an
             // origin-country lane against a destination-country one.
             .ThenBy(l => l.Id)
             .FirstOrDefault();

    private static bool Matches(RateCardLane lane, RateLaneKey key) =>
        CountryMatches(lane.OriginCountryIso,      key.OriginCountryIso)
     && CountryMatches(lane.DestinationCountryIso, key.DestinationCountryIso)
     && PrefixMatches(lane.OriginPostcodePrefix,      key.OriginPostcode)
     && PrefixMatches(lane.DestinationPostcodePrefix, key.DestinationPostcode);

    /// <summary>A criterion the lane does not state matches anything.</summary>
    private static bool CountryMatches(string? required, string? actual) =>
        string.IsNullOrWhiteSpace(required)
     || string.Equals(required.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool PrefixMatches(string? required, string? actual)
    {
        if (string.IsNullOrWhiteSpace(required)) return true;

        // An address with no postcode cannot satisfy a lane that asks for one. Letting it through
        // would price an unaddressed consignment on a lane nobody checked it belonged to.
        if (string.IsNullOrWhiteSpace(actual)) return false;

        return actual.Trim().StartsWith(required.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int Specificity(RateCardLane lane) =>
        (string.IsNullOrWhiteSpace(lane.OriginCountryIso)          ? 0 : 1)
      + (string.IsNullOrWhiteSpace(lane.DestinationCountryIso)     ? 0 : 1)
      // A postcode prefix is a narrower statement than a country, and a longer prefix narrower
      // still — so a lane for "540" beats one for "54" on the same country.
      + (lane.OriginPostcodePrefix?.Trim().Length      ?? 0) * 2
      + (lane.DestinationPostcodePrefix?.Trim().Length ?? 0) * 2;

    /// <summary>
    /// The break that applies: the highest one whose lower bound the weight reaches. Null when the
    /// lane has no break at or below it, which validation makes impossible for a saved lane and
    /// which is still handled rather than assumed away.
    /// </summary>
    internal static RateCardBreak? BreakFor(IEnumerable<RateCardBreak> breaks, decimal chargeableKg) =>
        breaks.Where(b => !b.IsDelete && b.FromWeightKg <= chargeableKg)
              .OrderByDescending(b => b.FromWeightKg)
              .FirstOrDefault();

    /// <summary>
    /// What the card charges, itemised. Null when no lane or no break matches — which the caller
    /// reports as "this card does not cover this consignment", never as free carriage.
    /// </summary>
    internal static CourierRateOption? Price(
        RateCard card,
        RateLaneKey lane,
        decimal chargeableKg,
        decimal? codAmount,
        string serviceCode,
        string? serviceName,
        int? transitDays,
        DateTime shipDate)
    {
        var matched = BestLane(card.Lanes, lane);
        if (matched is null) return null;

        var applicable = BreakFor(matched.Breaks, chargeableKg);
        if (applicable is null) return null;

        var basis = LogisticsCode.Parse<RateBasis>(applicable.Basis);

        var baseAmount = basis == RateBasis.Flat
            ? applicable.Amount
            : applicable.Amount * chargeableKg;

        // The card's floor is applied to the base rate, before surcharges — a minimum charge is a
        // floor on the carriage, and fuel is charged on top of whatever the carriage came to.
        if (card.MinimumCharge is { } floor && baseAmount < floor)
            baseAmount = floor;

        baseAmount = Round(baseAmount);

        var surcharges = new List<CourierSurcharge>();

        if (card.FuelSurchargePercent is { } fuel and > 0)
            surcharges.Add(new CourierSurcharge(
                "FUEL", $"Fuel surcharge at {fuel:0.##}%", Round(baseAmount * fuel / 100m)));

        if (codAmount is > 0 && (card.CodFeePercent is > 0 || card.CodFeeMinimum is > 0))
        {
            var fee = Math.Max(
                card.CodFeeMinimum ?? 0m,
                codAmount.Value * (card.CodFeePercent ?? 0m) / 100m);

            surcharges.Add(new CourierSurcharge("COD", "Cash-on-delivery handling", Round(fee)));
        }

        return new CourierRateOption(
            ServiceCode:        serviceCode,
            ServiceName:        serviceName,
            TotalAmount:        Round(baseAmount + surcharges.Sum(s => s.Amount)),
            Currency:           card.Currency,
            BaseAmount:         baseAmount,
            Surcharges:         surcharges,
            ChargeableWeightKg: chargeableKg,
            EstimatedDelivery:  transitDays is { } days ? shipDate.Date.AddDays(days) : null,
            TransitDays:        transitDays,
            // A tariff somebody typed in is not a service-level guarantee, whatever it promises.
            IsGuaranteed:       false);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
