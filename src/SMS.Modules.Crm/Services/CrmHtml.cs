using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>
/// D-21 / BR-19 — activity notes are HTML from the activity form's small editor. Only plain formatting survives: the tags
/// below, with no attributes at all (so no style, event handler, link or source can ride along). <c>script</c>, <c>style</c>
/// and similar elements are dropped with everything inside them; any other tag is dropped and its text kept; text is
/// re-encoded so a stray '&lt;' can never start a tag.
/// </summary>
internal static partial class CrmHtml
{
    public const int MaxLength = 1_000_000;

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "div", "span", "b", "strong", "i", "em", "u", "ul", "ol", "li"
    };

    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "noscript", "template", "svg", "math", "head", "title", "textarea", "select"
    };

    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase) { "br" };

    [GeneratedRegex(@"<!--.*?-->|<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    public static string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        if (html.Length > MaxLength) throw new BadRequestException("The notes are too long (1 MB at most).");

        var sb = new StringBuilder(html.Length);
        var pos = 0;
        string? dropping = null; // inside <script>…</script> and the like
        foreach (Match m in Tag().Matches(html))
        {
            if (dropping is null) AppendText(sb, html[pos..m.Index]);
            pos = m.Index + m.Length;
            if (m.Value.StartsWith("<!--")) continue;

            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();

            if (dropping is not null)
            {
                if (closing && name == dropping) dropping = null;
                continue;
            }
            if (DropWithContent.Contains(name))
            {
                if (!closing && !m.Value.EndsWith("/>")) dropping = name;
                continue;
            }
            if (!Allowed.Contains(name)) continue;
            if (Void.Contains(name)) { if (!closing) sb.Append("<br>"); continue; }
            sb.Append(closing ? $"</{name}>" : $"<{name}>");
        }
        if (dropping is null) AppendText(sb, html[pos..]);

        var result = sb.ToString().Trim();
        return PlainText(result).Length == 0 && !result.Contains("<li>") ? null : result;
    }

    /// <summary>The text of sanitized HTML (export, previews): tags out, line breaks for blocks, entities decoded.</summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li)\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*li\s*>", "• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]*>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    // Decode first so an entity stays one entity, then encode the characters HTML gives meaning to.
    private static void AppendText(StringBuilder sb, string text)
    {
        if (text.Length == 0) return;
        sb.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(text)));
    }
}
