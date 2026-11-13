using System.Data;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>A50 TPL (FSD §4.6, tasks 233–242) — the org's document templates.</summary>
public interface IDocumentTemplateService
{
    Task<List<DocumentTemplateModel>> ListAsync(string? type, bool includeInactive, int? userId, CancellationToken ct = default);
    Task<DocumentTemplateModel> GetAsync(Guid uuid, CancellationToken ct = default);
    Task<DocumentTemplateModel> CreateAsync(CreateDocumentTemplateRequest request, int? userId, CancellationToken ct = default);
    Task<DocumentTemplateModel> UpdateAsync(Guid uuid, UpdateDocumentTemplateRequest request, int? userId, CancellationToken ct = default);
    Task<DocumentTemplateModel> SetDefaultAsync(Guid uuid, int? userId, CancellationToken ct = default);
    Task<DocumentTemplateModel> ResetToDefaultAsync(string type, int? userId, CancellationToken ct = default);
    /// <summary>A sample document of the template's type as a PDF; <paramref name="unsaved"/> = the editor's values, not saved.</summary>
    Task<byte[]> PreviewAsync(Guid uuid, UpdateDocumentTemplateRequest? unsaved, CancellationToken ct = default);
    IReadOnlyList<PlaceholderModel> Placeholders(string type);
}

internal sealed class DocumentTemplateService : IDocumentTemplateService
{
    internal const int MaxHtml = 100_000;
    internal const int MaxBank = 2000;
    internal const int MaxTerms = 4000;

    private readonly OnboardingDbContext _db;
    private readonly TemplateSupport _support;
    private readonly TemplatedDocumentRenderer _renderer;
    private readonly TimeProvider _clock;

    public DocumentTemplateService(OnboardingDbContext db, TemplateSupport support, TemplatedDocumentRenderer renderer, TimeProvider? clock = null)
    {
        _db = db;
        _support = support;
        _renderer = renderer;
        _clock = clock ?? TimeProvider.System;
    }

    private Guid Org
    {
        get
        {
            var org = _support.OrganizationId;
            if (org == Guid.Empty) throw new BadRequestException("Document templates belong to an organization; sign in to one.");
            return org;
        }
    }

    public async Task<List<DocumentTemplateModel>> ListAsync(string? type, bool includeInactive, int? userId, CancellationToken ct = default)
    {
        if (type is not null) RequireType(type);
        var org = Org;
        await _support.EnsureLegacyPoCopyAsync(userId, ct);

        var rows = await _db.DocumentTemplates.AsNoTracking()
            .Where(t => t.OrganizationId == org && (type == null || t.TemplateType == type) && (includeInactive || t.IsActive))
            .ToListAsync(ct);

        var order = DocumentTemplateTypes.All.ToList();
        var list = new List<DocumentTemplateModel>();
        foreach (var t in rows.OrderBy(t => order.IndexOf(t.TemplateType)).ThenByDescending(t => t.IsDefault).ThenBy(t => t.Name))
            list.Add(await ToModelAsync(t, ct));
        return list;
    }

    public async Task<DocumentTemplateModel> GetAsync(Guid uuid, CancellationToken ct = default) => await ToModelAsync(await FindAsync(uuid, ct), ct);

