using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Life;

public interface ILetterService
{
    Task<IReadOnlyList<LetterTemplateModel>> ListTemplatesAsync(HrmCaller caller, CancellationToken ct = default);
    Task<LetterTemplateModel> CreateTemplateAsync(HrmCaller caller, LetterTemplateUpsert request, CancellationToken ct = default);
    Task<LetterTemplateModel> UpdateTemplateAsync(HrmCaller caller, Guid uuid, LetterTemplateUpsert request, CancellationToken ct = default);
    Task<IReadOnlyList<IssuedLetterModel>> ListAsync(HrmCaller caller, Guid? employeeUuid, CancellationToken ct = default);
    Task<IReadOnlyList<IssuedLetterModel>> MineAsync(HrmCaller caller, CancellationToken ct = default);
    Task<IssuedLetterModel> IssueAsync(HrmCaller caller, LetterIssueRequest request, CancellationToken ct = default);
    Task<HrmPdfFile> GetPdfAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

public interface ITaxCertificateService
{
    Task<IReadOnlyList<TaxCertificateModel>> GenerateAsync(HrmCaller caller, TaxCertificateGenerate request, CancellationToken ct = default);
    Task<IReadOnlyList<TaxCertificateModel>> ListAsync(HrmCaller caller, int? taxYear, CancellationToken ct = default);
    Task<IReadOnlyList<TaxCertificateModel>> MineAsync(HrmCaller caller, CancellationToken ct = default);
    Task<HrmPdfFile> GetPdfAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

public sealed record HrmPdfFile(byte[] Content, string FileName);

internal static class LifePdf
{
    /// <summary>Always through the gate: concurrent QuestPDF renders corrupt bold text.</summary>
    public static byte[] Render(string organization, string number, string title, string? warning, Action<ColumnDescriptor> body)
    {
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(50);
            page.DefaultTextStyle(t => t.FontSize(11));
            page.Header().Column(c =>
            {
                c.Item().Text(organization).Bold().FontSize(14);
                c.Item().Text($"{number}    {HrmValidate.Today:yyyy-MM-dd}").FontSize(9);
            });
            page.Content().PaddingVertical(20).Column(c =>
            {
                c.Spacing(10);
                c.Item().Text(title).Bold().FontSize(13);
                if (warning is not null) c.Item().Text(warning).Bold().FontColor(Colors.Red.Darken2);
                body(c);
            });
        }));
        return PdfRenderGate.Run(document.GeneratePdf);
    }
}

internal sealed class LetterService : HrmServiceBase, ILetterService
{
    internal static readonly string[] Placeholders = ["EmployeeName", "EmployeeNumber", "Designation", "Department", "JoinDate", "LeaveDate", "Salary", "OrganizationName", "Date"];
    private static readonly Regex Token = new(@"\{(\w+)\}", RegexOptions.Compiled);

    private readonly IHrmScopeService _scope;
    private readonly IHrmEmployeeDirectory _directory;
    private readonly IHrmDocumentStore _store;
    private readonly IOrganizationNameLookup? _organizations;

    private readonly LifeUserNames? _names;

    public LetterService(HrmDbContext db, IHrmScopeService scope, IHrmEmployeeDirectory directory, IHrmDocumentStore store, IOrganizationNameLookup? organizations = null, LifeUserNames? names = null) : base(db)
    {
        _names = names;
        _scope = scope;
        _directory = directory;
        _store = store;
        _organizations = organizations;
    }

    private static LetterTemplateModel Map(LetterTemplate t) => new() { Uuid = t.Uuid, Code = t.Code, Name = t.Name, Kind = t.Kind, Subject = t.Subject, Body = t.Body, IsSystem = t.IsSystem, IsActive = t.IsActive };
    private static IssuedLetterModel Map(IssuedLetter l) => new() { Uuid = l.Uuid, LetterNumber = l.LetterNumber, EmployeeUuid = l.Employee!.Uuid, EmployeeName = l.Employee.FullName, Kind = l.Kind, IssuedAt = l.IssuedAt, IssuedByName = l.IssuedByName };

    internal static void CheckPlaceholders(params string?[] texts)
    {
        foreach (var text in texts)
            foreach (Match m in Token.Matches(text ?? ""))
                if (!Placeholders.Contains(m.Groups[1].Value))
                    throw HrmErrors.Bad("LETTER_PLACEHOLDER_UNKNOWN", $"{{{m.Groups[1].Value}}} is not a placeholder. Allowed: {string.Join(", ", Placeholders.Select(p => "{" + p + "}"))}.");
    }

