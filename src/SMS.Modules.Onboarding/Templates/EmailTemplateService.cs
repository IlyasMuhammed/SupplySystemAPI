using System.Data;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>A50 TPL (FSD §4.7, tasks 254–258) — the org's email templates.</summary>
public interface IEmailTemplateService
{
    Task<List<EmailTemplateModel>> ListAsync(string? type, bool includeInactive, CancellationToken ct = default);
    Task<EmailTemplateModel> GetAsync(Guid uuid, CancellationToken ct = default);
    Task<EmailTemplateModel> CreateAsync(CreateEmailTemplateRequest request, int? userId, CancellationToken ct = default);
    Task<EmailTemplateModel> UpdateAsync(Guid uuid, UpdateEmailTemplateRequest request, int? userId, CancellationToken ct = default);
    Task<EmailTemplateModel> SetDefaultAsync(Guid uuid, int? userId, CancellationToken ct = default);
    Task<EmailTemplateModel> ResetToDefaultAsync(string type, int? userId, CancellationToken ct = default);
    Task<EmailPreviewModel> PreviewAsync(Guid uuid, IReadOnlyDictionary<string, string?>? placeholders, CancellationToken ct = default);
    Task<SendTestEmailResult> SendTestAsync(Guid uuid, string? toEmail, CancellationToken ct = default);
    IReadOnlyList<PlaceholderModel> Placeholders(string type);
}

internal sealed class EmailTemplateService : IEmailTemplateService
{
    internal const int MaxSubject = 300;
    internal const int MaxBody = 100_000;

    private readonly OnboardingDbContext _db;
    private readonly TemplateSupport _support;
    private readonly IEmailSender _sender;

    public EmailTemplateService(OnboardingDbContext db, TemplateSupport support, IEmailSender sender)
    {
        _db = db;
        _support = support;
        _sender = sender;
    }

    private Guid Org
    {
        get
        {
            var org = _support.OrganizationId;
            if (org == Guid.Empty) throw new BadRequestException("Email templates belong to an organization; sign in to one.");
            return org;
        }
    }

    public async Task<List<EmailTemplateModel>> ListAsync(string? type, bool includeInactive, CancellationToken ct = default)
    {
        if (type is not null) RequireType(type);
        var org = Org;
        var rows = await _db.EmailTemplates.AsNoTracking()
            .Where(t => t.OrganizationId == org && (type == null || t.TemplateType == type) && (includeInactive || t.IsActive)).ToListAsync(ct);
        var order = EmailTemplateTypes.All.ToList();
        return rows.OrderBy(t => order.IndexOf(t.TemplateType)).ThenByDescending(t => t.IsDefault).ThenBy(t => t.Name).Select(ToModel).ToList();
    }

    public async Task<EmailTemplateModel> GetAsync(Guid uuid, CancellationToken ct = default) => ToModel(await FindAsync(uuid, ct));

    public async Task<EmailTemplateModel> CreateAsync(CreateEmailTemplateRequest request, int? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireType(request.TemplateType);
        var org = Org;
        var entity = new EmailTemplate { OrganizationId = org, TemplateType = request.TemplateType, IsActive = true, CreatedBy = userId, ModifiedBy = userId };

        if (request.CloneFromUuid is { } from)
        {
            var source = await FindAsync(from, ct);
            if (source.TemplateType != request.TemplateType)
                throw new BadRequestException($"The template to copy is a {source.TemplateType} template, not a {request.TemplateType} one.");
            entity.Name = $"Copy of {source.Name}";
            entity.SubjectTemplate = source.SubjectTemplate;
            entity.BodyHtml = source.BodyHtml;
        }
        if (!string.IsNullOrWhiteSpace(request.Name)) entity.Name = request.Name.Trim();
        if (!string.IsNullOrWhiteSpace(request.SubjectTemplate)) entity.SubjectTemplate = request.SubjectTemplate.Trim();
        if (!string.IsNullOrWhiteSpace(request.BodyHtml)) entity.BodyHtml = request.BodyHtml;
        Validate(entity);

        _db.EmailTemplates.Add(entity);
        await _db.SaveChangesAsync(ct);
        var hasDefault = await _db.EmailTemplates.AnyAsync(t => t.OrganizationId == org && t.TemplateType == entity.TemplateType && t.IsDefault && t.Id != entity.Id, ct);
        if (request.IsDefault || !hasDefault) await MakeDefaultAsync(entity, userId, ct);
        return ToModel(entity);
    }