    public async Task<DocumentTemplateModel> CreateAsync(CreateDocumentTemplateRequest request, int? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireType(request.TemplateType);
        var org = Org;

        var entity = new DocumentTemplate { OrganizationId = org, TemplateType = request.TemplateType, IsActive = true, CreatedBy = userId, ModifiedBy = userId };
        if (request.CloneFromUuid is { } from)
        {
            var source = await FindAsync(from, ct);
            if (source.TemplateType != request.TemplateType)
                throw new BadRequestException($"The template to copy is a {source.TemplateType} template, not a {request.TemplateType} one.");
            entity.Name = $"Copy of {source.Name}";
            entity.HeaderHtml = source.HeaderHtml; entity.BodyHtml = source.BodyHtml; entity.FooterHtml = source.FooterHtml;
            entity.LogoAttachmentUuid = source.LogoAttachmentUuid; entity.PrimaryColor = source.PrimaryColor;
            entity.ShowBankDetails = source.ShowBankDetails; entity.BankDetailsText = source.BankDetailsText;
            entity.TermsAndConditions = source.TermsAndConditions;
        }

        if (!string.IsNullOrWhiteSpace(request.Name)) entity.Name = request.Name.Trim();
        if (request.HeaderHtml is not null) entity.HeaderHtml = TemplateSupport.Blank(request.HeaderHtml);
        if (request.BodyHtml is not null) entity.BodyHtml = TemplateSupport.Blank(request.BodyHtml);
        if (request.FooterHtml is not null) entity.FooterHtml = TemplateSupport.Blank(request.FooterHtml);
        if (request.LogoAttachmentUuid is not null) entity.LogoAttachmentUuid = request.LogoAttachmentUuid;
        if (request.PrimaryColor is not null) entity.PrimaryColor = TemplateSupport.Blank(request.PrimaryColor);
        if (request.ShowBankDetails is { } show) entity.ShowBankDetails = show;
        if (request.BankDetailsText is not null) entity.BankDetailsText = TemplateSupport.Blank(request.BankDetailsText);
        if (request.TermsAndConditions is not null) entity.TermsAndConditions = TemplateSupport.Blank(request.TermsAndConditions);
        if (string.IsNullOrWhiteSpace(entity.Name)) throw new BadRequestException("A template needs a name.");

        await ValidateAsync(entity, ct);
        _db.DocumentTemplates.Add(entity);
        await _db.SaveChangesAsync(ct);

        // The first template of a type is its default even when not asked (a type always has one once it has any).
        var hasDefault = await _db.DocumentTemplates.AnyAsync(t => t.OrganizationId == org && t.TemplateType == entity.TemplateType && t.IsDefault && t.Id != entity.Id, ct);
        if (request.IsDefault || !hasDefault) await MakeDefaultAsync(entity, userId, ct);
        return await ToModelAsync(entity, ct);
    }

    public async Task<DocumentTemplateModel> UpdateAsync(Guid uuid, UpdateDocumentTemplateRequest request, int? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = await FindAsync(uuid, ct, tracked: true);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new BadRequestException("A template needs a name.");
        if (!request.IsActive && entity.IsDefault) throw new BadRequestException("The default template of a type cannot be deactivated; make another one the default first.");

        Apply(entity, request);
        entity.ModifiedBy = userId;
        await ValidateAsync(entity, ct);
        await _db.SaveChangesAsync(ct);
        return await ToModelAsync(entity, ct);
    }

    public async Task<DocumentTemplateModel> SetDefaultAsync(Guid uuid, int? userId, CancellationToken ct = default)
    {
        var entity = await FindAsync(uuid, ct, tracked: true);
        if (!entity.IsActive) throw new BadRequestException("An inactive template cannot be the default.");
        await MakeDefaultAsync(entity, userId, ct);
        return await ToModelAsync(entity, ct);
    }

    public async Task<DocumentTemplateModel> ResetToDefaultAsync(string type, int? userId, CancellationToken ct = default)
    {
        RequireType(type);
        var org = Org;
        var pack = await _support.DocumentPackEntryAsync(org, type, ct);

        var seeded = await _db.DocumentTemplates.Where(t => t.OrganizationId == org && t.TemplateType == type && t.IsSeeded)
            .OrderBy(t => t.Id).FirstOrDefaultAsync(ct);
        if (seeded is null)
        {
            seeded = new DocumentTemplate { OrganizationId = org, TemplateType = type, IsSeeded = true, CreatedBy = userId };
            _db.DocumentTemplates.Add(seeded);
        }
        Restore(seeded, pack);
        seeded.IsActive = true;
        seeded.ModifiedBy = userId;
        await _db.SaveChangesAsync(ct);
        await MakeDefaultAsync(seeded, userId, ct);
        return await ToModelAsync(seeded, ct);
    }

