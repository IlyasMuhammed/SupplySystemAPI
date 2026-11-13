using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>
/// A50 TPL (tasks 243–250, D-65) — DOC_TEMPLATES: one template per type from the pack (natural key = templateType). A type
/// the org already has a template of is skipped (BR-50-004), so a re-run and an org that customised its templates are safe.
/// PurchaseOrder: an org with a saved pre-A50 PO letterhead gets that copied instead of the pack's (D-25).
/// </summary>
internal sealed class DocumentTemplatesSetupSeeder : ISetupCategorySeeder
{
    private readonly OnboardingDbContext _db;
    private readonly TemplateSupport _support;

    public DocumentTemplatesSetupSeeder(OnboardingDbContext db, TemplateSupport support)
    {
        _db = db;
        _support = support;
    }

    public string Category => SetupCategories.DocTemplates;
    public string? RequiresModule => null;

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        var errors = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("templates", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ["$.templates: an array of templates is required."];
        var i = 0;
        var seen = new HashSet<string>();
        foreach (var t in arr.EnumerateArray())
        {
            var type = t.TryGetProperty("templateType", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (!PlaceholderCatalog.IsDocumentType(type)) errors.Add($"$.templates[{i}].templateType: unknown type '{type}'.");
            else if (!seen.Add(type!)) errors.Add($"$.templates[{i}].templateType: {type} appears twice.");
            if (!t.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString()))
                errors.Add($"$.templates[{i}].name: required.");
            if (t.TryGetProperty("primaryColor", out var c) && c.ValueKind == JsonValueKind.String && !BrandPalette.IsValid(c.GetString()))
                errors.Add($"$.templates[{i}].primaryColor: must be #RRGGBB.");
            i++;
        }
        if (i == 0) errors.Add("$.templates: at least one template.");
        return errors;
    }

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var entries = TemplatePack.Documents(payload);
        var org = _support.OrganizationId;
        var present = await _db.DocumentTemplates.Where(t => t.OrganizationId == org).Select(t => t.TemplateType).Distinct().ToListAsync(ct);
        return new SetupPreview { Category = Category, InPayload = entries.Count, AlreadyPresent = entries.Count(e => present.Contains(e.TemplateType)) };
    }

    public async Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var org = _support.OrganizationId;
        var entries = TemplatePack.Documents(payload);
        var present = await _db.DocumentTemplates.Where(t => t.OrganizationId == org).Select(t => t.TemplateType).Distinct().ToListAsync(ct);
        var defaults = await _db.DocumentTemplates.Where(t => t.OrganizationId == org && t.IsDefault).Select(t => t.TemplateType).ToListAsync(ct);
        var legacy = entries.Any(e => e.TemplateType == DocumentTemplateTypes.PurchaseOrder) && !present.Contains(DocumentTemplateTypes.PurchaseOrder)
            ? await _support.LetterheadAsync(org, ct) : null;

        var created = new List<DocumentTemplate>();
        var skipped = 0;
        foreach (var e in entries)
        {
            if (present.Contains(e.TemplateType)) { skipped++; continue; }
            DocumentTemplate t;
            if (e.TemplateType == DocumentTemplateTypes.PurchaseOrder && legacy is not null)
            {
                t = TemplateSupport.FromLegacy(org, legacy, context.UserId);
                t.IsDefault = !defaults.Contains(e.TemplateType);
            }
            else
            {
                t = New(org, e, context.UserId);
                t.IsDefault = e.IsDefault && !defaults.Contains(e.TemplateType);
            }
            t.IsSeeded = true;
            _db.DocumentTemplates.Add(t);
            created.Add(t);
            if (t.IsDefault) defaults.Add(t.TemplateType);
        }
        await _db.SaveChangesAsync(ct);

        return new SetupApplyResult
        {
            Category = Category, Created = created.Count, Skipped = skipped,
            Seeded = created.Select(t => new SeededRecordRef(t.TemplateType, t.Uuid.ToString(), TemplateSupport.Fingerprint(t))).ToList(),
        };
    }

    internal static DocumentTemplate New(Guid org, DocumentPackEntry e, int? userId)
    {
        var t = new DocumentTemplate { OrganizationId = org, TemplateType = e.TemplateType, IsActive = true, CreatedBy = userId, ModifiedBy = userId };
        DocumentTemplateService.Restore(t, e);
        return t;
    }

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var org = _support.OrganizationId;
        var rows = await _db.DocumentTemplates.AsNoTracking().Where(t => t.OrganizationId == org).ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var row = Guid.TryParse(s.EntityRef, out var id) ? rows.FirstOrDefault(r => r.Uuid == id) : null;
            if (row is null) missing.Add(s.NaturalKey);
            else if (TemplateSupport.Fingerprint(row) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus { Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = rows.Count, ModifiedKeys = modified, MissingKeys = missing };
    }

    /// <summary>Templates are referenced by nothing (documents render with the current default), so nothing blocks a reset.</summary>
    public Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default) =>
        Task.FromResult(new SetupReferenceCheck());

    public async Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default)
    {
        var org = _support.OrganizationId;
        var entries = TemplatePack.Documents(payload);
        switch (mode)
        {
            case SetupResetMode.Merge:
                return await ApplyAsync(payload, context, ct);

            case SetupResetMode.Overwrite:
            {
                // Seeded rows get the pack's values back (logo kept); custom rows are untouched; missing types are added.
                var refs = new List<SeededRecordRef>();
                var rows = await _db.DocumentTemplates.Where(t => t.OrganizationId == org && t.IsSeeded).ToListAsync(ct);
                var overwritten = 0;
                foreach (var e in entries)
                {
                    var row = rows.FirstOrDefault(r => r.TemplateType == e.TemplateType);
                    if (row is null) continue;
                    DocumentTemplateService.Restore(row, e);
                    row.IsActive = true;
                    row.ModifiedBy = context.UserId;
                    overwritten++;
                    refs.Add(new SeededRecordRef(e.TemplateType, row.Uuid.ToString(), TemplateSupport.Fingerprint(row)));
                }
                await _db.SaveChangesAsync(ct);
                var added = await ApplyAsync(payload, context, ct);
                return new SetupApplyResult { Category = Category, Created = added.Created + overwritten, Skipped = added.Skipped - overwritten,
                                              Seeded = refs.Concat(added.Seeded).ToList() };
            }

            default: // Full: every template of the org goes, then the pack is seeded afresh.
            {
                var all = await _db.DocumentTemplates.Where(t => t.OrganizationId == org).ToListAsync(ct);
                var removedKeys = context.AlreadySeeded.Select(s => s.NaturalKey).ToList();
                _db.DocumentTemplates.RemoveRange(all);
                await _db.SaveChangesAsync(ct);
                var added = await ApplyAsync(payload, context, ct);
                return new SetupApplyResult { Category = Category, Created = added.Created, Removed = all.Count, Seeded = added.Seeded, RemovedKeys = removedKeys };
            }
        }
    }
}