    internal static bool Uses(string name, params string[] texts) => texts.Any(t => t.Contains("{" + name + "}", StringComparison.Ordinal));

    internal static string Merge(string text, IReadOnlyDictionary<string, string> values) => Token.Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public async Task<IReadOnlyList<LetterTemplateModel>> ListTemplatesAsync(HrmCaller caller, CancellationToken ct = default) =>
        (await Db.LetterTemplates.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == caller.OrganizationId).OrderBy(t => t.Name).ToListAsync(ct)).Select(Map).ToList();

    public async Task<LetterTemplateModel> CreateTemplateAsync(HrmCaller caller, LetterTemplateUpsert request, CancellationToken ct = default)
    {
        CheckPlaceholders(request.Subject, request.Body);
        var row = new LetterTemplate
        {
            Code = HrmValidate.Code(request.Code, 30), Name = HrmValidate.Name(request.Name), Kind = HrmValidate.OneOf(request.Kind ?? LetterTemplateCodes.Custom, LetterTemplateCodes.All, "kind"),
            Subject = HrmValidate.Name(request.Subject, 200, "Subject"), Body = HrmValidate.Name(request.Body, 20000, "Body"), IsActive = request.IsActive ?? true
        };
        if (await Db.LetterTemplates.IgnoreQueryFilters().AnyAsync(t => t.OrganizationId == caller.OrganizationId && t.Code == row.Code, ct))
            throw HrmErrors.Conflict("LETTER_TEMPLATE_CODE_TAKEN", $"A template with code {row.Code} already exists.");
        Stamp(row, caller, isNew: true);
        Db.LetterTemplates.Add(row);
        await SaveAsync(ct);
        return Map(row);
    }

    public async Task<LetterTemplateModel> UpdateTemplateAsync(HrmCaller caller, Guid uuid, LetterTemplateUpsert request, CancellationToken ct = default)
    {
        var row = await Db.LetterTemplates.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.OrganizationId == caller.OrganizationId && t.Uuid == uuid, ct) ?? throw new NotFoundException("Letter template", uuid);
        CheckPlaceholders(request.Subject, request.Body);
        // A system template keeps its code and kind; its text is the organization's to edit.
        if (!row.IsSystem)
        {
            var code = HrmValidate.Code(request.Code ?? row.Code, 30);
            if (code != row.Code && await Db.LetterTemplates.IgnoreQueryFilters().AnyAsync(t => t.OrganizationId == caller.OrganizationId && t.Code == code, ct))
                throw HrmErrors.Conflict("LETTER_TEMPLATE_CODE_TAKEN", $"A template with code {code} already exists.");
            row.Code = code;
            row.Kind = HrmValidate.OneOf(request.Kind ?? row.Kind, LetterTemplateCodes.All, "kind");
        }
        row.Name = HrmValidate.Name(request.Name ?? row.Name);
        row.Subject = HrmValidate.Name(request.Subject ?? row.Subject, 200, "Subject");
        row.Body = HrmValidate.Name(request.Body ?? row.Body, 20000, "Body");
        if (request.IsActive is { } active) row.IsActive = active;
        Stamp(row, caller);
        await SaveAsync(ct, "This template");
        return Map(row);
    }

    public async Task<IReadOnlyList<IssuedLetterModel>> ListAsync(HrmCaller caller, Guid? employeeUuid, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var people = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var query = Db.IssuedLetters.IgnoreQueryFilters().AsNoTracking().Include(l => l.Employee).Where(l => l.OrganizationId == caller.OrganizationId && people.Contains(l.EmployeeId));
        if (employeeUuid is { } e) query = query.Where(l => l.Employee!.Uuid == e);
        return (await query.OrderByDescending(l => l.Id).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<IssuedLetterModel>> MineAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var me = await _scope.RequireOwnEmployeeAsync(caller, ct);
        return (await Db.IssuedLetters.IgnoreQueryFilters().AsNoTracking().Include(l => l.Employee).Where(l => l.OrganizationId == caller.OrganizationId && l.EmployeeId == me.Id).OrderByDescending(l => l.Id).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<IssuedLetterModel> IssueAsync(HrmCaller caller, LetterIssueRequest request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid, ct);
        var template = await Db.LetterTemplates.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.OrganizationId == org && t.Uuid == request.TemplateUuid && t.IsActive, ct)
            ?? throw new NotFoundException("Letter template", request.TemplateUuid);
        CheckPlaceholders(template.Subject, template.Body);

        var today = HrmValidate.Today;
        var terms = await _directory.GetTermsAsync(org, employee.Id, today, ct)
            ?? (employee.LeaveDate is { } left ? await _directory.GetTermsAsync(org, employee.Id, left, ct) : null);
        var name = _organizations is null ? null : await _organizations.GetNameAsync(org, ct);
        if (string.IsNullOrWhiteSpace(name)) name = HrmValidate.Optional(request.OrganizationName, 200, "Organization name");
        if (string.IsNullOrWhiteSpace(name) && Uses("OrganizationName", template.Subject, template.Body))
            throw HrmErrors.Bad("LETTER_ORGANIZATION_REQUIRED", "The organization's name could not be found. Send organizationName.");

        var values = new Dictionary<string, string>
        {
            ["EmployeeName"] = employee.FullName, ["EmployeeNumber"] = employee.EmployeeNumber, ["Designation"] = terms?.DesignationName ?? "", ["Department"] = terms?.DepartmentName ?? "",
            ["JoinDate"] = employee.JoinDate?.ToString("yyyy-MM-dd") ?? "", ["LeaveDate"] = employee.LeaveDate?.ToString("yyyy-MM-dd") ?? "", ["OrganizationName"] = name ?? "", ["Date"] = today.ToString("yyyy-MM-dd")
        };
        if (Uses("Salary", template.Subject, template.Body))
        {
            caller.Require(PermissionCodes.HRM_SALARY_VIEW, "This letter shows the salary, which you do not have permission to see.");
            var on = employee.LeaveDate is { } l && l < today ? l : today;
            var lines = await Db.SalaryAssignments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.OrganizationId == org && a.EmployeeId == employee.Id && (a.ApprovalStatus == SalaryAssignmentStatuses.Approved || a.ApprovalStatus == SalaryAssignmentStatuses.Superseded) && a.EffectiveFrom <= on && (a.EffectiveTo == null || a.EffectiveTo >= on))
                .SelectMany(a => a.Lines).Where(x => x.Component!.InGross).Select(x => x.Amount).ToListAsync(ct);
            if (lines.Count == 0) throw HrmErrors.Conflict("LETTER_NO_SALARY", "This employee has no approved salary to state.");
            values["Salary"] = lines.Sum().ToString("N2");
        }

        var row = new IssuedLetter { EmployeeId = employee.Id, TemplateId = template.Id, Kind = template.Kind, IssuedBy = caller.UserIdOrNull, IssuedByName = _names is null ? caller.UserName : await _names.NameAsync(org, caller.UserIdOrNull, caller.UserName, ct) };
        Stamp(row, caller, isNew: true);
        var prefix = $"LTR-{today.Year}";
        var latest = await Db.IssuedLetters.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == org && l.LetterNumber.StartsWith(prefix + "-")).OrderByDescending(l => l.LetterNumber).Select(l => l.LetterNumber).FirstOrDefaultAsync(ct);
        var next = latest is not null && int.TryParse(latest[(prefix.Length + 1)..], out var n) ? n + 1 : 1;
        row.LetterNumber = $"{prefix}-{next:D5}";

        var subject = Merge(template.Subject, values);
        var body = Merge(template.Body, values);
        var pdf = LifePdf.Render(name ?? "", row.LetterNumber, subject, null, c => c.Item().Text(body));
        row.AttachmentUuid = await _store.StorePdfAsync(caller, HrmAttachmentCodes.Letter, row.Uuid, $"{row.LetterNumber}.pdf", pdf, PermissionCodes.HRM_LETTER_ISSUE, ct);
        Db.IssuedLetters.Add(row);
        await SaveAsync(ct);
        return Map(await Db.IssuedLetters.IgnoreQueryFilters().AsNoTracking().Include(l => l.Employee).FirstAsync(l => l.Id == row.Id, ct));
    }

    public async Task<HrmPdfFile> GetPdfAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Db.IssuedLetters.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(l => l.OrganizationId == caller.OrganizationId && l.Uuid == uuid, ct) ?? throw new NotFoundException("Letter", uuid);
        var own = await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId && caller.Has(PermissionCodes.HRM_SELF_SERVICE);
        if (!own && !(caller.Has(PermissionCodes.HRM_LETTER_ISSUE) && await _scope.CanAccessAsync(caller, row.EmployeeId, ct))) throw new NotFoundException("Letter", uuid);
        var file = await _store.ReadAsync(row.AttachmentUuid) ?? throw new NotFoundException("Letter", uuid);
        return new HrmPdfFile(file.Content, $"{row.LetterNumber}.pdf");
    }
}

