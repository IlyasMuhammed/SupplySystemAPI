using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Rating;

/// <summary>One parcel's measurements, in centimetres and kilograms. Any of them may be missing.</summary>
internal readonly record struct ParcelMeasurements(
    decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, decimal? GrossWeightKg);

/// <summary>
/// The terms of the carrier service a parcel is being rated under. Every one is optional, and the
/// all-null value is what a consignment naming a service nobody has configured gets — which rates
/// on actual weight and says so, rather than refusing.
/// </summary>
internal readonly record struct ServiceWeightTerms(
    int?     DimDivisor,
    decimal? MinimumChargeableKg,
    decimal? WeightRoundingKg,
    decimal? MaxWeightKgPerPackage,
    decimal? MaxLengthCm,
    decimal? MaxLengthPlusGirthCm)
{
    /// <summary>No service resolved: actual weight, no floor, no rounding, no limits.</summary>
    internal static ServiceWeightTerms None => default;

    internal static ServiceWeightTerms From(CarrierService? service) =>
        service is null
            ? None
            : new ServiceWeightTerms(
                service.DimDivisor, service.MinimumChargeableKg, service.WeightRoundingKg,
                service.MaxWeightKgPerPackage, service.MaxLengthCm, service.MaxLengthPlusGirthCm);
}