    public async Task<EmailTemplateModel> UpdateAsync(Guid uuid, UpdateEmailTemplateRequest request, int? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entity = await FindAsync(uuid, ct, tracked: true);
        if (!request.IsActive && entity.IsDefault) throw new BadRequestException("The default template of a type cannot be deactivated; make another one the default first.");
        entity.Name = request.Name?.Trim() ?? string.Empty;
        entity.SubjectTemplate = request.SubjectTemplate?.Trim() ?? string.Empty;
        entity.BodyHtml = request.BodyHtml ?? string.Empty;
        entity.IsActive = request.IsActive;
        entity.ModifiedBy = userId;
        Validate(entity);
        await _db.SaveChangesAsync(ct);
        return ToModel(entity);
    }

    public async Task<EmailTemplateModel> SetDefaultAsync(Guid uuid, int? userId, CancellationToken ct = default)
    {
        var entity = await FindAsync(uuid, ct, tracked: true);
        if (!entity.IsActive) throw new BadRequestException("An inactive template cannot be the default.");
        await MakeDefaultAsync(entity, userId, ct);
        return ToModel(entity);
    }

    public async Task<EmailTemplateModel> ResetToDefaultAsync(string type, int? userId, CancellationToken ct = default)
    {
        RequireType(type);
        var org = Org;
        var pack = await _support.EmailPackEntryAsync(org, type, ct);
        var seeded = await _db.EmailTemplates.Where(t => t.OrganizationId == org && t.TemplateType == type && t.IsSeeded).OrderBy(t => t.Id).FirstOrDefaultAsync(ct);
        if (seeded is null)
        {
            seeded = new EmailTemplate { OrganizationId = org, TemplateType = type, IsSeeded = true, CreatedBy = userId };
            _db.EmailTemplates.Add(seeded);
        }
        Restore(seeded, pack);
        seeded.IsActive = true;
        seeded.ModifiedBy = userId;
        await _db.SaveChangesAsync(ct);
        await MakeDefaultAsync(seeded, userId, ct);
        return ToModel(seeded);
    }

    internal static void Restore(EmailTemplate t, EmailPackEntry pack)
    {
        t.Name = pack.Name;
        t.SubjectTemplate = pack.SubjectTemplate;
        t.BodyHtml = pack.BodyHtml;
    }

    public async Task<EmailPreviewModel> PreviewAsync(Guid uuid, IReadOnlyDictionary<string, string?>? placeholders, CancellationToken ct = default)
    {
        var t = await FindAsync(uuid, ct);
        var values = await SampleValuesAsync(t.TemplateType, ct);
        if (placeholders is not null) foreach (var (k, v) in placeholders) values[k] = v;
        var rendered = EmailTemplateRenderer.Render(t.SubjectTemplate, t.BodyHtml, values);
        return new EmailPreviewModel(rendered.Subject, rendered.BodyHtml);
    }

    public async Task<SendTestEmailResult> SendTestAsync(Guid uuid, string? toEmail, CancellationToken ct = default)
    {
        var to = toEmail?.Trim() ?? string.Empty;
        if (!DocumentEmailAddresses.IsValid(to)) throw new BadRequestException("Enter one valid email address to send the test to.");

        var preview = await PreviewAsync(uuid, null, ct);
        var subject = "[TEST] " + preview.Subject;
        var result = await _sender.SendWithResultAsync(to, subject, preview.BodyHtml, null, ct)
                     ?? EmailSendResult.Failed("The email sender gave no result.");
        return new SendTestEmailResult(result.Success, to, subject, result.Success ? null : result.Error);
    }

