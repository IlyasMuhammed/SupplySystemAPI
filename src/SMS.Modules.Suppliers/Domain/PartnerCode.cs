using System.Reflection;

namespace SMS.Modules.Suppliers.Domain;

/// <summary>
/// Converts between <see cref="PartnerType"/> and the string it persists as, and computes it from
/// the four capability flags.
/// <para>
/// Parsing is strict on purpose, matching SMS.Modules.Logistics's LogisticsCode: an unknown code
/// is always a failure, never a guess.
/// </para>
/// </summary>
internal static class PartnerCode
{
    private static readonly IReadOnlyDictionary<PartnerType, string> ToCode;
    private static readonly IReadOnlyDictionary<string, PartnerType> FromCode;

    static PartnerCode()
    {
        var toCode   = new Dictionary<PartnerType, string>();
        var fromCode = new Dictionary<string, PartnerType>(StringComparer.Ordinal);

        foreach (var field in typeof(PartnerType).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<CodeAttribute>()
                ?? throw new InvalidOperationException(
                    $"PartnerType.{field.Name} has no [Code]. Every member of a persisted enum " +
                    "must declare the string it is stored as.");

            var member = (PartnerType)field.GetValue(null)!;
            fromCode[attribute.Value] = member;
            toCode[member] = attribute.Value;
        }

        ToCode   = toCode;
        FromCode = fromCode;
    }

    /// <summary>The string <paramref name="value"/> persists as.</summary>
    internal static string Of(PartnerType value) =>
        ToCode.TryGetValue(value, out var code)
            ? code
            : throw new InvalidOperationException($"PartnerType has no code for '{value}'.");

    /// <summary>Parses a persisted code. False for null, blank or unknown input — never guesses.</summary>
    internal static bool TryParse(string? code, out PartnerType value)
    {
        if (!string.IsNullOrWhiteSpace(code) && FromCode.TryGetValue(code, out value))
            return true;

        value = default;
        return false;
    }

    /// <summary>
    /// The <see cref="PartnerType"/> the four capability flags describe — the single source of
    /// truth for how flags map to a type label, used both to validate a stored/incoming
    /// combination and (in P1-04) to compute the column on save.
    /// </summary>
    internal static PartnerType FromFlags(bool isVendor, bool isCustomer, bool isCarrier, bool isServiceProvider) =>
        (isVendor, isCustomer, isCarrier, isServiceProvider) switch
        {
            (true,  false, false, false) => PartnerType.Vendor,
            (false, true,  false, false) => PartnerType.Customer,
            (false, false, true,  false) => PartnerType.Carrier,
            (false, false, false, true)  => PartnerType.ServiceProvider,
            (true,  true,  false, false) => PartnerType.Both,
            (true,  false, true,  false) => PartnerType.VendorCarrier,
            (true,  false, false, true)  => PartnerType.VendorService,
            (true,  true,  true,  true)  => PartnerType.FullPartner,
            _ => throw new InvalidOperationException(
                "This combination of vendor/customer/carrier/service-provider flags has no " +
                $"PartnerType (is_vendor={isVendor}, is_customer={isCustomer}, is_carrier={isCarrier}, " +
                $"is_service_provider={isServiceProvider}). The FSD's own table (§1.2) only defines " +
                "these eight combinations.")
        };
}
