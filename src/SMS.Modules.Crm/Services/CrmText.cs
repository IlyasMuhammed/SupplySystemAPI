using System.Text.RegularExpressions;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>Input checks shared by the CRM services: an over-long value is a 400 here, never a truncation error from SQL.</summary>
internal static partial class CrmText
{
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_\-]{0,29}$")]
    private static partial Regex CodePattern();

    /// <summary>Trimmed, or null when blank; refuses more than <paramref name="max"/> characters.</summary>
    public static string? Optional(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Length > max) throw new BadRequestException($"{field} must be at most {max} characters.");
        return v;
    }

    public static string Required(string? value, int max, string field) =>
        Optional(value, max, field) ?? throw new BadRequestException($"{field} is required.");

    /// <summary>Upper-cased machine code: letters, digits, '_' and '-', up to 30 characters.</summary>
    public static string Code(string? value, string field = "Code")
    {
        var v = Required(value, 30, field).ToUpperInvariant();
        if (!CodePattern().IsMatch(v))
            throw new BadRequestException($"{field} may contain only letters, digits, '_' and '-' and must start with a letter or digit.");
        return v;
    }

    /// <summary>One of <paramref name="allowed"/> (case-insensitive, stored upper-case); null when blank.</summary>
    public static string? OneOf(string? value, IReadOnlyList<string> allowed, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToUpperInvariant();
        return allowed.Contains(v) ? v : throw new BadRequestException($"{field} must be one of {string.Join(", ", allowed)}.");
    }

    public static string? Email(string? value, string field = "Email")
    {
        var v = Optional(value, 200, field);
        if (v is not null && (!v.Contains('@') || v.StartsWith('@') || v.EndsWith('@') || v.Contains(' ')))
            throw new BadRequestException($"{field} is not a valid address.");
        return v;
    }

    public static decimal? Money(decimal? value, string field)
    {
        if (value is null) return null;
        if (value < 0) throw new BadRequestException($"{field} cannot be negative.");
        if (value > 9_999_999_999_999_999.99m) throw new BadRequestException($"{field} is too large.");
        return decimal.Round(value.Value, 2);
    }

    /// <summary>Digits only, for comparing phone numbers typed with spaces, dashes or a leading '+'.</summary>
    public static string? PhoneKey(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 6 ? digits : null;
    }

    public static (int Page, int PageSize) Paging(int page, int pageSize, int max = 100) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, max));

    public static bool Desc(string? order) => order?.Trim().ToLowerInvariant() is "desc" or "-1" or "descend" or "descending";
}
