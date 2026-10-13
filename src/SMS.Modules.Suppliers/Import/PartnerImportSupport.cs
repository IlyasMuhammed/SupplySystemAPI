using System.Globalization;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Suppliers.Import;

/// <summary>
/// A40 (TSUP) — shared plumbing of the three partner import targets (CUSTOMER, SUPPLIER, SUPPLIER_CONTACT): reading the
/// already-parsed row values, error codes, ISO country normalization and the target-side error collector.
/// </summary>
internal static class PartnerImportSupport
{
    // Error codes (spec codes first, then target-specific UPPER_SNAKE ones; see docs/data-import/ADDENDUM-40-ANALYSIS.md D-11+).
    public const string Required = "REQUIRED", InvalidType = "INVALID_TYPE", MaxLength = "MAX_LENGTH", OutOfRange = "OUT_OF_RANGE",
        InvalidEmail = "INVALID_EMAIL", LookupNotFound = "LOOKUP_NOT_FOUND", DuplicateInFile = "DUPLICATE_IN_FILE",
        WalkInProtected = "WALK_IN_PROTECTED", CodeInUse = "CODE_IN_USE", PartnerRoleConflict = "PARTNER_ROLE_CONFLICT",
        AmbiguousMatch = "AMBIGUOUS_MATCH", CreditNotAllowed = "CREDIT_NOT_ALLOWED", StatusChangeNotAllowed = "STATUS_CHANGE_NOT_ALLOWED";

    public const string StatusImported = "IMPORTED", StatusUpdated = "UPDATED", ActionUpdate = "UPDATE";

    public const int CodeMax = 10, NameMax = 200, EmailMax = 150, PhoneMax = 20, AddressMax = 200, CityMax = 100,
        StateMax = 100, PostalMax = 20, TaxMax = 30, TitleMax = 100, MaxTermsDays = 3650;

    public static string? Get(IReadOnlyDictionary<string, string?> values, string field) =>
        values.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public static string Norm(string value) => value.Trim().ToUpperInvariant();

    /// <summary>The single active PartnerType string for a flag combination, or null when the FSD defines none.</summary>
    public static string? TypeCodeOf(bool vendor, bool customer, bool carrier, bool service)
    {
        try { return PartnerCode.Of(PartnerCode.FromFlags(vendor, customer, carrier, service)); }
        catch (InvalidOperationException) { return null; }
    }

    // ── ISO-3166 countries (country_code: alpha-2, alpha-3 or English name → stored as alpha-2) ──

    private static readonly Lazy<(IReadOnlyDictionary<string, string> Map, IReadOnlyList<string> Codes)> Countries = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo region;
            try { region = new RegionInfo(culture.Name); }
            catch (ArgumentException) { continue; }
            var two = region.TwoLetterISORegionName.ToUpperInvariant();
            if (two.Length != 2 || !two.All(char.IsLetter)) continue;
            map.TryAdd(two, two);
            if (region.ThreeLetterISORegionName.Length == 3) map.TryAdd(region.ThreeLetterISORegionName, two);
            map.TryAdd(region.EnglishName, two);
        }
        return (map, map.Values.Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList());
    });

    /// <summary>ISO alpha-2 for an alpha-2 / alpha-3 code or English country name; null when unknown.</summary>
    public static string? NormalizeCountry(string? value) =>
        value is null ? null : Countries.Value.Map.TryGetValue(value.Trim(), out var iso) ? iso : null;

    public static IReadOnlyList<string> CountryCodes => Countries.Value.Codes;

    /// <summary>Throws the first error of a row (ApplyAsync is all-or-nothing: one bad row fails the batch).</summary>
    public static void ThrowIfAny(int rowNumber, IReadOnlyList<ImportFieldError> errors)
    {
        if (errors.Count == 0) return;
        var e = errors[0];
        throw new BadRequestException($"Row {rowNumber}: {e.Message} ({e.Code}, field {e.Field}).");
    }
}

/// <summary>Collects one row's errors while reading its typed values.</summary>
internal sealed class RowReader
{
    private readonly IReadOnlyDictionary<string, string?> _values;
    public List<ImportFieldError> Errors { get; } = [];

    public RowReader(IReadOnlyDictionary<string, string?> values) => _values = values;

    public string? Raw(string field) => PartnerImportSupport.Get(_values, field);

    public void Error(string field, string code, string message, string? value = null) =>
        Errors.Add(new ImportFieldError(field, code, message, value ?? Raw(field)));

    public string? Text(string field, string display, int max, bool required = false)
    {
        var v = Raw(field);
        if (v is null)
        {
            if (required) Error(field, PartnerImportSupport.Required, $"{display} is required.");
            return null;
        }
        if (v.Length > max) Error(field, PartnerImportSupport.MaxLength, $"{display} must be at most {max} characters.");
        return v;
    }

    public string? Email(string field, string display)
    {
        var v = Text(field, display, PartnerImportSupport.EmailMax);
        if (v is not null && (!v.Contains('@') || v.StartsWith('@') || v.EndsWith('@')))
            Error(field, PartnerImportSupport.InvalidEmail, $"{display} is not a valid email address.");
        return v;
    }

    public bool? Bool(string field, string display)
    {
        var v = Raw(field);
        if (v is null) return null;
        switch (v.ToLowerInvariant())
        {
            case "true" or "yes" or "y" or "1": return true;
            case "false" or "no" or "n" or "0": return false;
            default: Error(field, PartnerImportSupport.InvalidType, $"{display} must be true or false."); return null;
        }
    }

    public decimal? Decimal(string field, string display, decimal min)
    {
        var v = Raw(field);
        if (v is null) return null;
        if (!decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            Error(field, PartnerImportSupport.InvalidType, $"{display} must be a number.");
            return null;
        }
        if (d < min) { Error(field, PartnerImportSupport.OutOfRange, $"{display} cannot be less than {min}."); return null; }
        return d;
    }

    public int? Int(string field, string display, int min, int max)
    {
        var v = Raw(field);
        if (v is null) return null;
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            Error(field, PartnerImportSupport.InvalidType, $"{display} must be a whole number.");
            return null;
        }
        if (i < min || i > max) { Error(field, PartnerImportSupport.OutOfRange, $"{display} must be between {min} and {max}."); return null; }
        return i;
    }

    public string? Country(string field, string display)
    {
        var v = Raw(field);
        if (v is null) return null;
        var iso = PartnerImportSupport.NormalizeCountry(v);
        if (iso is null) Error(field, PartnerImportSupport.LookupNotFound, $"{display} '{v}' is not an ISO country code or country name.");
        return iso;
    }
}
