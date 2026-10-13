using System.Globalization;
using System.Text.RegularExpressions;
using SMS.Modules.Import.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Services;

/// <summary>
/// A40 C7 — generic value parsing (booleans yes/no/1/0/y/n/true/false; dates ISO, MM-dd-yyyy, dd-MMM-yyyy or an Excel serial;
/// decimals with "." or "," and thousand separators stripped).
/// </summary>
internal static class ImportValueParser
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yy", "d-MMM-yy",
    ];

    /// <summary>Excel's serial range (1 = 1900-01-01 … 9999-12-31).</summary>
    private const double MaxSerial = 2_958_465;

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"^\+?[0-9\s\-\(\)\.\/]+$", RegexOptions.Compiled);

    public static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "yes": case "y": case "true": case "1": result = true; return true;
            case "no": case "n": case "false": case "0": result = false; return true;
            default: result = false; return false;
        }
    }

    public static bool TryParseDate(string value, out DateOnly result)
    {
        var text = value.Trim();
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            result = DateOnly.FromDateTime(dt);
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial >= 1 && serial <= MaxSerial)
        {
            result = DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>"1,234.56", "1.234,56", "1234,56", "1 234.5", "-12" … → invariant decimal. A lone comma followed by exactly
    /// three digits is read as a thousands separator ("1,234" = 1234).</summary>
    public static bool TryParseDecimal(string value, out decimal result)
    {
        result = 0;
        var text = new string(value.Trim().Where(c => !char.IsWhiteSpace(c) && c != '\'' && c != ' ').ToArray());
        if (text.Length == 0) return false;

        var lastDot = text.LastIndexOf('.');
        var lastComma = text.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            text = lastDot > lastComma
                ? text.Replace(",", "")
                : text.Replace(".", "").Replace(',', '.');
        }
        else if (lastComma >= 0)
        {
            var commas = text.Count(c => c == ',');
            var decimals = text.Length - lastComma - 1;
            text = commas > 1 || decimals == 3 ? text.Replace(",", "") : text.Replace(',', '.');
        }
        else if (lastDot >= 0 && text.Count(c => c == '.') > 1)
        {
            text = text.Replace(".", "");
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out result);
    }

    public static bool TryParseInteger(string value, out long result)
    {
        result = 0;
        if (!TryParseDecimal(value, out var d) || d != decimal.Truncate(d) || d > long.MaxValue || d < long.MinValue) return false;
        result = (long)d;
        return true;
    }

    public static bool IsEmail(string value) => EmailRegex.IsMatch(value);

    public static bool IsPhone(string value)
    {
        if (!PhoneRegex.IsMatch(value)) return false;
        var digits = value.Count(char.IsDigit);
        return digits is >= 7 and <= 15;
    }

    /// <summary>Digits after the decimal point once trailing zeros are dropped.</summary>
    public static int Scale(decimal value)
    {
        var normalized = value / 1.0000000000000000000000000000m;
        return (decimal.GetBits(normalized)[3] >> 16) & 0xFF;
    }

    public static string Format(decimal value) =>
        (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}

/// <summary>The outcome of generic checks on one row.</summary>
internal sealed record GenericRowResult(Dictionary<string, string?> Values, List<ImportFieldError> Errors);

/// <summary>A40 C7 — the generic, per-field checks run on every mapped value before the target sees the row.</summary>
internal static class ImportGenericValidator
{
    private static readonly Dictionary<string, Regex> RegexCache = new();

    /// <param name="fields">The profile's fields (external_id included).</param>
    /// <param name="raw">Mapped raw values by field name (unmapped fields absent).</param>
    public static GenericRowResult Validate(IReadOnlyList<ImportFieldDefinition> fields, IReadOnlyDictionary<string, string?> raw)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<ImportFieldError>();

        foreach (var field in fields)
        {
            var mapped = raw.TryGetValue(field.FieldName, out var rawValue);
            var text = rawValue?.Trim();
            if (string.IsNullOrEmpty(text)) text = null;
            // Only a REQUIRED field's default fills a blank cell ("a required field with a default value need not be mapped").
            // An optional field's default is the target's create default: filled in here it would reach an UPDATE too and
            // overwrite the stored value (IMP-EXC-04: blank = unchanged) — e.g. re-activate an inactive record.
            if (text is null && field.DefaultValue is not null && field.IsRequired) text = field.DefaultValue;
            if (!mapped && text is null) continue;

            if (text is null)
            {
                if (field.IsRequired)
                    errors.Add(new(field.FieldName, ImportErrorCodes.Required, $"{field.DisplayName} is required.", null));
                values[field.FieldName] = null;
                continue;
            }

            var (parsed, error) = ParseValue(field, text);
            if (error is not null) errors.Add(error);
            values[field.FieldName] = error is null ? parsed : text;
        }