internal sealed class TaxCertificateService : HrmServiceBase, ITaxCertificateService
{
    private const string TaxCode = "ITAX";
    private readonly IHrmScopeService _scope;
    private readonly IHrmDocumentStore _store;
    private readonly IOrganizationNameLookup? _organizations;

    public TaxCertificateService(HrmDbContext db, IHrmScopeService scope, IHrmDocumentStore store, IOrganizationNameLookup? organizations = null) : base(db)
    {
        _scope = scope;
        _store = store;
        _organizations = organizations;
    }

    private static (DateOnly From, DateOnly To) Year(int taxYear) => (new DateOnly(taxYear, 7, 1), new DateOnly(taxYear + 1, 6, 30));

    /// <summary>G-21: any tax table or rate in force during the year that nobody reviewed.</summary>
    private async Task<bool> UnverifiedAsync(Guid org, int taxYear, CancellationToken ct)
    {
        var (from, to) = Year(taxYear);
        return await Db.TaxSlabTables.IgnoreQueryFilters().AnyAsync(t => t.OrganizationId == org && !t.IsVerified && t.EffectiveFrom <= to && (t.EffectiveTo == null || t.EffectiveTo >= from), ct)
            || await Db.StatutoryRates.IgnoreQueryFilters().AnyAsync(r => r.OrganizationId == org && !r.IsVerified && r.RateType != StatutoryRateTypes.Gratuity && r.EffectiveFrom <= to && (r.EffectiveTo == null || r.EffectiveTo >= from), ct);
    }