/// <summary>A50 TPL (tasks 259–266) — EMAIL_TEMPLATES, the same rules as DOC_TEMPLATES. PayslipSent is seeded (wired after A49).</summary>
internal sealed class EmailTemplatesSetupSeeder : ISetupCategorySeeder
{
    private readonly OnboardingDbContext _db;
    private readonly TemplateSupport _support;

    public EmailTemplatesSetupSeeder(OnboardingDbContext db, TemplateSupport support)
    {
        _db = db;
        _support = support;
    }

    public string Category => SetupCategories.EmailTemplates;
    public string? RequiresModule => null;

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        var errors = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("templates", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ["$.templates: an array of templates is required."];
        var i = 0;
        var seen = new HashSet<string>();
        foreach (var t in arr.EnumerateArray())
        {
            string? S(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var type = S("templateType");
            if (!PlaceholderCatalog.IsEmailType(type)) errors.Add($"$.templates[{i}].templateType: unknown type '{type}'.");
            else if (!seen.Add(type!)) errors.Add($"$.templates[{i}].templateType: {type} appears twice.");
            if (string.IsNullOrWhiteSpace(S("name"))) errors.Add($"$.templates[{i}].name: required.");
            var subject = S("subjectTemplate");
            if (string.IsNullOrWhiteSpace(subject)) errors.Add($"$.templates[{i}].subjectTemplate: required.");
            else if (subject.IndexOfAny(['\r', '\n']) >= 0) errors.Add($"$.templates[{i}].subjectTemplate: no line breaks.");
            if (string.IsNullOrWhiteSpace(S("bodyHtml"))) errors.Add($"$.templates[{i}].bodyHtml: required.");
            i++;
        }
        if (i == 0) errors.Add("$.templates: at least one template.");
        return errors;
    }

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var entries = TemplatePack.Emails(payload);
        var org = _support.OrganizationId;
        var present = await _db.EmailTemplates.Where(t => t.OrganizationId == org).Select(t => t.TemplateType).Distinct().ToListAsync(ct);
        return new SetupPreview { Category = Category, InPayload = entries.Count, AlreadyPresent = entries.Count(e => present.Contains(e.TemplateType)) };
    }

