using System.Text.Json;

namespace SMS.Modules.Onboarding.Templates;

// A50 TPL — request / response shapes of api/onboarding/document-templates and api/onboarding/email-templates
// (docs/onboarding/API-CONTRACT.md "D-TPL").

public sealed class DocumentTemplateModel
{
    public Guid      Uuid               { get; set; }
    public string    TemplateType       { get; set; } = string.Empty;
    public string    Name               { get; set; } = string.Empty;
    public string?   HeaderHtml         { get; set; }
    public string?   BodyHtml           { get; set; }
    public string?   FooterHtml         { get; set; }
    public Guid?     LogoAttachmentUuid { get; set; }
    public string?   LogoUrl            { get; set; }
    public string?   PrimaryColor       { get; set; }
    public bool      ShowBankDetails    { get; set; }
    public string?   BankDetailsText    { get; set; }
    public string?   TermsAndConditions { get; set; }
    public bool      IsDefault          { get; set; }
    public bool      IsSeeded           { get; set; }
    public bool      IsActive           { get; set; }
    public DateTime  CreatedAt          { get; set; }
    public DateTime  ModifiedAt         { get; set; }
}

/// <summary>PUT body — and the optional POST …/preview body (unsaved editor values).</summary>
public class UpdateDocumentTemplateRequest
{
    public string?   Name               { get; set; }
    public string?   HeaderHtml         { get; set; }
    public string?   BodyHtml           { get; set; }
    public string?   FooterHtml         { get; set; }
    public Guid?     LogoAttachmentUuid { get; set; }
    public string?   PrimaryColor       { get; set; }
    public bool      ShowBankDetails    { get; set; }
    public string?   BankDetailsText    { get; set; }
    public string?   TermsAndConditions { get; set; }
    public bool      IsActive           { get; set; } = true;
}

public sealed class CreateDocumentTemplateRequest
{
    public string    TemplateType       { get; set; } = string.Empty;
    public string?   Name               { get; set; }
    public Guid?     CloneFromUuid      { get; set; }
    public string?   HeaderHtml         { get; set; }
    public string?   BodyHtml           { get; set; }
    public string?   FooterHtml         { get; set; }
    public Guid?     LogoAttachmentUuid { get; set; }
    public string?   PrimaryColor       { get; set; }
    public bool?     ShowBankDetails    { get; set; }
    public string?   BankDetailsText    { get; set; }
    public string?   TermsAndConditions { get; set; }
    public bool      IsDefault          { get; set; }
}

public sealed class EmailTemplateModel
{
    public Guid     Uuid            { get; set; }
    public string   TemplateType    { get; set; } = string.Empty;
    public string   Name            { get; set; } = string.Empty;
    public string   SubjectTemplate { get; set; } = string.Empty;
    public string   BodyHtml        { get; set; } = string.Empty;
    public bool     IsDefault       { get; set; }
    public bool     IsSeeded        { get; set; }
    public bool     IsActive        { get; set; }
    public DateTime CreatedAt       { get; set; }
    public DateTime ModifiedAt      { get; set; }
}

public sealed class UpdateEmailTemplateRequest
{
    public string? Name            { get; set; }
    public string? SubjectTemplate { get; set; }
    public string? BodyHtml        { get; set; }
    public bool    IsActive        { get; set; } = true;
}

public sealed class CreateEmailTemplateRequest
{
    public string  TemplateType    { get; set; } = string.Empty;
    public string? Name            { get; set; }
    public string? SubjectTemplate { get; set; }
    public string? BodyHtml        { get; set; }
    public Guid?   CloneFromUuid   { get; set; }
    public bool    IsDefault       { get; set; }
}

public sealed class SendTestEmailRequest
{
    public string? ToEmail { get; set; }
}

public sealed record SendTestEmailResult(bool Sent, string ToEmail, string Subject, string? Error);

public sealed class PreviewEmailRequest
{
    public Dictionary<string, string?>? Placeholders { get; set; }
}

public sealed record EmailPreviewModel(string Subject, string BodyHtml);

// ── Pack entries (DATA's DOC_TEMPLATES / EMAIL_TEMPLATES payloads, docs/onboarding/PAYLOAD-SCHEMAS.md) ───────────

internal sealed record DocumentPackEntry(
    string TemplateType, string Name, string? HeaderHtml, string? BodyHtml, string? FooterHtml, string? PrimaryColor,
    bool ShowBankDetails, string? BankDetailsText, string? TermsAndConditions, bool IsDefault);

internal sealed record EmailPackEntry(string TemplateType, string Name, string SubjectTemplate, string BodyHtml, bool IsDefault);

internal static class TemplatePack
{
    public static List<DocumentPackEntry> Documents(JsonElement payload)
    {
        var list = new List<DocumentPackEntry>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("templates", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in arr.EnumerateArray())
        {
            var type = Str(t, "templateType");
            if (!PlaceholderCatalog.IsDocumentType(type)) continue;
            list.Add(new DocumentPackEntry(type!, Str(t, "name") ?? $"Standard {type}", Blank(Str(t, "headerHtml")), Blank(Str(t, "bodyHtml")),
                Blank(Str(t, "footerHtml")), Blank(Str(t, "primaryColor")), Bool(t, "showBankDetails") ?? false, Blank(Str(t, "bankDetailsText")),
                Blank(Str(t, "termsAndConditions")), Bool(t, "isDefault") ?? true));
        }
        return list;
    }

    public static List<EmailPackEntry> Emails(JsonElement payload)
    {
        var list = new List<EmailPackEntry>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("templates", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in arr.EnumerateArray())
        {
            var type = Str(t, "templateType");
            if (!PlaceholderCatalog.IsEmailType(type)) continue;
            var subject = Str(t, "subjectTemplate");
            var body = Str(t, "bodyHtml");
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body)) continue;
            list.Add(new EmailPackEntry(type!, Str(t, "name") ?? type!, subject, body, Bool(t, "isDefault") ?? true));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
