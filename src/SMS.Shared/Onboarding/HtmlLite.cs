using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SMS.Shared.Onboarding;

/// <summary>One printable line of a header/footer HTML fragment (<see cref="HtmlLite"/>).</summary>
public sealed record HtmlLiteLine(string Text, bool Bold, float? FontSizePt, string? Color, string Align);

/// <summary>
/// A50 TPL (D-66) — the small HTML subset a template's header / footer is read as by the built-in QuestPDF layouts, which
/// cannot embed a browser: &lt;td&gt; cells become side-by-side columns; &lt;div&gt; / &lt;p&gt; / &lt;br&gt; / &lt;li&gt; / headings separate
/// lines; bold from &lt;b&gt;/&lt;strong&gt;/&lt;h1-6&gt;/font-weight; size from font-size:Npx|pt; colour from color:#hex; alignment from
/// text-align. Everything else (other CSS, images, links) is ignored, never an error. Each line takes the style of its first text.
/// </summary>
public static class HtmlLite
{
    private static readonly Regex Cell  = new(@"<td\b[^>]*>(?<c>.*?)</td\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Piece = new(@"<(?<close>/?)(?<tag>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*)>|<!--.*?-->|(?<text>[^<]+)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase) { "div", "p", "br", "li", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "table", "ul", "ol" };
    private static readonly HashSet<string> Voids  = new(StringComparer.OrdinalIgnoreCase) { "br", "img", "hr", "meta", "input" };

    private sealed record Style(bool Bold, float? Size, string? Color, string Align);

    public static IReadOnlyList<IReadOnlyList<HtmlLiteLine>> ToColumns(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        if (!TemplateText.LooksLikeHtml(html))
            return [TemplateText.ToPlainLines(html).Select(l => new HtmlLiteLine(l, false, null, null, "left")).ToList()];

        var cells = Cell.Matches(html).Select(m => m.Groups["c"].Value).ToList();
        return (cells.Count > 0 ? cells : [html]).Select(Lines).Where(c => c.Count > 0).ToList();
    }

    /// <summary>All lines of all columns, in reading order.</summary>
    public static IReadOnlyList<HtmlLiteLine> ToLines(string? html) => ToColumns(html).SelectMany(c => c).ToList();

    private static IReadOnlyList<HtmlLiteLine> Lines(string html)
    {
        var lines = new List<HtmlLiteLine>();
        var stack = new Stack<(string Tag, Style Style)>();
        var current = new Style(false, null, null, "left");
        var text = new StringBuilder();
        Style? lineStyle = null;

        void Flush()
        {
            var t = Regex.Replace(WebUtility.HtmlDecode(text.ToString()), @"[\s ]+", " ").Trim();
            if (t.Length > 0 && lineStyle is not null)
                lines.Add(new HtmlLiteLine(t, lineStyle.Bold, lineStyle.Size, lineStyle.Color, lineStyle.Align));
            text.Clear();
            lineStyle = null;
        }

        foreach (Match m in Piece.Matches(html))
        {
            if (m.Groups["text"].Success)
            {
                var raw = m.Groups["text"].Value;
                if (raw.Trim().Length == 0) { if (text.Length > 0) text.Append(' '); continue; }
                lineStyle ??= current;
                text.Append(raw);
                continue;
            }
            if (!m.Groups["tag"].Success) continue; // a comment

            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var closing = m.Groups["close"].Value == "/";
            if (Blocks.Contains(tag)) Flush();
            if (Voids.Contains(tag) || m.Groups["attrs"].Value.TrimEnd().EndsWith('/')) continue;

            if (!closing)
            {
                stack.Push((tag, current));
                current = Apply(current, tag, m.Groups["attrs"].Value);
            }
            else
            {
                while (stack.Count > 0)
                {
                    var (open, before) = stack.Pop();
                    current = before;
                    if (open == tag) break;
                }
            }
        }
        Flush();
        return lines;
    }

    private static Style Apply(Style s, string tag, string attrs)
    {
        var bold  = s.Bold || tag is "b" or "strong" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
        float? size = tag switch { "h1" => 18f, "h2" => 15f, "h3" => 13f, "h4" => 11f, _ => s.Size };
        var color = s.Color;
        var align = s.Align;

        var style = Regex.Match(attrs, @"style\s*=\s*(""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.IgnoreCase);
        if (style.Success)
        {
            foreach (var decl in style.Groups["v"].Value.Split(';'))
            {
                var kv = decl.Split(':', 2);
                if (kv.Length != 2) continue;
                var key   = kv[0].Trim().ToLowerInvariant();
                var value = kv[1].Trim().ToLowerInvariant();
                switch (key)
                {
                    case "font-weight":
                        bold = value is "bold" or "bolder" or "600" or "700" or "800" or "900";
                        break;
                    case "font-size":
                        var n = Regex.Match(value, @"^(?<n>\d+(\.\d+)?)\s*(?<u>px|pt)?$");
                        if (n.Success && float.TryParse(n.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                            size = n.Groups["u"].Value == "pt" ? f : f * 0.75f;
                        break;
                    case "color":
                        var c = kv[1].Trim();
                        if (BrandPalette.IsValid(c)) color = c.ToUpperInvariant();
                        break;
                    case "text-align":
                        if (value is "left" or "center" or "right") align = value;
                        break;
                }
            }
        }
        return new Style(bold, size, color, align);
    }
}