    /// <summary>The pack's values; the logo is kept (packs carry none).</summary>
    internal static void Restore(DocumentTemplate t, DocumentPackEntry pack)
    {
        t.Name = pack.Name;
        t.HeaderHtml = pack.HeaderHtml;
        t.BodyHtml = pack.BodyHtml;
        t.FooterHtml = pack.FooterHtml;
        t.PrimaryColor = pack.PrimaryColor;
        t.ShowBankDetails = pack.ShowBankDetails;
        t.BankDetailsText = pack.BankDetailsText;
        t.TermsAndConditions = pack.TermsAndConditions;
    }

    public async Task<byte[]> PreviewAsync(Guid uuid, UpdateDocumentTemplateRequest? unsaved, CancellationToken ct = default)
    {
        var stored = await FindAsync(uuid, ct);
        var t = Copy(stored);
        if (unsaved is not null)
        {
            Apply(t, unsaved);
            if (!string.IsNullOrWhiteSpace(unsaved.Name)) t.Name = unsaved.Name.Trim();
            await ValidateAsync(t, ct);
        }

        var branding = await _support.ToBrandingAsync(t, ct);
        var sample = PlaceholderCatalog.SampleDocument(t.TemplateType, _clock.GetUtcNow().UtcDateTime);
        // The org's own letterhead wins over the sample company where it has one.
        foreach (var (key, value) in new Dictionary<string, string?>
                 {
                     ["CompanyName"] = branding.CompanyName, ["CompanyAddress"] = branding.CompanyAddress, ["CompanyPhone"] = branding.CompanyPhone,
                     ["CompanyEmail"] = branding.CompanyEmail, ["CompanyTaxId"] = branding.CompanyTaxId,
                 })
            if (!string.IsNullOrWhiteSpace(value)) sample.Values[key] = value;

        var withCompany = new DocumentBranding
        {
            TemplateType = branding.TemplateType, LogoBytes = branding.LogoBytes, PrimaryColor = branding.PrimaryColor,
            HeaderHtml = branding.HeaderHtml, BodyHtml = branding.BodyHtml, FooterHtml = branding.FooterHtml,
            ShowBankDetails = branding.ShowBankDetails,
            BankDetailsText = branding.BankDetailsText ?? (branding.ShowBankDetails ? "Bank: (your bank details)\nIBAN: (your IBAN)" : null),
            TermsAndConditions = branding.TermsAndConditions,
            CompanyName = sample.Values["CompanyName"], CompanyAddress = sample.Values["CompanyAddress"], CompanyPhone = sample.Values["CompanyPhone"],
            CompanyEmail = sample.Values["CompanyEmail"], CompanyTaxId = sample.Values["CompanyTaxId"],
        };
        return _renderer.RenderBody(withCompany, sample, withCompany.BodyHtml);
    }

