using SMS.Modules.Logistics.Rating;

namespace SMS.Modules.Logistics.Couriers.Simulator;

/// <summary>One product the simulator sells, and the terms it prices on.</summary>
internal sealed record SimulatorService(
    string  Code,
    string  Name,
    decimal RatePerKg,
    decimal MinimumCharge,
    int     TransitDays,
    bool    IsGuaranteed);

/// <summary>
/// What the simulator charges.
/// <para>
/// <b>Deliberately simple, and deliberately real in shape.</b> A fake carrier inventing a convincing
/// tariff would encourage somebody to read meaning into the numbers; one with no shape at all would
/// fail to exercise the things that actually go wrong — a quote that does not reconcile to its own
/// surcharges, a total that ignores dimensional weight, a service list with a duplicate in it.
/// </para>
/// <para>
/// It prices on <b>chargeable weight</b> through the same T-44 calculator the rest of the module
/// uses, applying its own divisor and rounding step, because that is what a carrier does: the
/// divisor belongs to the product being sold, and the simulator sells three.
/// </para>
/// </summary>
internal static class SimulatorTariff
{
    internal const string Currency = "PKR";

    /// <summary>The simulator's divisor and rounding. Distinct from any scenario code.</summary>
    private static readonly ServiceWeightTerms Terms = new(
        DimDivisor:            5000,
        MinimumChargeableKg:   0.5m,
        WeightRoundingKg:      0.5m,
        MaxWeightKgPerPackage: null,
        MaxLengthCm:           null,
        MaxLengthPlusGirthCm:  null);

    /// <summary>Fuel is a percentage of the base rate everywhere in this industry.</summary>
    private const decimal FuelSurchargeRate = 0.12m;

    private const decimal CodFeeRate    = 0.015m;
    private const decimal CodFeeMinimum = 150m;

    /// <summary>
    /// Ordered slowest to fastest, so a rate-shopping screen shows the cheapest first without
    /// having to know what these codes mean.
    /// </summary>
    internal static readonly IReadOnlyList<SimulatorService> Services =
    [
        new("SIM-ECONOMY",   "Simulator Economy",   90m,  400m, 4, IsGuaranteed: false),
        new("SIM-EXPRESS",   "Simulator Express",  120m,  600m, 2, IsGuaranteed: false),
        new("SIM-OVERNIGHT", "Simulator Overnight", 190m, 950m, 1, IsGuaranteed: true)
    ];

    /// <summary>The one a booking is costed against when nothing named a service.</summary>
    internal static SimulatorService Default => Services[1];

    internal static bool TryResolve(string? serviceCode, out SimulatorService service)
    {
        service = Default;

        if (string.IsNullOrWhiteSpace(serviceCode)) return false;

        var code = serviceCode.Trim();
        var match = Services.FirstOrDefault(
            s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));

        if (match is null) return false;

        service = match;
        return true;
    }

    /// <summary>
    /// The chargeable weight of a whole consignment: each piece rated on its own and the results
    /// summed, never the consolidated dimensions rated once. Carriers bill piece by piece.
    /// </summary>
    internal static decimal ChargeableWeightOf(IReadOnlyList<CourierPackage> packages) =>
        packages.Sum(p => ChargeableWeight
            .ForParcel(new ParcelMeasurements(p.LengthCm, p.WidthCm, p.HeightCm, p.GrossWeightKg), Terms)
            .ChargeableKg ?? 0m);

    /// <summary>
    /// A quote whose surcharges add up to its total — which the contract suite checks, and which is
    /// the invariant Phase 4's three-way match will lean on.
    /// </summary>
    internal static CourierRateOption Quote(
        SimulatorService service,
        IReadOnlyList<CourierPackage> packages,
        decimal? codAmount,
        DateTime shipDate)
    {
        var chargeable = ChargeableWeightOf(packages);
        var baseAmount = Round(Math.Max(service.MinimumCharge, chargeable * service.RatePerKg));

        var surcharges = new List<CourierSurcharge>
        {
            new("FUEL", "Fuel surcharge", Round(baseAmount * FuelSurchargeRate))
        };

        if (codAmount is > 0)
            surcharges.Add(new CourierSurcharge(
                "COD", "Cash-on-delivery handling",
                Round(Math.Max(CodFeeMinimum, codAmount.Value * CodFeeRate))));

        return new CourierRateOption(
            ServiceCode:        service.Code,
            ServiceName:        service.Name,
            TotalAmount:        Round(baseAmount + surcharges.Sum(s => s.Amount)),
            Currency:           Currency,
            BaseAmount:         baseAmount,
            Surcharges:         surcharges,
            ChargeableWeightKg: chargeable,
            // Calendar days, not working ones. A simulator that modelled public holidays would be
            // modelling the wrong thing.
            EstimatedDelivery:  shipDate.Date.AddDays(service.TransitDays),
            TransitDays:        service.TransitDays,
            IsGuaranteed:       service.IsGuaranteed);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
