using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SMS.Shared.Onboarding;

// A50 (TPL, D-25 / D-26 / D-65..D-69) — the seams the existing document renderers (Finance, Demand, Logistics) use to
// apply an organization's document template without referencing SMS.Modules.Onboarding, plus the one {Token} engine
// shared by document and email templates. Every interface has a Null default for hosts without Onboarding.

/// <summary>A column of the line-items table of a <see cref="TemplatedDocument"/>.</summary>
public sealed record TemplatedColumn(string Header, float RelativeWidth = 1f, bool AlignRight = false);

/// <summary>A labelled amount: a tax-summary row or a totals row.</summary>
public sealed record TemplatedAmount(string Label, string Value, bool Emphasis = false);

/// <summary>
/// A document handed to <see cref="ITemplatedDocumentRenderer"/> when the org's template has a BodyHtml: the simple
/// placeholder values plus the structural blocks ({LineItems}, {TaxSummary}, {Totals}, {SignatureBlock}).
/// </summary>
public sealed class TemplatedDocument
{
    public string TemplateType { get; init; } = string.Empty;
    /// <summary>"INVOICE", "QUOTATION", … — the heading and {DocumentTitle}.</summary>
    public string Title { get; init; } = string.Empty;
    /// <summary>Simple placeholder values by name (no braces), case-insensitive.</summary>
    public Dictionary<string, string?> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TemplatedColumn> LineColumns { get; init; } = new();
    public List<IReadOnlyList<string>> LineRows { get; init; } = new();
    public List<TemplatedAmount> TaxSummary { get; init; } = new();
    public List<TemplatedAmount> Totals { get; init; } = new();
    /// <summary>Labels of the signature lines; empty = "Prepared By / Approved By / Authorized Signatory".</summary>
    public List<string> SignatureLabels { get; init; } = new();
    /// <summary>When set, {SignatureBlock} prints this sentence instead of signature lines.</summary>
    public string? SignatureDisclaimer { get; init; }
    public DateTime GeneratedAt { get; init; }
}

/// <summary>Renders a template's BodyHtml (D-25) into a PDF. Implemented by Onboarding; the Null one answers null (= use the built-in layout).</summary>
public interface ITemplatedDocumentRenderer
{
    /// <summary>The PDF, or null when this host cannot render templated bodies.</summary>
    byte[]? Render(DocumentBranding branding, TemplatedDocument document);
}

public sealed class NullTemplatedDocumentRenderer : ITemplatedDocumentRenderer
{
    public byte[]? Render(DocumentBranding branding, TemplatedDocument document) => null;
}

/// <summary>The organization's pre-A50 letterhead (Lookups PoDocumentTemplate), for the one-time copy and the company block.</summary>
public sealed record LegacyLetterhead(
    string? CompanyName, string? CompanyAddress, string? CompanyPhone, string? CompanyEmail, string? CompanyTaxId,
    string? LogoUrl, string? BodyHtml, string? FooterText, string? BankDetails,
    bool ShowSignatureBlock, string? SignatureDisclaimer, string? PreparedByLabel, string? ApprovedByLabel, string? AuthorizedSignatoryLabel);

/// <summary>Implemented by Lookups. Ambient organization.</summary>
public interface ILegacyLetterheadSource
{
    /// <summary>The org's saved PoDocumentTemplate, or null when it never saved one (defaults are not invented here).</summary>
    Task<LegacyLetterhead?> GetAsync(CancellationToken ct = default);
    /// <summary>Same, for an explicit organization (anonymous paths: accept-invite).</summary>
    Task<LegacyLetterhead?> GetForOrganizationAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult<LegacyLetterhead?>(null);
}

public sealed class NullLegacyLetterheadSource : ILegacyLetterheadSource
{
    public Task<LegacyLetterhead?> GetAsync(CancellationToken ct = default) => Task.FromResult<LegacyLetterhead?>(null);
}

/// <summary>A stored attachment's bytes and URL, for template logos. Implemented by WorkflowEngine.</summary>
public interface IDocumentAssetReader
{
    /// <summary>The file, only when the attachment exists, is not deleted and has <paramref name="interfaceCode"/>; else null.</summary>
    Task<DocumentAsset?> ReadAsync(Guid attachmentUuid, string interfaceCode, CancellationToken ct = default);
}

public sealed record DocumentAsset(byte[] Content, string? ContentType, string FileUrl);

public sealed class NullDocumentAssetReader : IDocumentAssetReader
{
    public Task<DocumentAsset?> ReadAsync(Guid attachmentUuid, string interfaceCode, CancellationToken ct = default) =>
        Task.FromResult<DocumentAsset?>(null);
}

