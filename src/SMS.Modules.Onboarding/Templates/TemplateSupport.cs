using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Modules.Onboarding.Services.Defaults;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>
/// A50 TPL — what the template services, seeders and providers share: the org's DOC_TEMPLATES / EMAIL_TEMPLATES payload
/// (applied snapshot first, else today's resolution, D-69), fingerprints for provenance, the template → branding mapping and
/// the one-time copy of the pre-A50 PO letterhead (D-25).
/// </summary>
internal sealed class TemplateSupport
{
    public const string LogoInterfaceCode = "DOC_TEMPLATE_LOGO";

    private readonly OnboardingDbContext _db;
    private readonly Func<ISetupDefaultsService> _defaults;
    private readonly ILegacyLetterheadSource _letterhead;
    private readonly IDocumentAssetReader _assets;
    private readonly IOrganizationNameLookup? _orgNames;

    /// <summary>
    /// DI: the defaults service is resolved lazily — SetupDefaultsService validates payloads with every ISetupCategorySeeder,
    /// and the template seeders use this class, so an eager dependency would be a cycle (QA50-1).
    /// </summary>
    public TemplateSupport(
        OnboardingDbContext db, IServiceProvider services, ILegacyLetterheadSource? letterhead = null,
        IDocumentAssetReader? assets = null, IOrganizationNameLookup? orgNames = null)
        : this(db, () => (ISetupDefaultsService)services.GetService(typeof(ISetupDefaultsService))!, letterhead, assets, orgNames) { }

    /// <summary>Tests.</summary>
    internal TemplateSupport(
        OnboardingDbContext db, ISetupDefaultsService defaults, ILegacyLetterheadSource? letterhead = null,
        IDocumentAssetReader? assets = null, IOrganizationNameLookup? orgNames = null)
        : this(db, () => defaults, letterhead, assets, orgNames) { }

    private TemplateSupport(
        OnboardingDbContext db, Func<ISetupDefaultsService> defaults, ILegacyLetterheadSource? letterhead,
        IDocumentAssetReader? assets, IOrganizationNameLookup? orgNames)
    {
        _db = db;
        _defaults = defaults;
        _letterhead = letterhead ?? new NullLegacyLetterheadSource();
        _assets = assets ?? new NullDocumentAssetReader();
        _orgNames = orgNames;
    }

    public Guid OrganizationId => _db.TenantContext.OrganizationId;

    // ── Packs ────────────────────────────────────────────────────────────────

    /// <summary>The payload the org applied for the category (its snapshot), else what its country / industry resolves to now.</summary>
    public async Task<JsonElement?> PayloadAsync(Guid organizationId, string category, CancellationToken ct)
    {
        var setup = await _db.OrganizationSetups.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);

        if (!string.IsNullOrWhiteSpace(setup?.DefaultsSnapshotJson))
        {
            try
            {
                using var snapshot = JsonDocument.Parse(setup.DefaultsSnapshotJson);
                if (snapshot.RootElement.TryGetProperty(category, out var entry) && entry.TryGetProperty("payload", out var payload)
                    && payload.ValueKind == JsonValueKind.Object)
                    return payload.Clone();
            }
            catch (JsonException) { /* a damaged snapshot falls back to the resolution */ }
        }