    public IReadOnlyList<PlaceholderModel> Placeholders(string type)
    {
        RequireType(type);
        return PlaceholderCatalog.ForEmail(type);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The sample values, with the org's own company block where it has one.</summary>
    private async Task<Dictionary<string, string?>> SampleValuesAsync(string type, CancellationToken ct)
    {
        var values = PlaceholderCatalog.EmailSamples(type);
        var letterhead = await _support.LetterheadAsync(Org, ct);
        if (!string.IsNullOrWhiteSpace(letterhead?.CompanyName)) values["CompanyName"] = letterhead.CompanyName;
        if (!string.IsNullOrWhiteSpace(letterhead?.CompanyEmail)) values["CompanyEmail"] = letterhead.CompanyEmail;
        if (!string.IsNullOrWhiteSpace(letterhead?.CompanyPhone)) values["CompanyPhone"] = letterhead.CompanyPhone;
        return values;
    }

    private static void Validate(EmailTemplate t)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(t.Name)) errors.Add("A template needs a name.");
        else if (t.Name.Length > 200) errors.Add("The name is longer than 200 characters.");
        if (string.IsNullOrWhiteSpace(t.SubjectTemplate)) errors.Add("A template needs a subject.");
        else if (t.SubjectTemplate.Length > MaxSubject) errors.Add($"The subject is longer than {MaxSubject} characters.");
        else if (t.SubjectTemplate.IndexOfAny(['\r', '\n']) >= 0) errors.Add("The subject cannot contain line breaks.");
        if (string.IsNullOrWhiteSpace(t.BodyHtml)) errors.Add("A template needs a body.");
        else if (t.BodyHtml.Length > MaxBody) errors.Add($"The body is longer than {MaxBody:N0} characters.");
        if (errors.Count > 0) throw new BadRequestException(string.Join(" ", errors));
    }

    private async Task MakeDefaultAsync(EmailTemplate entity, int? userId, CancellationToken ct)
    {
        var relational = _db.Database.IsRelational();
        // The SQL Server retrying strategy refuses a user transaction unless the whole unit runs inside the strategy.
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = relational ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var others = await _db.EmailTemplates
                .Where(t => t.OrganizationId == entity.OrganizationId && t.TemplateType == entity.TemplateType && t.IsDefault && t.Id != entity.Id).ToListAsync(ct);
            foreach (var o in others) { o.IsDefault = false; o.ModifiedBy = userId; }
            await _db.SaveChangesAsync(ct);
            entity.IsDefault = true;
            entity.ModifiedBy = userId;
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        });
    }

    private async Task<EmailTemplate> FindAsync(Guid uuid, CancellationToken ct, bool tracked = false)
    {
        var org = Org;
        var q = _db.EmailTemplates.Where(t => t.Uuid == uuid && t.OrganizationId == org);
        return await (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("EmailTemplate", uuid);
    }

    private static EmailTemplateModel ToModel(EmailTemplate t) => new()
    {
        Uuid = t.Uuid, TemplateType = t.TemplateType, Name = t.Name, SubjectTemplate = t.SubjectTemplate, BodyHtml = t.BodyHtml,
        IsDefault = t.IsDefault, IsSeeded = t.IsSeeded, IsActive = t.IsActive, CreatedAt = t.CreatedAt, ModifiedAt = t.ModifiedAt,
    };

    internal static void RequireType(string? type)
    {
        if (!PlaceholderCatalog.IsEmailType(type))
            throw new BadRequestException($"Unknown email template type '{type}'. Allowed: {string.Join(", ", EmailTemplateTypes.All)}.");
    }
}
