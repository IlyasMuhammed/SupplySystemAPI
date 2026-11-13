using System.Net;
using SMS.Modules.Lookups.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Demand.Services.Documents;

/// <summary>
/// A50 TPL (D-26, D-68) — "Email to customer / supplier" for a generated document: the recipients (the request's, else the
/// party's email), the subject and body from the org's default email template of the type (else the built-in text), the PDF
/// attached, one send per address (IEmailSender has no CC) through SendWithResultAsync. A failed send is reported, never thrown.
/// </summary>
internal sealed class DocumentEmailDispatcher
{
    private readonly IEmailSender _sender;
    private readonly IEmailTemplateRenderer _templates;

    public DocumentEmailDispatcher(IEmailSender sender, IEmailTemplateRenderer? templates = null)
    {
        _sender = sender;
        _templates = templates ?? new NullEmailTemplateRenderer();
    }

    /// <summary>The company placeholders, from the letterhead.</summary>
    public static void AddCompany(Dictionary<string, string?> values, PoDocumentTemplateModel? letterhead)
    {
        values["CompanyName"]    = letterhead?.CompanyName ?? string.Empty;
        values["CompanyAddress"] = letterhead?.CompanyAddress ?? string.Empty;
        values["CompanyPhone"]   = letterhead?.CompanyPhone ?? string.Empty;
        values["CompanyEmail"]   = letterhead?.CompanyEmail ?? string.Empty;
        values["CompanyTaxId"]   = letterhead?.CompanyTaxId ?? string.Empty;
        values["DocumentUrl"]    = string.Empty;
    }

    public async Task<DocumentEmailResult> SendAsync(
        DocumentEmailRequest? request, string? partyEmail, string templateType, Dictionary<string, string?> values,
        EmailAttachment attachment, CancellationToken ct = default)
    {
        request ??= new DocumentEmailRequest();
        var to = DocumentEmailAddresses.Parse(request.To, out var badTo);
        var cc = DocumentEmailAddresses.Parse(request.Cc, out var badCc);
        var invalid = badTo.Concat(badCc).ToList();
        if (invalid.Count > 0) throw new BadRequestException($"Not a valid email address: {string.Join(", ", invalid)}.");

        if (to.Count == 0 && DocumentEmailAddresses.IsValid(partyEmail?.Trim())) to.Add(partyEmail!.Trim());
        if (to.Count == 0) throw new BadRequestException("No recipient: enter an email address (the party has none on file).");
        cc = cc.Where(c => !to.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (to.Count + cc.Count > DocumentEmailAddresses.MaxRecipients)
            throw new BadRequestException($"At most {DocumentEmailAddresses.MaxRecipients} recipients.");

        values["Message"] = request.Message?.Trim() ?? string.Empty;
        var rendered = await _templates.RenderAsync(templateType, values, ct) ?? BuiltIn(templateType, values);

        var failed = new List<DocumentEmailFailure>();
        foreach (var address in to.Concat(cc))
        {
            var result = await _sender.SendWithResultAsync(address, rendered.Subject, rendered.BodyHtml, [attachment], ct)
                         ?? EmailSendResult.Failed("The email sender gave no result.");
            if (!result.Success) failed.Add(new DocumentEmailFailure(address, result.Error ?? "The email could not be sent."));
        }

        return new DocumentEmailResult { Sent = failed.Count == 0, SentTo = to, Cc = cc, Subject = rendered.Subject, Failed = failed };
    }

    /// <summary>The text sent when the org has no template of the type (Onboarding's built-in text, same wording).</summary>
    internal static RenderedEmail BuiltIn(string type, IReadOnlyDictionary<string, string?> v)
    {
        string E(string key) => WebUtility.HtmlEncode(v.TryGetValue(key, out var s) ? s ?? "" : "");
        string P(string key) => (v.TryGetValue(key, out var s) ? s ?? "" : "").Replace("\r", " ").Replace("\n", " ");
        var message = v.TryGetValue("Message", out var m) && !string.IsNullOrWhiteSpace(m) ? $"<p>{TemplateText.PlainToHtml(m)}</p>" : "";
        var sign = $"<p>Regards,<br>{E("CompanyName")}</p>";
        return type switch
        {
            EmailTemplateTypes.InvoiceSent => new($"Invoice {P("InvoiceNumber")} from {P("CompanyName")}".Trim(),
                $"<p>Dear {E("CustomerName")},</p><p>Please find attached invoice <strong>{E("InvoiceNumber")}</strong> dated {E("InvoiceDate")} for {E("TotalAmount")} {E("CurrencyCode")}, due on {E("DueDate")}.</p>{message}{sign}"),
            EmailTemplateTypes.QuotationSent => new($"Quotation {P("QuotationNumber")} from {P("CompanyName")}".Trim(),
                $"<p>Dear {E("CustomerName")},</p><p>Please find attached our quotation <strong>{E("QuotationNumber")}</strong> for {E("TotalAmount")} {E("CurrencyCode")}, valid until {E("ValidUntil")}.</p>{message}{sign}"),
            EmailTemplateTypes.PoToSupplier => new($"Purchase Order {P("PoNumber")} from {P("CompanyName")}".Trim(),
                $"<p>Dear {E("SupplierName")},</p><p>Please find attached purchase order <strong>{E("PoNumber")}</strong> dated {E("PoDate")} for {E("TotalAmount")} {E("CurrencyCode")}. Requested delivery: {E("DeliveryDate")}.</p>{message}{sign}"),
            _ => new(P("DocumentNumber"), $"{message}{sign}")
        };
    }
}