/// <summary>
/// What one parcel is charged on, and why. <see cref="ChargeableKg"/> is null only when the parcel
/// has neither a weight nor a usable set of dimensions.
/// </summary>
internal sealed record ChargeableWeightResult(
    decimal?              ActualKg,
    decimal?              VolumetricKg,
    decimal?              ChargeableKg,
    ChargeableWeightBasis Basis,
    int?                  DivisorUsed,
    decimal?              LongestSideCm,
    decimal?              LengthPlusGirthCm,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Chargeable weight: <c>max(actual, L×W×H ÷ divisor)</c>, rounded up to the service's step and
/// floored at its minimum.
/// <para>
/// <b>Why this is a pure function.</b> It is the number every tariff in Phase 3 multiplies and every
/// carrier invoice in Phase 4 is checked against, so it has to be testable on its own, without a
/// database, a consignment or a carrier. Everything it needs is in its two arguments.
/// </para>
/// <para>
/// <b>It never throws and never refuses.</b> A parcel nobody measured is a real parcel sitting on a
/// real dock; the answer is "it cannot be rated, and here is why", not an exception that takes a
/// screen down. Every doubt comes back as a warning attached to the figure it affects.
/// </para>
/// </summary>
internal static class ChargeableWeight
{
    /// <summary>The weight columns are decimal(18,3), so every figure is settled at three places.</summary>
    private const int WeightDecimals = 3;

    internal static ChargeableWeightResult ForParcel(ParcelMeasurements parcel, ServiceWeightTerms terms)
    {
        var warnings = new List<string>();

        // A zero or negative measurement is not a measurement. Treating it as one would produce a
        // volumetric weight of zero, which looks like a cheap parcel rather than an unmeasured one.
        var length = Measured(parcel.LengthCm);
        var width  = Measured(parcel.WidthCm);
        var height = Measured(parcel.HeightCm);
        var actual = Measured(parcel.GrossWeightKg);

        var dims        = new[] { length, width, height };
        var hasAllDims  = dims.All(d => d is not null);
        var hasSomeDims = dims.Any(d => d is not null);

        decimal? longest = null, lengthPlusGirth = null;

        if (hasAllDims)
        {
            // Carriers measure the longest side, whichever column it was typed into — a 30×30×120
            // carton is 120 cm long even when "LengthCm" says 30. Girth is the distance around the
            // parcel, so length plus girth is longest + 2×(the other two).
            var sides = new[] { length!.Value, width!.Value, height!.Value };
            Array.Sort(sides);

            longest         = sides[2];
            lengthPlusGirth = sides[2] + 2 * (sides[0] + sides[1]);
        }
        else if (hasSomeDims)
        {
            warnings.Add(
                "Only some of this package's dimensions are recorded, so its volume could not be worked out.");
        }

        // ── Volumetric ────────────────────────────────────────────────────────

        decimal? volumetric = null;
        int?     divisorUsed = null;

        if (terms.DimDivisor is { } divisor and > 0)
        {
            if (hasAllDims)
            {
                volumetric  = Round(length!.Value * width!.Value * height!.Value / divisor);
                divisorUsed = divisor;
            }
            else if (!hasSomeDims)
            {
                warnings.Add(
                    "This service charges on volume and this package has no dimensions, so it is being " +
                    "rated on actual weight alone and may be under-quoted.");
            }
        }

        // ── Which one wins ────────────────────────────────────────────────────

        decimal? chargeable;
        ChargeableWeightBasis basis;

        if (actual is null && volumetric is null)
        {
            chargeable = null;
            basis      = ChargeableWeightBasis.Unknown;
            warnings.Add(
                "This package has neither a weight nor a full set of dimensions, so there is nothing to " +
                "charge on. Weigh or measure it before rating the consignment.");
        }
        else if (volumetric is { } v && (actual is null || v > actual))
        {
            chargeable = v;
            basis      = ChargeableWeightBasis.Volumetric;

            if (actual is null)
                warnings.Add(
                    "No weight is recorded for this package, so it is being charged on volume alone. If it " +
                    "is denser than the service assumes, the carrier will bill more.");
        }
        else
        {
            chargeable = actual;
            basis      = ChargeableWeightBasis.Actual;
        }

        // ── Rounding, then the floor ──────────────────────────────────────────

        if (chargeable is { } figure)
        {
            if (terms.WeightRoundingKg is { } step and > 0)
                chargeable = Round(Math.Ceiling(figure / step) * step);

            // The minimum is applied last because it is a floor on what is billed — the rounded
            // figure — and because the carrier's stated minimum is itself the figure it charges.
            // Rounding afterwards instead would quote a 0.7 kg minimum at 1 kg on a half-kilo step,
            // which is a number the carrier never asked for.
            if (terms.MinimumChargeableKg is { } minimum && chargeable < minimum)
            {
                chargeable = Round(minimum);
                basis      = ChargeableWeightBasis.Minimum;
            }
        }

        // ── What the service will not carry ───────────────────────────────────
        //
        // Warnings, not refusals. These columns have existed since T-43 and nothing has read them;
        // a parcel over a limit is something a person needs told before booking, but refusing here
        // would make an unrateable consignment unviewable too.

        if (terms.MaxWeightKgPerPackage is { } maxWeight && actual is { } weighed && weighed > maxWeight)
            warnings.Add(
                $"This package weighs {Kg(weighed)} kg and the service will not carry more than " +
                $"{Kg(maxWeight)} kg in one piece.");

        if (terms.MaxLengthCm is { } maxLength && longest is { } longestSide && longestSide > maxLength)
            warnings.Add(
                $"This package's longest side is {Cm(longestSide)} cm and the service's limit is " +
                $"{Cm(maxLength)} cm.");

        if (terms.MaxLengthPlusGirthCm is { } maxGirth && lengthPlusGirth is { } measured && measured > maxGirth)
            warnings.Add(
                $"This package measures {Cm(measured)} cm in length plus girth and the service's limit is " +
                $"{Cm(maxGirth)} cm. Long thin parcels pass every single-dimension limit and are still refused.");

        return new ChargeableWeightResult(
            ActualKg:          actual,
            VolumetricKg:      volumetric,
            ChargeableKg:      chargeable,
            Basis:             basis,
            DivisorUsed:       divisorUsed,
            LongestSideCm:     longest,
            LengthPlusGirthCm: lengthPlusGirth,
            Warnings:          warnings);
    }

    private static decimal? Measured(decimal? value) => value is > 0 ? value : null;

    private static decimal Round(decimal value) =>
        Math.Round(value, WeightDecimals, MidpointRounding.AwayFromZero);

    private static string Kg(decimal value) => value.ToString("0.###");
    private static string Cm(decimal value) => value.ToString("0.##");
}