    public async Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var org = _support.OrganizationId;
        var entries = TemplatePack.Emails(payload);
        var present = await _db.EmailTemplates.Where(t => t.OrganizationId == org).Select(t => t.TemplateType).Distinct().ToListAsync(ct);
        var created = new List<EmailTemplate>();
        var skipped = 0;
        foreach (var e in entries)
        {
            if (present.Contains(e.TemplateType)) { skipped++; continue; }
            var t = new EmailTemplate { OrganizationId = org, TemplateType = e.TemplateType, IsActive = true, IsSeeded = true, IsDefault = true,
                                        CreatedBy = context.UserId, ModifiedBy = context.UserId };
            EmailTemplateService.Restore(t, e);
            _db.EmailTemplates.Add(t);
            created.Add(t);
        }
        await _db.SaveChangesAsync(ct);
        return new SetupApplyResult
        {
            Category = Category, Created = created.Count, Skipped = skipped,
            Seeded = created.Select(t => new SeededRecordRef(t.TemplateType, t.Uuid.ToString(), TemplateSupport.Fingerprint(t))).ToList(),
        };
    }

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var org = _support.OrganizationId;
        var rows = await _db.EmailTemplates.AsNoTracking().Where(t => t.OrganizationId == org).ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var row = Guid.TryParse(s.EntityRef, out var id) ? rows.FirstOrDefault(r => r.Uuid == id) : null;
            if (row is null) missing.Add(s.NaturalKey);
            else if (TemplateSupport.Fingerprint(row) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus { Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = rows.Count, ModifiedKeys = modified, MissingKeys = missing };
    }

    public Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default) =>
        Task.FromResult(new SetupReferenceCheck());

    public async Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default)
    {
        var org = _support.OrganizationId;
        var entries = TemplatePack.Emails(payload);
        switch (mode)
        {
            case SetupResetMode.Merge:
                return await ApplyAsync(payload, context, ct);
            case SetupResetMode.Overwrite:
            {
                var refs = new List<SeededRecordRef>();
                var rows = await _db.EmailTemplates.Where(t => t.OrganizationId == org && t.IsSeeded).ToListAsync(ct);
                foreach (var e in entries)
                {
                    var row = rows.FirstOrDefault(r => r.TemplateType == e.TemplateType);
                    if (row is null) continue;
                    EmailTemplateService.Restore(row, e);
                    row.IsActive = true;
                    row.ModifiedBy = context.UserId;
                    refs.Add(new SeededRecordRef(e.TemplateType, row.Uuid.ToString(), TemplateSupport.Fingerprint(row)));
                }
                await _db.SaveChangesAsync(ct);
                var added = await ApplyAsync(payload, context, ct);
                return new SetupApplyResult { Category = Category, Created = added.Created + refs.Count, Skipped = added.Skipped - refs.Count,
                                              Seeded = refs.Concat(added.Seeded).ToList() };
            }
            default:
            {
                var all = await _db.EmailTemplates.Where(t => t.OrganizationId == org).ToListAsync(ct);
                var removedKeys = context.AlreadySeeded.Select(s => s.NaturalKey).ToList();
                _db.EmailTemplates.RemoveRange(all);
                await _db.SaveChangesAsync(ct);
                var added = await ApplyAsync(payload, context, ct);
                return new SetupApplyResult { Category = Category, Created = added.Created, Removed = all.Count, Seeded = added.Seeded, RemovedKeys = removedKeys };
            }
        }
    }
}