        var resolved = await _defaults().ResolveAsync(setup?.CountryCode, setup?.IndustryType, category, ct);
        return resolved is null ? null : SetupDefaultsService.Parse(resolved.Row.DataPayload);
    }

    public async Task<DocumentPackEntry> DocumentPackEntryAsync(Guid organizationId, string type, CancellationToken ct)
    {
        var payload = await PayloadAsync(organizationId, SetupCategories.DocTemplates, ct);
        return (payload is { } p ? TemplatePack.Documents(p).FirstOrDefault(e => e.TemplateType == type) : null)
               ?? new DocumentPackEntry(type, $"Standard {Humanize(type)}", null, null, null, null, type == DocumentTemplateTypes.SalesInvoice, null, null, true);
    }

    public async Task<EmailPackEntry> EmailPackEntryAsync(Guid organizationId, string type, CancellationToken ct)
    {
        var payload = await PayloadAsync(organizationId, SetupCategories.EmailTemplates, ct);
        return (payload is { } p ? TemplatePack.Emails(p).FirstOrDefault(e => e.TemplateType == type) : null)
               ?? BuiltInEmails.Entry(type);
    }

    public static string Humanize(string type) => Regex.Replace(type, "(?<=[a-z])(?=[A-Z])", " ");

    // ── Fingerprints (D-5) ──────────────────────────────────────────────────────

    public static string Fingerprint(DocumentTemplate t) => Hash(t.TemplateType, t.Name, t.HeaderHtml, t.BodyHtml, t.FooterHtml, t.PrimaryColor,
        t.ShowBankDetails ? "1" : "0", t.BankDetailsText, t.TermsAndConditions);

    public static string Fingerprint(EmailTemplate t) => Hash(t.TemplateType, t.Name, t.SubjectTemplate, t.BodyHtml);

    private static string Hash(params string?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001F", parts.Select(p => p ?? "\u0000"))));
        return Convert.ToHexString(bytes)[..32];
    }

    // ── Branding ───────────────────────────────────────────────────────────────

    public async Task<DocumentBranding> ToBrandingAsync(DocumentTemplate t, CancellationToken ct, LegacyLetterhead? letterhead = null, bool letterheadLoaded = false)
    {
        if (!letterheadLoaded) letterhead = await LetterheadAsync(t.OrganizationId, ct);
        byte[]? logo = null;
        if (t.LogoAttachmentUuid is { } logoId)
            logo = (await _assets.ReadAsync(logoId, LogoInterfaceCode, ct))?.Content;

        var name = Blank(letterhead?.CompanyName) ?? (_orgNames is null ? null : await _orgNames.GetNameAsync(t.OrganizationId, ct));
        return new DocumentBranding
        {
            TemplateType = t.TemplateType,
            LogoBytes = logo,
            PrimaryColor = Blank(t.PrimaryColor),
            HeaderHtml = Blank(t.HeaderHtml),
            BodyHtml = Blank(t.BodyHtml),
            FooterHtml = Blank(t.FooterHtml),
            ShowBankDetails = t.ShowBankDetails,
            BankDetailsText = Blank(t.BankDetailsText),
            TermsAndConditions = Blank(t.TermsAndConditions),
            CompanyName = name,
            CompanyAddress = Blank(letterhead?.CompanyAddress),
            CompanyTaxId = Blank(letterhead?.CompanyTaxId),
            CompanyPhone = Blank(letterhead?.CompanyPhone),
            CompanyEmail = Blank(letterhead?.CompanyEmail),
        };
    }

    /// <summary>The letterhead of the ambient org (the Lookups source reads through the tenant filter).</summary>
    public async Task<LegacyLetterhead?> LetterheadAsync(Guid organizationId, CancellationToken ct) =>
        organizationId == _db.TenantContext.OrganizationId
            ? await _letterhead.GetAsync(ct)
            : await _letterhead.GetForOrganizationAsync(organizationId, ct);

    public async Task<string?> LogoUrlAsync(Guid? attachmentUuid, CancellationToken ct) =>
        attachmentUuid is { } id ? (await _assets.ReadAsync(id, LogoInterfaceCode, ct))?.FileUrl : null;

    public async Task<bool> LogoExistsAsync(Guid attachmentUuid, CancellationToken ct) =>
        await _assets.ReadAsync(attachmentUuid, LogoInterfaceCode, ct) is not null;

    // ── The one-time PO copy (D-25) ─────────────────────────────────────────────

    private static readonly (string Old, string New)[] PoTokenMap =
    [
        ("{{LineItemsTable}}", "{LineItems}"), ("{{SignatureBlock}}", "{SignatureBlock}"),
    ];

    /// <summary>The pre-A50 PO body with its {{Token}} placeholders turned into {Token}.</summary>
    public static string? ConvertLegacyBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        foreach (var (o, n) in PoTokenMap) body = body.Replace(o, n, StringComparison.Ordinal);
        return Regex.Replace(body, @"\{\{([A-Za-z][A-Za-z0-9]*)\}\}", "{$1}");
    }

    /// <summary>
    /// When the ambient org has no PurchaseOrder template and has a saved PoDocumentTemplate, copies the letterhead into a
    /// default PurchaseOrder template (once: afterwards a template exists). Returns the template created, or null.
    /// </summary>
    public async Task<DocumentTemplate?> EnsureLegacyPoCopyAsync(int? userId, CancellationToken ct)
    {
        var org = OrganizationId;
        if (org == Guid.Empty) return null;
        if (await _db.DocumentTemplates.AnyAsync(t => t.OrganizationId == org && t.TemplateType == DocumentTemplateTypes.PurchaseOrder, ct)) return null;

        var legacy = await _letterhead.GetAsync(ct);
        if (legacy is null) return null;

        var copy = FromLegacy(org, legacy, userId);
        _db.DocumentTemplates.Add(copy);
        await _db.SaveChangesAsync(ct);
        return copy;
    }

    /// <summary>
    /// REV50-12 — the copy carries no body, header, footer, colour or logo: the PO keeps rendering the pre-A50 letter (its
    /// PoDocumentTemplate body, footer text and logo) exactly as before, so opening the template list changes no PO PDF. The
    /// admin brands it by editing this template; a body pasted here (e.g. <see cref="ConvertLegacyBody"/> of the old one)
    /// then switches the PO to the templated renderer.
    /// </summary>
    public static DocumentTemplate FromLegacy(Guid org, LegacyLetterhead legacy, int? userId) => new()
    {
        OrganizationId = org,
        TemplateType = DocumentTemplateTypes.PurchaseOrder,
        Name = "Purchase Order (from PO template)",
        BodyHtml = null,
        FooterHtml = null,
        ShowBankDetails = !string.IsNullOrWhiteSpace(legacy.BankDetails),
        BankDetailsText = Blank(legacy.BankDetails),
        IsDefault = true,
        IsSeeded = false,
        IsActive = true,
        CreatedBy = userId,
        ModifiedBy = userId,
    };

    public static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