/// <summary>
/// The {Token} engine of document and email templates (dunning-style, D-26): <c>{Name}</c>, case-insensitive, letters and
/// digits. In HTML mode values are HTML-encoded (a customer called "&lt;b&gt;" prints as typed, never as markup); unknown
/// tokens are left as typed so a typo shows in the preview. A value that must stay HTML (a table) is passed through
/// <paramref name="rawHtml"/>.
/// </summary>
public static class TemplateText
{
    private static readonly Regex Token = new(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[a-zA-Z/!][^>]*>", RegexOptions.Compiled);

    public static string Render(
        string? template, IReadOnlyDictionary<string, string?> values, bool html,
        IReadOnlyDictionary<string, string>? rawHtml = null)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return Token.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            if (html && rawHtml is not null && TryGet(rawHtml, name, out var raw)) return raw;
            if (!TryGet(values, name, out var value)) return m.Value;
            value ??= string.Empty;
            return html ? WebUtility.HtmlEncode(value) : value;
        });
    }

    /// <summary>The token names a template uses (distinct, as typed).</summary>
    public static IReadOnlyList<string> TokensIn(string? template) =>
        string.IsNullOrEmpty(template) ? [] : Token.Matches(template).Select(m => m.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when the text contains HTML tags (else it is plain text).</summary>
    public static bool LooksLikeHtml(string? text) => !string.IsNullOrEmpty(text) && Tag.IsMatch(text);

    /// <summary>Plain text → HTML: encoded, line breaks as &lt;br&gt;.</summary>
    public static string PlainToHtml(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>");

    /// <summary>
    /// A simple HTML fragment as printable lines: &lt;p&gt;, &lt;div&gt;, &lt;br&gt;, &lt;li&gt; and headings end a line; every other tag is
    /// dropped; entities are decoded; blank lines removed. Plain text is split on its line breaks.
    /// </summary>
    public static IReadOnlyList<string> ToPlainLines(string? htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText)) return [];
        var text = htmlOrText.Replace("\r\n", "\n");
        if (LooksLikeHtml(text))
        {
            text = text.Replace("\n", " ");
            text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\s*/\s*(p|div|li|h[1-6]|tr)\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\s*li[^>]*>", "• ", RegexOptions.IgnoreCase);
            text = Tag.Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text);
        }
        return text.Split('\n').Select(l => Regex.Replace(l, @"[ \t ]+", " ").Trim()).Where(l => l.Length > 0).ToList();
    }

    private static bool TryGet<T>(IReadOnlyDictionary<string, T> map, string name, out T value)
    {
        if (map.TryGetValue(name, out value!)) return true;
        foreach (var (k, v) in map)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = default!;
        return false;
    }
}

/// <summary>Brand colours derived from a template's PrimaryColor, for the built-in layouts.</summary>
public static class BrandPalette
{
    private static readonly Regex Hex = new(@"\A#[0-9A-Fa-f]{6}\z", RegexOptions.Compiled); // \z, not $: "#AABBCC\n" is not a colour (REV50-15)

    public static bool IsValid(string? color) => color is not null && Hex.IsMatch(color);

    /// <summary>The colour scaled towards black (factor 0..1 of the original kept).</summary>
    public static string Darken(string color, double keep = 0.69) => Mix(color, "#000000", keep);

    /// <summary>The colour mixed into white (factor of the colour kept), for row stripes and boxes.</summary>
    public static string Tint(string color, double keep = 0.07) => Mix(color, "#FFFFFF", keep);

    private static string Mix(string color, string other, double keep)
    {
        static (int R, int G, int B) Parse(string c) => (
            int.Parse(c.AsSpan(1, 2), NumberStyles.HexNumber), int.Parse(c.AsSpan(3, 2), NumberStyles.HexNumber),
            int.Parse(c.AsSpan(5, 2), NumberStyles.HexNumber));
        var a = Parse(color);
        var b = Parse(other);
        int M(int x, int y) => (int)Math.Round(x * keep + y * (1 - keep));
        return $"#{M(a.R, b.R):X2}{M(a.G, b.G):X2}{M(a.B, b.B):X2}";
    }
}

/// <summary>An amount in English words ("One thousand two hundred and fifty Pakistani rupees and 50/100").</summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
         "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    private static readonly (long Value, string Name)[] Scales =
        [(1_000_000_000_000, "trillion"), (1_000_000_000, "billion"), (1_000_000, "million"), (1_000, "thousand")];

    /// <summary>"<paramref name="currencyCode"/> one hundred and five only" style: code first, cents as "and NN/100".</summary>
    public static string Format(decimal amount, string? currencyCode)
    {
        var negative = amount < 0;
        amount = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var whole = (long)Math.Truncate(amount);
        var cents = (int)((amount - whole) * 100);
        var sb = new StringBuilder();
        if (negative) sb.Append("minus ");
        if (!string.IsNullOrWhiteSpace(currencyCode)) sb.Append(currencyCode.Trim().ToUpperInvariant()).Append(' ');
        var words = Words(whole);
        sb.Append(char.ToUpperInvariant(words[0])).Append(words[1..]);
        if (cents > 0) sb.Append(" and ").Append(cents.ToString("00", CultureInfo.InvariantCulture)).Append("/100");
        sb.Append(" only");
        var s = sb.ToString();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public static string Words(long n)
    {
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : "");
        if (n < 1000) return Ones[n / 100] + " hundred" + (n % 100 > 0 ? " and " + Words(n % 100) : "");
        foreach (var (value, name) in Scales)
            if (n >= value)
            {
                var rest = n % value;
                return Words(n / value) + " " + name + (rest == 0 ? "" : (rest < 100 ? " and " : " ") + Words(rest));
            }
        return n.ToString(CultureInfo.InvariantCulture);
    }
}
