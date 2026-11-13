using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>
/// A50 TPL (D-25) — <see cref="IDocumentBrandingProvider"/>: the ambient org's default, active template of a type, with its logo
/// bytes and the company block (the saved letterhead, else the organization's name). Null when the org has none — the renderer
/// then prints exactly what it printed before A50.
/// </summary>
internal sealed class DocumentBrandingProvider : IDocumentBrandingProvider
{
    private readonly OnboardingDbContext _db;
    private readonly TemplateSupport _support;

    public DocumentBrandingProvider(OnboardingDbContext db, TemplateSupport support)
    {
        _db = db;
        _support = support;
    }

    public async Task<DocumentBranding?> GetAsync(string templateType, CancellationToken ct = default)
    {
        var org = _support.OrganizationId;
        if (org == Guid.Empty || !PlaceholderCatalog.IsDocumentType(templateType)) return null;

        var template = await _db.DocumentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.OrganizationId == org && t.TemplateType == templateType && t.IsDefault && t.IsActive, ct);
        return template is null ? null : await _support.ToBrandingAsync(template, ct);
    }
}

/// <summary>
/// A50 TPL (D-26) — <see cref="IEmailTemplateRenderer"/>: the org's default, active email template of a type, rendered with the
/// dunning-style {Token} engine (values HTML-encoded in the body, plain in the subject; unknown tokens left as typed). A body
/// without tags is plain text: it is encoded and its line breaks kept. {Message} keeps the sender's line breaks.
/// Null when the org has no template (the caller sends its built-in text).
/// </summary>
internal sealed class EmailTemplateRenderer : IEmailTemplateRenderer
{
    private readonly OnboardingDbContext _db;
    private readonly TemplateSupport _support;

    public EmailTemplateRenderer(OnboardingDbContext db, TemplateSupport support)
    {
        _db = db;
        _support = support;
    }

    public Task<RenderedEmail?> RenderAsync(string templateType, IReadOnlyDictionary<string, string?> placeholders, CancellationToken ct = default) =>
        RenderForAsync(_support.OrganizationId, templateType, placeholders, ct);

    /// <summary>Org-explicit (accept-invite runs without a tenant).</summary>
    internal async Task<RenderedEmail?> RenderForAsync(Guid organizationId, string templateType, IReadOnlyDictionary<string, string?> placeholders, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || !PlaceholderCatalog.IsEmailType(templateType)) return null;

        var template = await _db.EmailTemplates.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.OrganizationId == organizationId && t.TemplateType == templateType && t.IsDefault && t.IsActive, ct);
        return template is null ? null : Render(template.SubjectTemplate, template.BodyHtml, placeholders);
    }

    internal static RenderedEmail Render(string subjectTemplate, string bodyTemplate, IReadOnlyDictionary<string, string?> placeholders)
    {
        var subject = TemplateText.Render(subjectTemplate, placeholders, html: false).Replace("\r", " ").Replace("\n", " ").Trim();
        var body = TemplateText.LooksLikeHtml(bodyTemplate) ? bodyTemplate : TemplateText.PlainToHtml(bodyTemplate);

        Dictionary<string, string>? raw = null;
        var message = placeholders.FirstOrDefault(p => string.Equals(p.Key, "Message", StringComparison.OrdinalIgnoreCase)).Value;
        if (message is not null) raw = new(StringComparer.OrdinalIgnoreCase) { ["Message"] = TemplateText.PlainToHtml(message) };

        return new RenderedEmail(subject, TemplateText.Render(body, placeholders, html: true, raw));
    }
}

/// <summary>The built-in text of each email type, used when the org has no template and as the reset fallback without a pack.</summary>
internal static class BuiltInEmails
{
    public static EmailPackEntry Entry(string type) => type switch
    {
        EmailTemplateTypes.InvoiceSent => new(type, "Invoice to customer", "Invoice {InvoiceNumber} from {CompanyName}",
            "<p>Dear {CustomerName},</p><p>Please find attached invoice <strong>{InvoiceNumber}</strong> dated {InvoiceDate} for {TotalAmount} {CurrencyCode}, due on {DueDate}.</p><p>{Message}</p><p>Regards,<br>{CompanyName}</p>", true),
        EmailTemplateTypes.QuotationSent => new(type, "Quotation to customer", "Quotation {QuotationNumber} from {CompanyName}",
            "<p>Dear {CustomerName},</p><p>Please find attached our quotation <strong>{QuotationNumber}</strong> for {TotalAmount} {CurrencyCode}, valid until {ValidUntil}.</p><p>{Message}</p><p>Regards,<br>{CompanyName}</p>", true),
        EmailTemplateTypes.PoToSupplier => new(type, "Purchase order to supplier", "Purchase Order {PoNumber} from {CompanyName}",
            "<p>Dear {SupplierName},</p><p>Please find attached purchase order <strong>{PoNumber}</strong> dated {PoDate} for {TotalAmount} {CurrencyCode}. Requested delivery: {DeliveryDate}.</p><p>{Message}</p><p>Regards,<br>{CompanyName}</p>", true),
        EmailTemplateTypes.PaymentReminder => new(type, "Payment reminder", "Reminder: invoice {InvoiceNumber} is overdue",
            "<p>Dear {CustomerName},</p><p>Invoice <strong>{InvoiceNumber}</strong> was due on {DueDate}; {AmountDue} {CurrencyCode} is still outstanding ({DaysOverdue} days overdue).</p><p>Regards,<br>{CompanyName}</p>", true),
        EmailTemplateTypes.PaymentReceived => new(type, "Payment received", "Payment received — {PaymentNumber}",
            "<p>Dear {CustomerName},</p><p>Thank you: we have received {AmountPaid} {CurrencyCode} on {PaymentDate} (receipt {PaymentNumber}).</p><p>Regards,<br>{CompanyName}</p>", true),
        EmailTemplateTypes.WelcomeEmail => new(type, "Welcome", "Welcome to {OrganizationName}",
            "<p>Hello {FirstName},</p><p>Your account at <strong>{OrganizationName}</strong> is ready. Sign in here: {LoginUrl}</p><p>Regards,<br>{OrganizationName}</p>", true),
        EmailTemplateTypes.PayslipSent => new(type, "Payslip", "Your payslip for {PayPeriod}",
            "<p>Dear {EmployeeName},</p><p>Your payslip for {PayPeriod} is attached. Net pay: {NetPay} {CurrencyCode}.</p><p>Regards,<br>{CompanyName}</p>", true),
        _ => new(type, type, type, "<p>{Message}</p>", true)
    };
}
