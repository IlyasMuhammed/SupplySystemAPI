using System.Text;

namespace SMS.Modules.Integration.Core.Matching;

/// <summary>How names and ids are compared when matching caller records against existing QuickBooks records.</summary>
internal static class MatchNormalizer
{
    /// <summary>Company-form words ignored at the end of a name for a "probable" match.</summary>
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "LTD", "PVT", "LLC", "INC", "CO", "COMPANY", "LIMITED", "PRIVATE", "CORP", "CORPORATION", "PLC", "SMC"
    };

    /// <summary>The minimum length of the shorter name for a "one contains the other" match.</summary>
    public const int MinContainsLength = 4;

    /// <summary>Exact form: trimmed, case-insensitive, runs of whitespace collapsed.</summary>
    public static string Exact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Loose form: punctuation removed and trailing company-form words (Ltd, Pvt, Inc…) dropped.</summary>
    public static string Loose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ');

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && Suffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);

        return string.Join(' ', words);
    }

    /// <summary>Identifier form (tax ids, codes, SKUs): letters and digits only, upper case.</summary>
    public static string Id(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    public static string Email(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    /// <summary>One loose name contains the other, and the shorter is long enough to mean something.</summary>
    public static bool Contains(string looseA, string looseB)
    {
        if (looseA.Length == 0 || looseB.Length == 0) return false;
        var (shorter, longer) = looseA.Length <= looseB.Length ? (looseA, looseB) : (looseB, looseA);
        if (shorter.Length < MinContainsLength) return false;
        return longer.Contains(shorter, StringComparison.Ordinal);
    }
}