        return new GenericRowResult(values, errors);
    }

    private static (string? Value, ImportFieldError? Error) ParseValue(ImportFieldDefinition f, string text)
    {
        ImportFieldError Err(string code, string message) => new(f.FieldName, code, message, text);

        switch (f.FieldType)
        {
            case ImportFieldTypes.Integer:
            {
                if (!ImportValueParser.TryParseInteger(text, out var n))
                    return (null, Err(ImportErrorCodes.InvalidType, $"{f.DisplayName} must be a whole number."));
                if (RangeError(f, n) is { } range) return (null, Err(ImportErrorCodes.OutOfRange, range));
                return (n.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
            }
            case ImportFieldTypes.Currency:
            {
                // A currency CODE (ISO 4217, e.g. "pkr" → "PKR"); amounts are DECIMAL.
                var code = text.ToUpperInvariant();
                if (code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z'))
                    return (null, Err(ImportErrorCodes.InvalidType, $"{f.DisplayName} must be a 3-letter currency code."));
                return (code, null);
            }
            case ImportFieldTypes.Decimal:
            {
                if (!ImportValueParser.TryParseDecimal(text, out var d))
                    return (null, Err(ImportErrorCodes.InvalidType, $"{f.DisplayName} must be a number."));
                if (RangeError(f, d) is { } range) return (null, Err(ImportErrorCodes.OutOfRange, range));
                if (f.DecimalPlaces is { } places && ImportValueParser.Scale(d) > places)
                    return (null, Err(ImportErrorCodes.InvalidPrecision, $"{f.DisplayName} allows at most {places} decimal places."));
                return (ImportValueParser.Format(d), null);
            }
            case ImportFieldTypes.Date:
            {
                if (!ImportValueParser.TryParseDate(text, out var date))
                    return (null, Err(ImportErrorCodes.InvalidDate,
                        $"{f.DisplayName} must be a date (yyyy-MM-dd, MM-dd-yyyy, dd-MMM-yyyy or an Excel date)."));
                return (date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), null);
            }
            case ImportFieldTypes.Boolean:
            {
                if (!ImportValueParser.TryParseBoolean(text, out var b))
                    return (null, Err(ImportErrorCodes.InvalidType, $"{f.DisplayName} must be Yes or No."));
                return (b ? "true" : "false", null);
            }
            case ImportFieldTypes.Email:
            {
                if (LengthError(f, text) is { } len) return (null, Err(ImportErrorCodes.MaxLength, len));
                if (!ImportValueParser.IsEmail(text))
                    return (null, Err(ImportErrorCodes.InvalidEmail, $"{f.DisplayName} is not a valid email address."));
                return (text, null);
            }
            case ImportFieldTypes.Phone:
            {
                if (LengthError(f, text) is { } len) return (null, Err(ImportErrorCodes.MaxLength, len));
                if (!ImportValueParser.IsPhone(text))
                    return (null, Err(ImportErrorCodes.InvalidPhone, $"{f.DisplayName} is not a valid phone number."));
                return (text, null);
            }
            default: // TEXT, LOOKUP
            {
                if (LengthError(f, text) is { } len) return (null, Err(ImportErrorCodes.MaxLength, len));
                if (f.ValidationRegex is { Length: > 0 } pattern && !RegexFor(pattern).IsMatch(text))
                    return (null, Err(ImportErrorCodes.PatternMismatch, $"{f.DisplayName} has an invalid format."));
                return (text, null);
            }
        }
    }

    private static string? LengthError(ImportFieldDefinition f, string text) =>
        f.MaxLength is { } max && text.Length > max ? $"{f.DisplayName} must be at most {max} characters." : null;

    private static string? RangeError(ImportFieldDefinition f, decimal value)
    {
        var low = f.MinValue is { } min && value < min;
        var high = f.MaxValue is { } max && value > max;
        if (!low && !high) return null;
        return (f.MinValue, f.MaxValue) switch
        {
            ({ } a, { } b) => $"{f.DisplayName} must be between {ImportValueParser.Format(a)} and {ImportValueParser.Format(b)}.",
            ({ } a, null)  => $"{f.DisplayName} must be at least {ImportValueParser.Format(a)}.",
            (null, { } b)  => $"{f.DisplayName} must be at most {ImportValueParser.Format(b)}.",
            _              => null
        };
    }

    private static Regex RegexFor(string pattern)
    {
        lock (RegexCache)
        {
            if (!RegexCache.TryGetValue(pattern, out var regex))
                RegexCache[pattern] = regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            return regex;
        }
    }

    /// <summary>
    /// DUPLICATE_IN_FILE — a later row whose match-field values (all present, case-insensitive) or external id repeat an
    /// earlier row's. Returns errors keyed by row number.
    /// </summary>
    public static Dictionary<int, List<ImportFieldError>> FindInFileDuplicates(
        IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows,
        IReadOnlyList<string> matchFields, IReadOnlyList<ImportFieldDefinition> fields)
    {
        var result = new Dictionary<int, List<ImportFieldError>>();
        void Add(int row, ImportFieldError e)
        {
            if (!result.TryGetValue(row, out var list)) result[row] = list = new();
            list.Add(e);
        }

        var display = fields.ToDictionary(f => f.FieldName, f => f.DisplayName, StringComparer.OrdinalIgnoreCase);
        if (matchFields.Count > 0)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var label = string.Join(", ", matchFields.Select(m => display.GetValueOrDefault(m, m)));
            foreach (var (rowNumber, values) in rows)
            {
                var parts = matchFields.Select(m => values.TryGetValue(m, out var v) ? v : null).ToList();
                if (parts.Any(string.IsNullOrWhiteSpace)) continue;
                var key = string.Join("\u001F", parts.Select(p => p!.Trim()));
                if (seen.TryGetValue(key, out var first))
                    Add(rowNumber, new(matchFields[0], ImportErrorCodes.DuplicateInFile, $"Duplicate of row {first} ({label}).", parts[0]));
                else seen[key] = rowNumber;
            }
        }

        var seenExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowNumber, values) in rows)
        {
            if (!values.TryGetValue(Models.ImportLimits.ExternalIdField, out var ext) || string.IsNullOrWhiteSpace(ext)) continue;
            if (seenExt.TryGetValue(ext.Trim(), out var first))
                Add(rowNumber, new(Models.ImportLimits.ExternalIdField, ImportErrorCodes.DuplicateInFile,
                    $"Duplicate of row {first} (External ID).", ext));
            else seenExt[ext.Trim()] = rowNumber;
        }
        return result;
    }
}
