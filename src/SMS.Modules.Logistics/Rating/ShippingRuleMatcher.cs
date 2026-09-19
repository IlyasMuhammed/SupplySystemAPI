using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Rating;

/// <summary>Everything about a consignment that a shipping rule can be written against.</summary>
internal readonly record struct ShipmentFacts(
    decimal  ChargeableWeightKg,
    decimal? DeclaredValue,
    bool     IsHazardous,
    bool     HasCod,
    string?  OriginCountryIso,
    string?  OriginPostcode,
    string?  DestinationCountryIso,
    string?  DestinationPostcode);

/// <param name="Matched">Every condition the rule stated, and which held.</param>
internal sealed record RuleMatch(bool Matched, string Explanation);

/// <summary>
/// Whether a rule applies, and — either way — <b>why</b>.
/// <para>
/// A pure function, like the rest of the rating arithmetic. The explanation is not decoration: a
/// rule engine that says only "rule 3 fired" leaves somebody reading conditions in a database to
/// work out why rule 2 did not, at the moment a parcel has gone out on the wrong carrier.
/// </para>
/// </summary>
internal static class ShippingRuleMatcher
{
    internal static RuleMatch Evaluate(ShippingRule rule, ShipmentFacts facts)
    {
        var held   = new List<string>();
        var failed = new List<string>();

        void Check(bool condition, string stated) => (condition ? held : failed).Add(stated);

        if (rule.MinChargeableWeightKg is { } min)
            Check(facts.ChargeableWeightKg >= min,
                  $"weight {Kg(facts.ChargeableWeightKg)} kg is at least {Kg(min)} kg");

        if (rule.MaxChargeableWeightKg is { } max)
            Check(facts.ChargeableWeightKg <= max,
                  $"weight {Kg(facts.ChargeableWeightKg)} kg is at most {Kg(max)} kg");

        if (rule.MinDeclaredValue is { } minValue)
            Check(facts.DeclaredValue is { } v && v >= minValue,
                  $"declared value {Money(facts.DeclaredValue)} is at least {Money(minValue)}");

        if (rule.MaxDeclaredValue is { } maxValue)
            Check(facts.DeclaredValue is { } v && v <= maxValue,
                  $"declared value {Money(facts.DeclaredValue)} is at most {Money(maxValue)}");

        if (!string.IsNullOrWhiteSpace(rule.OriginCountryIso))
            Check(Same(rule.OriginCountryIso, facts.OriginCountryIso),
                  $"origin country is {rule.OriginCountryIso}");

        if (!string.IsNullOrWhiteSpace(rule.DestinationCountryIso))
            Check(Same(rule.DestinationCountryIso, facts.DestinationCountryIso),
                  $"destination country is {rule.DestinationCountryIso}");

        if (!string.IsNullOrWhiteSpace(rule.OriginPostcodePrefix))
            Check(StartsWith(facts.OriginPostcode, rule.OriginPostcodePrefix),
                  $"origin postcode starts {rule.OriginPostcodePrefix}");

        if (!string.IsNullOrWhiteSpace(rule.DestinationPostcodePrefix))
            Check(StartsWith(facts.DestinationPostcode, rule.DestinationPostcodePrefix),
                  $"destination postcode starts {rule.DestinationPostcodePrefix}");

        if (rule.AppliesToHazardous is { } hazardous)
            Check(facts.IsHazardous == hazardous,
                  hazardous ? "the goods are hazardous" : "the goods are not hazardous");

        if (rule.AppliesToCod is { } cod)
            Check(facts.HasCod == cod,
                  cod ? "there is cash to collect" : "there is no cash to collect");

        if (held.Count == 0 && failed.Count == 0)
            return new RuleMatch(true, "It states no conditions, so it applies to everything.");

        return failed.Count == 0
            ? new RuleMatch(true,  $"Every condition holds: {Join(held)}.")
            : new RuleMatch(false, $"Does not apply: {Join(failed)} — not true of this consignment.");
    }

    private static bool Same(string? required, string? actual) =>
        string.Equals(required?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An address with no postcode cannot satisfy a rule that asks for one — the same judgement the
    /// rate card's lanes make, and for the same reason: the alternative is matching a rule nobody
    /// checked the consignment belonged to.
    /// </summary>
    private static bool StartsWith(string? actual, string? prefix) =>
        !string.IsNullOrWhiteSpace(actual)
     && actual.Trim().StartsWith(prefix!.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Join(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join("; ", parts);

    private static string Kg(decimal value)    => value.ToString("0.###");
    private static string Money(decimal? value) => value?.ToString("N2") ?? "unknown";
}
