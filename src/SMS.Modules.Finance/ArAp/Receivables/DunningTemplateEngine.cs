using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SMS.Modules.Finance.ArAp.Receivables;

/// <summary>What a dunning notice is rendered from.</summary>
internal sealed record DunningTemplateModel(
    string CustomerName,
    string? CustomerCode,
    int DunningLevel,
    decimal TotalOverdue,
    string BaseCurrencyCode,
    DateTime? OldestDueDate,
    IReadOnlyList<DunningOpenItem> Items,
    decimal? ChargeAmount = null);

/// <summary>A48 tasks 235–238 (D-11) — placeholder substitution for dunning subjects and bodies.</summary>
internal interface IDunningTemplateEngine
{
    /// <summary>The placeholders a template may use.</summary>
    IReadOnlyList<string> Placeholders { get; }
    string RenderSubject(string template, DunningTemplateModel model);
    string RenderBody(string template, DunningTemplateModel model);
}

/// <summary>
/// Replaces {CustomerName}, {CustomerCode}, {TotalOverdue}, {OldestDueDate}, {DunningLevel}, {InvoiceList}, {Amount} and
/// {DueDate} (case-insensitive). The body is HTML (it goes out through IEmailSender as HTML): values are HTML-encoded and
/// {InvoiceList} is a small table; the subject is plain text and {InvoiceList} there is a comma list of numbers.
/// {Amount} = the total overdue, {DueDate} = the oldest due date (the FSD's aliases). Unknown placeholders are left as typed,
/// so a typo shows in the preview instead of vanishing.
/// </summary>
internal sealed class DunningTemplateEngine : IDunningTemplateEngine
{
    private static readonly Regex Token = new(@"\{(?<name>[A-Za-z]+)\}", RegexOptions.Compiled);

    public static readonly IReadOnlyList<string> Names =
        ["CustomerName", "CustomerCode", "TotalOverdue", "OldestDueDate", "DunningLevel", "InvoiceList", "Amount", "DueDate", "Charge"];

    public IReadOnlyList<string> Placeholders => Names;

    public string RenderSubject(string template, DunningTemplateModel m) => Render(template, m, html: false);

    public string RenderBody(string template, DunningTemplateModel m) => Render(template, m, html: true);

    /// <summary>
    /// A body template without HTML tags (the seeded ones are plain text) has its own text HTML-encoded and its line breaks
    /// turned into &lt;br&gt;, so it reads the same in an HTML mail; an HTML template is taken as written.
    /// </summary>
    internal static string Render(string? template, DunningTemplateModel m, bool html)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var plain = html && !Regex.IsMatch(template, @"<[a-zA-Z/][^>]*>");

        var sb = new StringBuilder();
        var last = 0;
        foreach (Match match in Token.Matches(template))
        {
            sb.Append(Literal(template[last..match.Index], plain));
            sb.Append(Value(match.Groups["name"].Value, m, html) ?? Literal(match.Value, plain));
            last = match.Index + match.Length;
        }
        sb.Append(Literal(template[last..], plain));
        return sb.ToString();
    }

    private static string Literal(string text, bool plainToHtml) =>
        plainToHtml ? WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>") : text;

    private static string? Value(string name, DunningTemplateModel m, bool html)
    {
        string Enc(string s) => html ? WebUtility.HtmlEncode(s) : s;
        switch (name.ToLowerInvariant())
        {
            case "customername":  return Enc(m.CustomerName);
            case "customercode":  return Enc(m.CustomerCode ?? string.Empty);
            case "totaloverdue":
            case "amount":        return Enc($"{Money(m.TotalOverdue)} {m.BaseCurrencyCode}".Trim());
            case "oldestduedate":
            case "duedate":       return Enc(m.OldestDueDate is { } d ? d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : "-");
            case "dunninglevel":  return m.DunningLevel.ToString(CultureInfo.InvariantCulture);
            case "charge":        return Enc(m.ChargeAmount is > 0m ? $"{Money(m.ChargeAmount.Value)} {m.BaseCurrencyCode}".Trim() : "-");
            case "invoicelist":   return html ? InvoiceTable(m.Items) : string.Join(", ", m.Items.Select(i => i.InvoiceNumber));
            default:              return null;
        }
    }

    internal static string InvoiceTable(IReadOnlyList<DunningOpenItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("<table style=\"border-collapse:collapse\" cellpadding=\"4\" border=\"1\">");
        sb.Append("<tr><th>Invoice</th><th>Invoice date</th><th>Due date</th><th>Open</th></tr>");
        foreach (var i in items)
            sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(i.InvoiceNumber))
              .Append("</td><td>").Append(i.InvoiceDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))
              .Append("</td><td>").Append(i.DueDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))
              .Append("</td><td style=\"text-align:right\">").Append(WebUtility.HtmlEncode($"{Money(i.Open)} {i.CurrencyCode}"))
              .Append("</td></tr>");
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
}