    private static TaxCertificateModel Map(TaxCertificate c, Employee e, bool unverified) => new()
    {
        Uuid = c.Uuid, EmployeeUuid = e.Uuid, EmployeeName = e.FullName, TaxYear = c.TaxYear, TotalIncome = c.TotalIncome, TotalTax = c.TotalTax, GeneratedAt = c.GeneratedAt, IsUnverified = unverified
    };

    private async Task<TaxCertificateModel> GenerateOneAsync(HrmCaller caller, Employee employee, int taxYear, bool unverified, string organization, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var (from, to) = Year(taxYear);
        var paid = new[] { PayrollRunStatuses.Finalized, PayrollRunStatuses.Posted, PayrollRunStatuses.Paid };
        var components = await Db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org && c.Line!.EmployeeId == employee.Id && !c.Line.IsBlocked && paid.Contains(c.Line.Run!.Status) && c.Line.Run.PeriodEnd >= from && c.Line.Run.PeriodEnd <= to)
            .Select(c => new { c.Code, c.Amount, c.IsTaxable }).ToListAsync(ct);
        var income = components.Where(c => c.IsTaxable).Sum(c => c.Amount);
        var tax = components.Where(c => c.Code == TaxCode).Sum(c => c.Amount);

        var row = await Db.TaxCertificates.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org && c.EmployeeId == employee.Id && c.TaxYear == taxYear, ct);
        // Idempotent: the same figures return the same certificate. A year still running gets a fresh PDF once a run adds to it.
        if (row is { AttachmentUuid: not null } && row.TotalIncome == income && row.TotalTax == tax) return Map(row, employee, unverified);
        if (row is null)
        {
            row = new TaxCertificate { EmployeeId = employee.Id, TaxYear = taxYear };
            Stamp(row, caller, isNew: true);
            Db.TaxCertificates.Add(row);
        }
        row.TotalIncome = income;
        row.TotalTax = tax;
        row.GeneratedAt = DateTime.UtcNow;
        var warning = unverified ? "UNVERIFIED: the tax and statutory rates behind these figures have not been reviewed by a qualified consultant." : null;
        var pdf = LifePdf.Render(organization, $"TAX-{taxYear}-{employee.EmployeeNumber}", $"Annual tax statement {from:yyyy-MM-dd} to {to:yyyy-MM-dd}", warning, c =>
        {
            c.Item().Text($"Employee: {employee.FullName} ({employee.EmployeeNumber})");
            c.Item().Text($"Taxable income: {income:N2}");
            c.Item().Text($"Income tax withheld: {tax:N2}");
        });
        row.AttachmentUuid = await _store.StorePdfAsync(caller, HrmAttachmentCodes.TaxCertificate, row.Uuid, $"TAX-{taxYear}-{employee.EmployeeNumber}.pdf", pdf, PermissionCodes.HRM_TAX_CERT_VIEW, ct);
        Stamp(row, caller);
        try
        {
            await SaveAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            Db.Entry(row).State = EntityState.Detached;
            row = await Db.TaxCertificates.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.OrganizationId == org && c.EmployeeId == employee.Id && c.TaxYear == taxYear, ct);
        }
        return Map(row, employee, unverified);
    }

    public async Task<IReadOnlyList<TaxCertificateModel>> GenerateAsync(HrmCaller caller, TaxCertificateGenerate request, CancellationToken ct = default)
    {
        if (request.TaxYear is < 2000 or > 2100) throw new BadRequestException("The tax year is the year it starts in, for example 2026.");
        var org = caller.OrganizationId;
        var unverified = await UnverifiedAsync(org, request.TaxYear, ct);
        var name = _organizations is null ? null : await _organizations.GetNameAsync(org, ct);
        List<Employee> people;
        if (request.EmployeeUuid is { } one) people = [await _scope.RequireEmployeeAsync(caller, one, ct)];
        else
        {
            var (from, to) = Year(request.TaxYear);
            var scope = await _scope.GetAsync(caller, ct);
            var withPay = Db.PayrollRunLines.IgnoreQueryFilters().Where(l => l.OrganizationId == org && l.Run!.PeriodEnd >= from && l.Run.PeriodEnd <= to).Select(l => l.EmployeeId);
            people = await _scope.Apply(Db.Employees, caller, scope).Where(e => withPay.Contains(e.Id)).ToListAsync(ct);
        }
        var result = new List<TaxCertificateModel>();
        foreach (var employee in people) result.Add(await GenerateOneAsync(caller, employee, request.TaxYear, unverified, name ?? "", ct));
        return result;
    }

    private async Task<IReadOnlyList<TaxCertificateModel>> MapAllAsync(Guid org, List<TaxCertificate> rows, CancellationToken ct)
    {
        var flags = new Dictionary<int, bool>();
        foreach (var year in rows.Select(r => r.TaxYear).Distinct()) flags[year] = await UnverifiedAsync(org, year, ct);
        return rows.Select(r => Map(r, r.Employee!, flags[r.TaxYear])).ToList();
    }

    public async Task<IReadOnlyList<TaxCertificateModel>> ListAsync(HrmCaller caller, int? taxYear, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var people = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var rows = await Db.TaxCertificates.IgnoreQueryFilters().AsNoTracking().Include(c => c.Employee)
            .Where(c => c.OrganizationId == org && people.Contains(c.EmployeeId) && (taxYear == null || c.TaxYear == taxYear)).OrderByDescending(c => c.TaxYear).ThenBy(c => c.EmployeeId).ToListAsync(ct);
        return await MapAllAsync(org, rows, ct);
    }

    public async Task<IReadOnlyList<TaxCertificateModel>> MineAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var me = await _scope.RequireOwnEmployeeAsync(caller, ct);
        var rows = await Db.TaxCertificates.IgnoreQueryFilters().AsNoTracking().Include(c => c.Employee).Where(c => c.OrganizationId == caller.OrganizationId && c.EmployeeId == me.Id).OrderByDescending(c => c.TaxYear).ToListAsync(ct);
        return await MapAllAsync(caller.OrganizationId, rows, ct);
    }

    public async Task<HrmPdfFile> GetPdfAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Db.TaxCertificates.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == caller.OrganizationId && c.Uuid == uuid, ct) ?? throw new NotFoundException("Tax certificate", uuid);
        var own = await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId && caller.Has(PermissionCodes.HRM_SELF_SERVICE);
        if (!own && !(caller.Has(PermissionCodes.HRM_TAX_CERT_VIEW) && await _scope.CanAccessAsync(caller, row.EmployeeId, ct))) throw new NotFoundException("Tax certificate", uuid);
        if (row.AttachmentUuid is not { } file) throw new NotFoundException("Tax certificate", uuid);
        var content = await _store.ReadAsync(file) ?? throw new NotFoundException("Tax certificate", uuid);
        return new HrmPdfFile(content.Content, $"TAX-{row.TaxYear}.pdf");
    }
}

