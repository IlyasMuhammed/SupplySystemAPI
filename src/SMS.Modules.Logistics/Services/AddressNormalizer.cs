using PhoneNumbers;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

internal sealed record PhoneNormalizationResult(string? E164, string? FailureReason)
{
    internal bool Succeeded => E164 is not null;
}

internal interface IAddressNormalizer
{
    /// <summary>
    /// Fills in the derived fields of an address: the E.164 phone, the resolved city and country
    /// names, and the resulting validation status.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="BadRequestException"/> only for the three fields an address is
    /// meaningless without. Everything else that fails to resolve downgrades the address to
    /// UNVALIDATED and records why — see <see cref="NormalizeAsync"/>.
    /// </remarks>
    Task NormalizeAsync(Address address);
}

/// <summary>
/// Turns a partly-filled address into a saved one, deciding how much of it could be trusted.
/// <para>
/// The governing rule: <b>only a structurally meaningless address is rejected.</b> Line 1, city
/// and country are required because an address without them cannot be delivered to by any means.
/// Everything else — an unparseable phone, a city id that no longer exists, a missing ISO country
/// code — marks the address UNVALIDATED and records the reason, but still saves.
/// </para>
/// <para>
/// That asymmetry is deliberate. The legacy free-text addresses backfilled in T-16 largely will
/// not parse, and refusing them would either block the migration or silently drop delivery
/// history. Booking a courier requires VALID; listing, reporting and backfilling do not.
/// </para>
/// </summary>
internal sealed class AddressNormalizer : IAddressNormalizer
{
    private static readonly PhoneNumberUtil Phone = PhoneNumberUtil.GetInstance();

    private readonly ICityLookupService _cities;

    public AddressNormalizer(ICityLookupService cities) => _cities = cities;

    public async Task NormalizeAsync(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ── Hard requirements ─────────────────────────────────────────────────
        address.Line1       = Require(address.Line1,       "Address line 1");
        address.CityName    = Require(address.CityName,    "City");
        address.CountryName = Require(address.CountryName, "Country");

        address.Line2       = Trim(address.Line2);
        address.State       = Trim(address.State);
        address.PostalCode  = Trim(address.PostalCode);
        address.ContactName = Trim(address.ContactName);
        address.ContactPhone = Trim(address.ContactPhone);
        address.ContactEmail = Trim(address.ContactEmail);

        var problems = new List<string>();

        // ── City ──────────────────────────────────────────────────────────────
        if (address.CityId is { } cityId && cityId != Guid.Empty)
        {
            var city = await _cities.FindAsync(cityId);

            if (city is null)
            {
                // The raw text is kept. A city that was deleted or deactivated in the catalog
                // must not erase where the goods were actually going.
                problems.Add($"City '{address.CityName}' is not in the city catalogue.");
                address.CityId = null;
            }
            else
            {
                address.CityName   = city.CityName;
                address.CountryId  = city.CountryId;
                address.CountryName = city.CountryName;

                // Only adopt the catalogue's country code when it is actually shaped like an
                // ISO-3166 alpha-2 region. It is free text in the Lookups admin screen, so it
                // can be anything — "PAK", "92", or a typo.
                if (address.CountryIsoCode is null && IsIsoRegion(city.CountryCode))
                    address.CountryIsoCode = city.CountryCode!.ToUpperInvariant();
            }
        }

        // ── Country ISO code ──────────────────────────────────────────────────
        address.CountryIsoCode = Trim(address.CountryIsoCode)?.ToUpperInvariant();

        if (address.CountryIsoCode is not null && !IsIsoRegion(address.CountryIsoCode))
        {
            problems.Add($"'{address.CountryIsoCode}' is not a known two-letter country code.");
            address.CountryIsoCode = null;
        }

        // ── Phone ─────────────────────────────────────────────────────────────
        if (address.ContactPhone is not null)
        {
            var result = NormalizePhone(address.ContactPhone, address.CountryIsoCode);

            address.ContactPhoneE164 = result.E164;
            if (!result.Succeeded) problems.Add(result.FailureReason!);
        }

        // ── Status ────────────────────────────────────────────────────────────
        // INVALID is reserved for a carrier's own address-validation API saying no (Phase 2).
        // Anything we merely could not confirm ourselves is UNVALIDATED, not INVALID — the
        // difference matters to whoever works the exception queue.
        if (problems.Count == 0)
        {
            address.ValidationStatus = LogisticsCode.Of(AddressValidationStatus.Valid);
            address.ValidationNotes  = null;
        }
        else
        {
            address.ValidationStatus = LogisticsCode.Of(AddressValidationStatus.Unvalidated);
            address.ValidationNotes  = string.Join(" ", problems);
        }
    }

    /// <summary>
    /// Parses a phone into E.164.
    /// <para>
    /// Unlike the supplier-contact equivalent in SMS.Modules.Suppliers, a fixed line is accepted.
    /// A consignee is often an office reception or a site hut, and refusing those would push
    /// perfectly deliverable addresses into the exception queue. Mobile-only remains the right
    /// rule for supplier WhatsApp, which is why the two are not shared.
    /// </para>
    /// </summary>
    internal static PhoneNormalizationResult NormalizePhone(string? raw, string? isoRegion)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new PhoneNormalizationResult(null, "No contact phone was given.");

        var trimmed = raw.Trim();
        var region  = IsIsoRegion(isoRegion) ? isoRegion!.ToUpperInvariant() : "ZZ";

        if (TryParse(trimmed, region, out var e164))
            return new PhoneNormalizationResult(e164, null);

        // Numbers are routinely stored as bare digits that already carry the country calling code
        // without a leading "+". A "+"-prefixed number is self-describing, so this retry works
        // without needing to know the region at all.
        if (!trimmed.StartsWith('+') && TryParse("+" + trimmed, "ZZ", out e164))
            return new PhoneNormalizationResult(e164, null);

        var hint = region == "ZZ"
            ? " No country code was available to interpret a local number, so it needs the "
            + "international '+' prefix."
            : string.Empty;

        return new PhoneNormalizationResult(
            null, $"Phone '{trimmed}' could not be read as a valid number.{hint}");
    }

    private static bool TryParse(string raw, string region, out string? e164)
    {
        e164 = null;
        try
        {
            var parsed = Phone.Parse(raw, region);
            if (!Phone.IsValidNumber(parsed)) return false;

            e164 = Phone.Format(parsed, PhoneNumberFormat.E164);
            return true;
        }
        catch (NumberParseException)
        {
            return false;
        }
    }

    private static bool IsIsoRegion(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && code.Trim().Length == 2
        && Phone.GetSupportedRegions().Contains(code.Trim().ToUpperInvariant());

    private static string Require(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new BadRequestException($"{fieldName} is required.")
            : value.Trim();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