    public IReadOnlyList<PlaceholderModel> Placeholders(string type)
    {
        RequireType(type);
        return PlaceholderCatalog.ForDocument(type);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void Apply(DocumentTemplate t, UpdateDocumentTemplateRequest r)
    {
        t.Name = string.IsNullOrWhiteSpace(r.Name) ? t.Name : r.Name.Trim();
        t.HeaderHtml = TemplateSupport.Blank(r.HeaderHtml);
        t.BodyHtml = TemplateSupport.Blank(r.BodyHtml);
        t.FooterHtml = TemplateSupport.Blank(r.FooterHtml);
        t.LogoAttachmentUuid = r.LogoAttachmentUuid;
        t.PrimaryColor = TemplateSupport.Blank(r.PrimaryColor);
        t.ShowBankDetails = r.ShowBankDetails;
        t.BankDetailsText = TemplateSupport.Blank(r.BankDetailsText);
        t.TermsAndConditions = TemplateSupport.Blank(r.TermsAndConditions);
        t.IsActive = r.IsActive;
    }

    private static DocumentTemplate Copy(DocumentTemplate s) => new()
    {
        Id = s.Id, Uuid = s.Uuid, OrganizationId = s.OrganizationId, TemplateType = s.TemplateType, Name = s.Name,
        HeaderHtml = s.HeaderHtml, BodyHtml = s.BodyHtml, FooterHtml = s.FooterHtml, LogoAttachmentUuid = s.LogoAttachmentUuid,
        PrimaryColor = s.PrimaryColor, ShowBankDetails = s.ShowBankDetails, BankDetailsText = s.BankDetailsText,
        TermsAndConditions = s.TermsAndConditions, IsDefault = s.IsDefault, IsSeeded = s.IsSeeded, IsActive = s.IsActive,
    };

    private async Task ValidateAsync(DocumentTemplate t, CancellationToken ct)
    {
        var errors = new List<string>();
        if (t.Name.Length > 200) errors.Add("The name is longer than 200 characters.");
        if (t.PrimaryColor is not null && !BrandPalette.IsValid(t.PrimaryColor)) errors.Add("The colour must be #RRGGBB.");
        if ((t.BankDetailsText?.Length ?? 0) > MaxBank) errors.Add($"The bank details are longer than {MaxBank} characters.");
        if ((t.TermsAndConditions?.Length ?? 0) > MaxTerms) errors.Add($"The terms are longer than {MaxTerms} characters.");
        foreach (var (label, html) in new[] { ("header", t.HeaderHtml), ("body", t.BodyHtml), ("footer", t.FooterHtml) })
            if ((html?.Length ?? 0) > MaxHtml) errors.Add($"The {label} is longer than {MaxHtml:N0} characters.");
        if (t.LogoAttachmentUuid is { } logo && !await _support.LogoExistsAsync(logo, ct))
            errors.Add("The logo must be a picture uploaded for document templates (DOC_TEMPLATE_LOGO).");
        if (errors.Count > 0) throw new BadRequestException(string.Join(" ", errors));
    }

    /// <summary>Clears the type's other default, then sets this one — two saves in one transaction (the filtered unique index).</summary>
    private async Task MakeDefaultAsync(DocumentTemplate entity, int? userId, CancellationToken ct)
    {
        var relational = _db.Database.IsRelational();
        // The SQL Server retrying strategy refuses a user transaction unless the whole unit runs inside the strategy.
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = relational ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;

            var others = await _db.DocumentTemplates
                .Where(t => t.OrganizationId == entity.OrganizationId && t.TemplateType == entity.TemplateType && t.IsDefault && t.Id != entity.Id)
                .ToListAsync(ct);
            foreach (var o in others) { o.IsDefault = false; o.ModifiedBy = userId; }
            await _db.SaveChangesAsync(ct);

            var tracked = _db.DocumentTemplates.Local.FirstOrDefault(t => t.Id == entity.Id) ?? entity;
            if (_db.Entry(tracked).State == EntityState.Detached) _db.DocumentTemplates.Attach(tracked);
            tracked.IsDefault = true;
            tracked.ModifiedBy = userId;
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        });
        entity.IsDefault = true;
    }

    private async Task<DocumentTemplate> FindAsync(Guid uuid, CancellationToken ct, bool tracked = false)
    {
        var org = Org;
        var q = _db.DocumentTemplates.Where(t => t.Uuid == uuid && t.OrganizationId == org);
        return await (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("DocumentTemplate", uuid);
    }

    private async Task<DocumentTemplateModel> ToModelAsync(DocumentTemplate t, CancellationToken ct) => new()
    {
        Uuid = t.Uuid, TemplateType = t.TemplateType, Name = t.Name, HeaderHtml = t.HeaderHtml, BodyHtml = t.BodyHtml, FooterHtml = t.FooterHtml,
        LogoAttachmentUuid = t.LogoAttachmentUuid, LogoUrl = await _support.LogoUrlAsync(t.LogoAttachmentUuid, ct), PrimaryColor = t.PrimaryColor,
        ShowBankDetails = t.ShowBankDetails, BankDetailsText = t.BankDetailsText, TermsAndConditions = t.TermsAndConditions,
        IsDefault = t.IsDefault, IsSeeded = t.IsSeeded, IsActive = t.IsActive, CreatedAt = t.CreatedAt, ModifiedAt = t.ModifiedAt,
    };

    internal static void RequireType(string? type)
    {
        if (!PlaceholderCatalog.IsDocumentType(type))
            throw new BadRequestException($"Unknown document template type '{type}'. Allowed: {string.Join(", ", DocumentTemplateTypes.All)}.");
    }
}
