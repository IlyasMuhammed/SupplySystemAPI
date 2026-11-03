using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Tal;

/// <summary>409 CANDIDATE_DUPLICATE, carrying the matches for the response body (API-CONTRACT §8).</summary>
public sealed class CandidateDuplicateException : ConflictException
{
    public IReadOnlyList<CandidateDuplicateMatch> Matches { get; }
    public CandidateDuplicateException(IReadOnlyList<CandidateDuplicateMatch> matches)
        : base("CANDIDATE_DUPLICATE: Another candidate has the same email or phone. Open that candidate, or save again with force.") => Matches = matches;
}

public interface IRecruitmentService
{
    Task<IReadOnlyList<VacancyModel>> ListVacanciesAsync(HrmCaller caller, string? status, CancellationToken ct = default);
    Task<VacancyModel> GetVacancyAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<VacancyModel> CreateVacancyAsync(HrmCaller caller, VacancyUpsert request, CancellationToken ct = default);
    Task<VacancyModel> UpdateVacancyAsync(HrmCaller caller, Guid uuid, VacancyUpsert request, CancellationToken ct = default);
    Task<VacancyModel> SetVacancyStatusAsync(HrmCaller caller, Guid uuid, string? status, CancellationToken ct = default);
    Task<IReadOnlyList<BoardColumn>> GetBoardAsync(HrmCaller caller, Guid vacancyUuid, CancellationToken ct = default);

    Task<IReadOnlyList<CandidateModel>> ListCandidatesAsync(HrmCaller caller, string? search, CancellationToken ct = default);
    Task<CandidateModel> GetCandidateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<CandidateModel> CreateCandidateAsync(HrmCaller caller, CandidateUpsert request, CancellationToken ct = default);
    Task<CandidateModel> UpdateCandidateAsync(HrmCaller caller, Guid uuid, CandidateUpsert request, CancellationToken ct = default);
    Task DeleteCandidateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);

    Task<IReadOnlyList<ApplicationModel>> ListApplicationsAsync(HrmCaller caller, Guid? vacancyUuid, string? stage, CancellationToken ct = default);
    Task<ApplicationModel> GetApplicationAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ApplicationModel> CreateApplicationAsync(HrmCaller caller, ApplicationCreate request, CancellationToken ct = default);
    Task<ApplicationModel> ChangeStageAsync(HrmCaller caller, Guid uuid, ApplicationStageChange request, CancellationToken ct = default);
    Task<ApplicationModel> SetRatingAsync(HrmCaller caller, Guid uuid, int? rating, CancellationToken ct = default);
    Task<EmployeeDetail> ConvertToEmployeeAsync(HrmCaller caller, Guid applicationUuid, ConvertToEmployeeRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<InterviewModel>> ListInterviewsAsync(HrmCaller caller, Guid applicationUuid, CancellationToken ct = default);
    /// <summary>The caller's own assigned interviews, whatever their HR scope (D-TAL-13).</summary>
    Task<IReadOnlyList<MyInterviewModel>> ListMyInterviewsAsync(HrmCaller caller, CancellationToken ct = default);
    Task<InterviewModel> CreateInterviewAsync(HrmCaller caller, Guid applicationUuid, InterviewUpsert request, CancellationToken ct = default);
    Task<InterviewModel> UpdateInterviewAsync(HrmCaller caller, Guid uuid, InterviewUpsert request, CancellationToken ct = default);

    Task<IReadOnlyList<OfferModel>> ListOffersAsync(HrmCaller caller, Guid applicationUuid, CancellationToken ct = default);
    Task<OfferModel> GetOfferAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<OfferModel> CreateOfferAsync(HrmCaller caller, Guid applicationUuid, OfferUpsert request, CancellationToken ct = default);
    Task<OfferModel> UpdateOfferAsync(HrmCaller caller, Guid uuid, OfferUpsert request, CancellationToken ct = default);
    Task<OfferModel> SetOfferStatusAsync(HrmCaller caller, Guid uuid, string? status, CancellationToken ct = default);
    Task<OfferModel> GenerateLetterAsync(HrmCaller caller, Guid uuid, OfferLetterRequest request, CancellationToken ct = default);
    Task<OfferLetterFile> ReadLetterAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

/// <summary>
/// Vacancies, candidates and the pipeline (D-TAL-1..7). HR scope: a vacancy belongs to its department and branch; an application
/// and its interviews and offers follow the vacancy; a candidate is visible to whoever created them or can see one of their applications.
/// </summary>
internal sealed partial class RecruitmentService : HrmServiceBase, IRecruitmentService
{
    /// <summary>Lists are plain arrays (contract §8); newest first, at most this many (D-TAL-8).</summary>
    private const int ListCap = 500;

    private static readonly string[] Pipeline = [ApplicationStages.New, ApplicationStages.Screening, ApplicationStages.Interview, ApplicationStages.Offer];

    private readonly IHrmScopeService _scope;
    private readonly IEmployeeService _employees;
    private readonly IHrmDocumentStore _documents;
    private readonly IHrmSeeder _seeder;
    private readonly IOrganizationNameLookup? _names;
    private readonly IOrganizationCurrencyService? _currency;
    private readonly IOrgCurrencyLookup? _currencies;
    private readonly IHrmAttachmentGuard? _attachments;

    public RecruitmentService(HrmDbContext db, IHrmScopeService scope, IEmployeeService employees, IHrmDocumentStore documents, IHrmSeeder seeder,
        IOrganizationNameLookup? names = null, IOrganizationCurrencyService? currency = null, IOrgCurrencyLookup? currencies = null, IHrmAttachmentGuard? attachments = null) : base(db)
    {
        _attachments = attachments;
        _scope = scope;
        _employees = employees;
        _documents = documents;
        _seeder = seeder;
        _names = names;
        _currency = currency;
        _currencies = currencies;
    }

    // ── Scope ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool InScope(HrmScope scope, int departmentId, int? branchId) =>
        scope.All || scope.DepartmentIds.Contains(departmentId) || (branchId is { } b && scope.BranchIds.Contains(b));

    private IQueryable<Vacancy> Vacancies(HrmCaller caller, HrmScope scope)
    {
        var org = caller.OrganizationId;
        var q = Db.Vacancies.IgnoreQueryFilters().Where(v => v.OrganizationId == org);
        if (scope.All) return q;
        var departments = scope.DepartmentIds.ToList();
        var branches = scope.BranchIds.ToList();
        return q.Where(v => departments.Contains(v.DepartmentId) || (v.BranchId != null && branches.Contains(v.BranchId.Value)));
    }

    private IQueryable<JobApplication> Applications(HrmCaller caller, HrmScope scope)
    {
        var org = caller.OrganizationId;
        var vacancies = Vacancies(caller, scope).Select(v => v.Id);
        return Db.JobApplications.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && !a.Candidate!.IsDeleted && vacancies.Contains(a.VacancyId));
    }

    private IQueryable<Candidate> Candidates(HrmCaller caller, HrmScope scope)
    {
        var org = caller.OrganizationId;
        var q = Db.Candidates.IgnoreQueryFilters().Where(c => c.OrganizationId == org && !c.IsDeleted);
        if (scope.All) return q;
        var user = caller.UserId;
        var visible = Applications(caller, scope).Select(a => a.CandidateId);
        return q.Where(c => c.CreatedBy == user || visible.Contains(c.Id));
    }

    private async Task<JobApplication> RequireApplicationAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var scope = await _scope.GetAsync(caller, ct);
        return await Applications(caller, scope).Include(a => a.Candidate).Include(a => a.Vacancy).Include(a => a.HiredEmployee).FirstOrDefaultAsync(a => a.Uuid == uuid, ct)
            ?? throw new NotFoundException("Application", uuid);
    }

    // ── Vacancies ───────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<List<VacancyModel>> MapVacanciesAsync(Guid org, IQueryable<Vacancy> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking().OrderByDescending(v => v.VacancyNumber).Take(ListCap).Select(v => new VacancyModel
        {
            Uuid = v.Uuid, VacancyNumber = v.VacancyNumber, Title = v.Title, DesignationUuid = v.Designation!.Uuid, DesignationName = v.Designation.Name,
            DepartmentUuid = v.Department!.Uuid, DepartmentName = v.Department.Name, BranchUuid = v.Branch == null ? null : v.Branch.Uuid, BranchName = v.Branch == null ? null : v.Branch.Name,
            Positions = v.Positions, Status = v.Status, Description = v.Description, ClosingDate = v.ClosingDate
        }).ToListAsync(ct);
        var uuids = rows.Select(r => r.Uuid).ToList();
        var counts = await Db.JobApplications.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && !a.Candidate!.IsDeleted && uuids.Contains(a.Vacancy!.Uuid))
            .GroupBy(a => new { a.Vacancy!.Uuid, a.Stage }).Select(g => new { g.Key.Uuid, g.Key.Stage, Count = g.Count() }).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.ApplicationCounts = ApplicationStages.All.ToDictionary(s => s, _ => 0);
            foreach (var c in counts.Where(c => c.Uuid == row.Uuid)) row.ApplicationCounts[c.Stage] = c.Count;
        }
        return rows;
    }

    public async Task<IReadOnlyList<VacancyModel>> ListVacanciesAsync(HrmCaller caller, string? status, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var q = Vacancies(caller, scope);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = HrmValidate.OneOf(status, VacancyStatuses.All, "status");
            q = q.Where(v => v.Status == s);
        }
        return await MapVacanciesAsync(caller.OrganizationId, q, ct);
    }

    public async Task<VacancyModel> GetVacancyAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var rows = await MapVacanciesAsync(caller.OrganizationId, Vacancies(caller, scope).Where(v => v.Uuid == uuid), ct);
        return rows.FirstOrDefault() ?? throw new NotFoundException("Vacancy", uuid);
    }

    private async Task ApplyVacancyAsync(Vacancy row, VacancyUpsert r, HrmCaller caller, HrmScope scope, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        row.Title = HrmValidate.Name(r.Title, 200, "Title");
        row.DesignationId = await OptionalIdAsync(Db.Designations, org, r.DesignationUuid, "Designation", ct) ?? throw new BadRequestException("Designation is required.");
        row.DepartmentId = await OptionalIdAsync(Db.Departments, org, r.DepartmentUuid, "Department", ct) ?? throw new BadRequestException("Department is required.");
        row.BranchId = await OptionalIdAsync(Db.Branches, org, r.BranchUuid, "Branch", ct);
        var positions = r.Positions ?? 1;
        if (positions < 1) throw new BadRequestException("A vacancy needs at least one position.");
        if (row.Id != 0)
        {
            var vacancyId = row.Id;
            var hired = await Db.JobApplications.IgnoreQueryFilters().CountAsync(a => a.OrganizationId == org && a.VacancyId == vacancyId && a.Stage == ApplicationStages.Hired, ct);
            if (positions < hired) throw HrmErrors.Conflict("VACANCY_POSITIONS_BELOW_HIRED", $"This vacancy already has {hired} hired, so it cannot have only {positions} position(s).");
        }
        row.Positions = positions;
        row.Description = HrmValidate.Optional(r.Description, 4000, "Description");
        row.ClosingDate = r.ClosingDate;
        // A scoped recruiter cannot open or move a vacancy into a department or branch they cannot see.
        if (!InScope(scope, row.DepartmentId, row.BranchId)) throw new NotFoundException("Department", r.DepartmentUuid);
    }

    public async Task<VacancyModel> CreateVacancyAsync(HrmCaller caller, VacancyUpsert request, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var row = new Vacancy();
        await ApplyVacancyAsync(row, request, caller, scope, ct);
        Stamp(row, caller, isNew: true);
        Db.Vacancies.Add(row);
        for (var attempt = 1; ; attempt++)
        {
            row.VacancyNumber = await NextVacancyNumberAsync(caller.OrganizationId, ct);
            try
            {
                await SaveAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (IsDuplicate(ex) && attempt < 3)
            {
                // Two creates took the same number: the next read sees the other one's.
            }
        }
        return await GetVacancyAsync(caller, row.Uuid, ct);
    }

    private async Task<string> NextVacancyNumberAsync(Guid org, CancellationToken ct)
    {
        var last = await Db.Vacancies.IgnoreQueryFilters().AsNoTracking().Where(v => v.OrganizationId == org)
            .OrderByDescending(v => v.VacancyNumber).Select(v => v.VacancyNumber).FirstOrDefaultAsync(ct);
        var number = last is not null && int.TryParse(last.AsSpan(4), out var n) ? n + 1 : 1;
        return $"VAC-{number:D5}";
    }

    private async Task<Vacancy> RequireVacancyAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var scope = await _scope.GetAsync(caller, ct);
        return await Vacancies(caller, scope).FirstOrDefaultAsync(v => v.Uuid == uuid, ct) ?? throw new NotFoundException("Vacancy", uuid);
    }

    public async Task<VacancyModel> UpdateVacancyAsync(HrmCaller caller, Guid uuid, VacancyUpsert request, CancellationToken ct = default)
    {
        var row = await RequireVacancyAsync(caller, uuid, ct);
        await ApplyVacancyAsync(row, request, caller, await _scope.GetAsync(caller, ct), ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This vacancy");
        return await GetVacancyAsync(caller, uuid, ct);
    }

    public async Task<VacancyModel> SetVacancyStatusAsync(HrmCaller caller, Guid uuid, string? status, CancellationToken ct = default)
    {
        var row = await RequireVacancyAsync(caller, uuid, ct);
        row.Status = HrmValidate.OneOf(status, VacancyStatuses.All, "status");
        Stamp(row, caller);
        await SaveAsync(ct, "This vacancy");
        return await GetVacancyAsync(caller, uuid, ct);
    }

    public async Task<IReadOnlyList<BoardColumn>> GetBoardAsync(HrmCaller caller, Guid vacancyUuid, CancellationToken ct = default)
    {
        await RequireVacancyAsync(caller, vacancyUuid, ct);
        var all = await ListApplicationsAsync(caller, vacancyUuid, null, ct);
        return ApplicationStages.All.Select(s => new BoardColumn { Stage = s, Applications = all.Where(a => a.Stage == s).ToList() }).ToList();
    }

    // ── Candidates ──────────────────────────────────────────────────────────────────────────────────────────────────

    internal static string? NormalizeEmail(string? email)
    {
        var value = HrmValidate.Optional(email, 200, "Email")?.ToLowerInvariant();
        if (value is not null && (!value.Contains('@') || value.StartsWith('@') || value.EndsWith('@'))) throw new BadRequestException($"'{value}' is not an email address.");
        return value;
    }

    /// <summary>Digits, with a leading + kept: "0300-123 4567" and "03001234567" are stored alike.</summary>
    internal static string? NormalizePhone(string? phone)
    {
        var value = HrmValidate.Optional(phone, 30, "Phone");
        if (value is null) return null;
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) throw new BadRequestException($"'{value}' is not a phone number.");
        return value.StartsWith('+') ? "+" + digits : digits;
    }

    /// <summary>The last ten digits: +92 300 1234567 and 0300 1234567 are one number.</summary>
    internal static string PhoneTail(string phone)
    {
        var digits = phone.TrimStart('+');
        return digits.Length <= 10 ? digits : digits[^10..];
    }

    /// <summary>Tasks 202, 214: across the whole organization, or a duplicate outside the caller's scope would be missed.</summary>
    private async Task CheckDuplicateAsync(HrmCaller caller, string? email, string? phone, int? exceptId, CancellationToken ct)
    {
        if (email is null && phone is null) return;
        var org = caller.OrganizationId;
        var tail = phone is null ? null : PhoneTail(phone);
        var found = await Db.Candidates.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org && !c.IsDeleted && c.Id != (exceptId ?? 0)
                && ((email != null && c.Email == email) || (tail != null && c.Phone != null && c.Phone.EndsWith(tail))))
            .Select(c => new { c.Id, c.Uuid, c.FirstName, c.LastName, c.Email, c.Phone }).Take(20).ToListAsync(ct);
        if (found.Count == 0) return;

        var scope = await _scope.GetAsync(caller, ct);
        var ids = found.Select(f => f.Id).ToList();
        var visible = await Candidates(caller, scope).Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        throw new CandidateDuplicateException(found.Select(f => new CandidateDuplicateMatch
        {
            Uuid = f.Uuid,
            Name = visible.Contains(f.Id) ? $"{f.FirstName} {f.LastName}".Trim() : null,
            MatchedOn = new[] { email != null && f.Email == email ? "email" : null, tail != null && f.Phone != null && f.Phone.EndsWith(tail) ? "phone" : null }.OfType<string>().ToList()
        }).ToList());
    }

    private static void ApplyCandidate(Candidate row, CandidateUpsert r, string? email, string? phone)
    {
        row.FirstName = HrmValidate.Name(r.FirstName, 60, "First name");
        row.LastName = HrmValidate.Name(r.LastName, 60, "Last name");
        row.Email = email;
        row.Phone = phone;
        row.Source = HrmValidate.Optional(r.Source, 50, "Source");
        row.ResumeAttachmentUuid = r.ResumeAttachmentUuid == Guid.Empty ? null : r.ResumeAttachmentUuid;
        row.Notes = HrmValidate.Optional(r.Notes, 2000, "Notes");
    }

    public async Task<IReadOnlyList<CandidateModel>> ListCandidatesAsync(HrmCaller caller, string? search, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var q = Candidates(caller, scope).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c => (c.FirstName + " " + c.LastName).Contains(s) || (c.Email != null && c.Email.Contains(s)) || (c.Phone != null && c.Phone.Contains(s)));
        }
        var rows = await q.OrderByDescending(c => c.Id).Take(ListCap).ToListAsync(ct);
        return rows.Select(MapCandidate).ToList();
    }

    private static CandidateModel MapCandidate(Candidate c) => new()
    {
        Uuid = c.Uuid, FirstName = c.FirstName, LastName = c.LastName, Email = c.Email, Phone = c.Phone, Source = c.Source, ResumeAttachmentUuid = c.ResumeAttachmentUuid, Notes = c.Notes
    };

    public async Task<CandidateModel> GetCandidateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var row = await Candidates(caller, scope).AsNoTracking().FirstOrDefaultAsync(c => c.Uuid == uuid, ct) ?? throw new NotFoundException("Candidate", uuid);
        var model = MapCandidate(row);
        var id = row.Id;
        model.Applications = await ProjectApplications(Applications(caller, scope).Where(a => a.CandidateId == id)).ToListAsync(ct);
        return model;
    }

    public async Task<CandidateModel> CreateCandidateAsync(HrmCaller caller, CandidateUpsert request, CancellationToken ct = default)
    {
        var email = NormalizeEmail(request.Email);
        var phone = NormalizePhone(request.Phone);
        var row = new Candidate();
        await _attachments.CheckedAsync(caller, request.ResumeAttachmentUuid, HrmAttachmentCodes.Candidate);
        ApplyCandidate(row, request, email, phone);
        if (!request.Force) await CheckDuplicateAsync(caller, email, phone, null, ct);
        Stamp(row, caller, isNew: true);
        Db.Candidates.Add(row);
        await SaveAsync(ct);
        return MapCandidate(row);
    }

    private async Task<Candidate> RequireCandidateAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var scope = await _scope.GetAsync(caller, ct);
        return await Candidates(caller, scope).FirstOrDefaultAsync(c => c.Uuid == uuid, ct) ?? throw new NotFoundException("Candidate", uuid);
    }

    public async Task<CandidateModel> UpdateCandidateAsync(HrmCaller caller, Guid uuid, CandidateUpsert request, CancellationToken ct = default)
    {
        var row = await RequireCandidateAsync(caller, uuid, ct);
        var email = NormalizeEmail(request.Email);
        var phone = NormalizePhone(request.Phone);
        await _attachments.CheckedAsync(caller, request.ResumeAttachmentUuid, HrmAttachmentCodes.Candidate, row.ResumeAttachmentUuid);
        ApplyCandidate(row, request, email, phone);
        if (!request.Force) await CheckDuplicateAsync(caller, email, phone, row.Id, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This candidate");
        return await GetCandidateAsync(caller, uuid, ct);
    }

    /// <summary>Soft: only while every application ended in Rejected, so a live pipeline and a hire keep their candidate.</summary>
    public async Task DeleteCandidateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireCandidateAsync(caller, uuid, ct);
        var org = caller.OrganizationId;
        if (await Db.JobApplications.IgnoreQueryFilters().AnyAsync(a => a.OrganizationId == org && a.CandidateId == row.Id && a.Stage != ApplicationStages.Rejected, ct))
            throw HrmErrors.Conflict("CANDIDATE_IN_USE", "This candidate has an application in progress or was hired.");
        row.IsDeleted = true;
        Stamp(row, caller);
        await SaveAsync(ct, "This candidate");
    }

    // ── Applications ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IQueryable<ApplicationModel> ProjectApplications(IQueryable<JobApplication> q) => q.AsNoTracking().Select(a => new ApplicationModel
    {
        Uuid = a.Uuid, CandidateUuid = a.Candidate!.Uuid, CandidateName = a.Candidate.FirstName + " " + a.Candidate.LastName,
        VacancyUuid = a.Vacancy!.Uuid, VacancyTitle = a.Vacancy.Title, Stage = a.Stage, Rating = a.Rating, RejectionReason = a.RejectionReason,
        HiredEmployeeUuid = a.HiredEmployee == null ? null : a.HiredEmployee.Uuid
    });

    public async Task<IReadOnlyList<ApplicationModel>> ListApplicationsAsync(HrmCaller caller, Guid? vacancyUuid, string? stage, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var q = Applications(caller, scope);
        if (vacancyUuid is { } v) q = q.Where(a => a.Vacancy!.Uuid == v);
        if (!string.IsNullOrWhiteSpace(stage))
        {
            var s = HrmValidate.OneOf(stage, ApplicationStages.All, "stage");
            q = q.Where(a => a.Stage == s);
        }
        return await ProjectApplications(q.OrderByDescending(a => a.Id).Take(ListCap)).ToListAsync(ct);
    }

    public async Task<ApplicationModel> GetApplicationAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        return await ProjectApplications(Applications(caller, scope).Where(a => a.Uuid == uuid)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Application", uuid);
    }

    public async Task<ApplicationModel> CreateApplicationAsync(HrmCaller caller, ApplicationCreate request, CancellationToken ct = default)
    {
        if (request.CandidateUuid is not { } candidateUuid || candidateUuid == Guid.Empty) throw new BadRequestException("Candidate is required.");
        if (request.VacancyUuid is not { } vacancyUuid || vacancyUuid == Guid.Empty) throw new BadRequestException("Vacancy is required.");
        var org = caller.OrganizationId;
        var candidate = await RequireCandidateAsync(caller, candidateUuid, ct);
        var vacancy = await RequireVacancyAsync(caller, vacancyUuid, ct);
        if (vacancy.Status != VacancyStatuses.Open) throw HrmErrors.Conflict("VACANCY_NOT_OPEN", $"This vacancy is {vacancy.Status}.");
        if (await Db.JobApplications.IgnoreQueryFilters().AnyAsync(a => a.OrganizationId == org && a.CandidateId == candidate.Id && a.VacancyId == vacancy.Id, ct))
            throw HrmErrors.Conflict("APPLICATION_DUPLICATE", "This candidate has already applied to this vacancy.");

        var row = new JobApplication { CandidateId = candidate.Id, VacancyId = vacancy.Id };
        Stamp(row, caller, isNew: true);
        Db.JobApplications.Add(row);
        try
        {
            await SaveAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            throw HrmErrors.Conflict("APPLICATION_DUPLICATE", "This candidate has already applied to this vacancy.");
        }
        return await GetApplicationAsync(caller, row.Uuid, ct);
    }

    /// <summary>
    /// Task 207 (Odoo: a Kanban, so New, Screening, Interview and Offer move freely both ways). Anything open can go to Rejected, with a
    /// reason. Rejected can reopen into the pipeline. Hired comes only from convert and is final.
    /// </summary>
    internal static void CheckStageMove(string from, string to)
    {
        var allowed = from != ApplicationStages.Hired && to != ApplicationStages.Hired && from != to;
        if (!allowed) throw HrmErrors.Conflict("APPLICATION_STAGE_TRANSITION",
            to == ApplicationStages.Hired ? "Hire a candidate with Convert to employee." : $"An application cannot go from {from} to {to}.");
    }

    public async Task<ApplicationModel> ChangeStageAsync(HrmCaller caller, Guid uuid, ApplicationStageChange request, CancellationToken ct = default)
    {
        var row = await RequireApplicationAsync(caller, uuid, ct);
        var next = HrmValidate.OneOf(request.Stage, ApplicationStages.All, "stage");
        if (next == row.Stage) return await GetApplicationAsync(caller, uuid, ct);
        CheckStageMove(row.Stage, next);
        // A closed vacancy can still turn down whoever is left; it cannot move anyone forward.
        if (next != ApplicationStages.Rejected && row.Vacancy!.Status == VacancyStatuses.Closed)
            throw HrmErrors.Conflict("VACANCY_NOT_OPEN", "This vacancy is closed.");
        if (next == ApplicationStages.Rejected)
            row.RejectionReason = HrmValidate.Optional(request.RejectionReason, 500, "Rejection reason") ?? throw HrmErrors.Bad("APPLICATION_REJECTION_REASON_REQUIRED", "Give the reason for rejecting.");
        else
            row.RejectionReason = null;
        row.Stage = next;
        Stamp(row, caller);
        await SaveAsync(ct, "This application");
        return await GetApplicationAsync(caller, uuid, ct);
    }

    public async Task<ApplicationModel> SetRatingAsync(HrmCaller caller, Guid uuid, int? rating, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5) throw new BadRequestException("A rating is from 1 to 5.");
        var row = await RequireApplicationAsync(caller, uuid, ct);
        row.Rating = rating;
        Stamp(row, caller);
        await SaveAsync(ct, "This application");
        return await GetApplicationAsync(caller, uuid, ct);
    }

    /// <summary>
    /// Task 208. One transaction over the employee, the hire record (IEmployeeService.CreateAsync, which writes it through
    /// IEmploymentRecordFactory and owns the number) and the application. The work may run again after a transient failure, so it
    /// starts from an empty change tracker and reloads everything.
    /// </summary>
    public async Task<EmployeeDetail> ConvertToEmployeeAsync(HrmCaller caller, Guid applicationUuid, ConvertToEmployeeRequest request, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.HRM_RECRUITMENT_MANAGE);
        caller.Require(PermissionCodes.HRM_EMPLOYEE_EDIT, "Hiring creates an employee: it also needs the permission to edit employees.");
        var joinDate = request.JoinDate ?? throw new BadRequestException("The join date is required.");
        var employmentType = HrmValidate.OneOf(request.EmploymentType ?? EmploymentTypes.Permanent, EmploymentTypes.All, "employmentType");
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);

        EmployeeDetail? result = null;
        await Db.InTransactionAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            var app = await Applications(caller, scope).Include(a => a.Candidate).Include(a => a.Vacancy).FirstOrDefaultAsync(a => a.Uuid == applicationUuid, ct)
                ?? throw new NotFoundException("Application", applicationUuid);
            var candidate = app.Candidate!;
            var vacancy = app.Vacancy!;

            if (app.Stage == ApplicationStages.Hired || await Db.JobApplications.IgnoreQueryFilters().AnyAsync(a => a.OrganizationId == org && a.CandidateId == app.CandidateId && a.HiredEmployeeId != null, ct))
                throw HrmErrors.Conflict("APPLICATION_ALREADY_HIRED", "This candidate has already been hired.");
            if (app.Stage != ApplicationStages.Offer)
                throw HrmErrors.Conflict("APPLICATION_STAGE_TRANSITION", $"Only an application at the Offer stage can be hired; this one is at {app.Stage}.");
            var offers = await Db.JobOffers.IgnoreQueryFilters().AsNoTracking().Where(o => o.OrganizationId == org && o.ApplicationId == app.Id).Select(o => o.Status).ToListAsync(ct);
            if (offers.Count > 0 && !offers.Contains(OfferStatuses.Accepted))
                throw HrmErrors.Conflict("OFFER_NOT_ACCEPTED", "This application has an offer that has not been accepted.");
            var hired = await Db.JobApplications.IgnoreQueryFilters().CountAsync(a => a.OrganizationId == org && a.VacancyId == vacancy.Id && a.Stage == ApplicationStages.Hired, ct);
            if (hired >= vacancy.Positions) throw HrmErrors.Conflict("VACANCY_FILLED", "Every position of this vacancy is filled.");
            if (vacancy.Status != VacancyStatuses.Open) throw HrmErrors.Conflict("VACANCY_NOT_OPEN", $"This vacancy is {vacancy.Status}.");

            // The hire lands where the vacancy says unless the form overrides it, and never outside the caller's scope.
            var departmentId = request.DepartmentUuid is null ? vacancy.DepartmentId : await OptionalIdAsync(Db.Departments, org, request.DepartmentUuid, "Department", ct) ?? vacancy.DepartmentId;
            var branchId = request.BranchUuid is null ? vacancy.BranchId : await OptionalIdAsync(Db.Branches, org, request.BranchUuid, "Branch", ct) ?? vacancy.BranchId;
            if (!InScope(scope, departmentId, branchId)) throw new NotFoundException("Department", request.DepartmentUuid);
            var departmentUuid = await Db.Departments.IgnoreQueryFilters().Where(d => d.OrganizationId == org && d.Id == departmentId).Select(d => d.Uuid).FirstAsync(ct);
            var branchUuid = branchId is { } b ? await Db.Branches.IgnoreQueryFilters().Where(x => x.OrganizationId == org && x.Id == b).Select(x => (Guid?)x.Uuid).FirstAsync(ct) : null;
            var designationUuid = request.DesignationUuid ?? await Db.Designations.IgnoreQueryFilters().Where(d => d.OrganizationId == org && d.Id == vacancy.DesignationId).Select(d => d.Uuid).FirstAsync(ct);

            var employee = await _employees.CreateAsync(caller, new EmployeeUpsert
            {
                FirstName = candidate.FirstName, LastName = candidate.LastName, PersonalEmail = candidate.Email, Mobile = candidate.Phone,
                JoinDate = joinDate, SourceCandidateUuid = candidate.Uuid,
                Status = employmentType == EmploymentTypes.Probation ? EmployeeStatuses.Probation : EmployeeStatuses.Active,
                Employment = new EmploymentRecordCreate
                {
                    EffectiveFrom = joinDate, EmploymentType = employmentType, DepartmentUuid = departmentUuid, DesignationUuid = designationUuid, BranchUuid = branchUuid,
                    ManagerUuid = request.ManagerUuid, ChangeReason = EmploymentChangeReasons.Hire
                }
            }, ct);

            app.HiredEmployeeId = await Db.Employees.IgnoreQueryFilters().Where(e => e.OrganizationId == org && e.Uuid == employee.Uuid).Select(e => e.Id).FirstAsync(ct);
            app.Stage = ApplicationStages.Hired;
            app.RejectionReason = null;
            Stamp(app, caller);
            if (hired + 1 >= vacancy.Positions)
            {
                vacancy.Status = VacancyStatuses.Closed;
                Stamp(vacancy, caller);
            }
            await SaveAsync(ct, "This application");
            result = employee;
        }, ct);
        return result!;
    }

    // ── Interviews ──────────────────────────────────────────────────────────────────────────────────────────────────

    private IQueryable<InterviewModel> ProjectInterviews(Guid org) => Db.Interviews.IgnoreQueryFilters().AsNoTracking().Where(i => i.OrganizationId == org).Select(i => new InterviewModel
    {
        Uuid = i.Uuid, ApplicationUuid = i.Application!.Uuid, InterviewerUuid = i.Interviewer == null ? null : i.Interviewer.Uuid,
        InterviewerName = i.Interviewer == null ? null : i.Interviewer.FirstName + " " + i.Interviewer.LastName,
        ScheduledAt = i.ScheduledAt, Feedback = i.Feedback, Score = i.Score, Result = i.Result
    });

    public async Task<IReadOnlyList<InterviewModel>> ListInterviewsAsync(HrmCaller caller, Guid applicationUuid, CancellationToken ct = default)
    {
        await RequireApplicationAsync(caller, applicationUuid, ct);
        return await ProjectInterviews(caller.OrganizationId).Where(i => i.ApplicationUuid == applicationUuid).OrderBy(i => i.ScheduledAt).ToListAsync(ct);
    }

    /// <summary>Only what giving feedback needs: no candidate contact details or resume, no rating or rejection reason, no offer or salary.</summary>
    public async Task<IReadOnlyList<MyInterviewModel>> ListMyInterviewsAsync(HrmCaller caller, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.HRM_RECRUITMENT_VIEW);
        var org = caller.OrganizationId;
        var own = await _scope.OwnEmployeeIdAsync(caller, ct)
            ?? throw new NotFoundException("HRM_NO_EMPLOYEE: Your user is not linked to an employee record.");
        return await Db.Interviews.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.InterviewerEmployeeId == own && !i.Application!.Candidate!.IsDeleted)
            .OrderByDescending(i => i.ScheduledAt).Take(ListCap)
            .Select(i => new MyInterviewModel
            {
                Uuid = i.Uuid, ScheduledAt = i.ScheduledAt, Feedback = i.Feedback, Score = i.Score, Result = i.Result,
                ApplicationUuid = i.Application!.Uuid, ApplicationStage = i.Application.Stage,
                CandidateName = i.Application.Candidate!.FirstName + " " + i.Application.Candidate.LastName,
                VacancyTitle = i.Application.Vacancy!.Title, DesignationName = i.Application.Vacancy.Designation!.Name, DepartmentName = i.Application.Vacancy.Department!.Name
            }).ToListAsync(ct);
    }

    private async Task ApplyInterviewAsync(Interview row, InterviewUpsert r, HrmCaller caller, CancellationToken ct)
    {
        row.ScheduledAt = r.ScheduledAt ?? throw new BadRequestException("The interview date and time are required.");
        row.InterviewerEmployeeId = r.InterviewerUuid is { } u && u != Guid.Empty ? (await _scope.RequireEmployeeAsync(caller, u, ct)).Id : null;
        ApplyInterviewOutcome(row, r);
    }

    private static void ApplyInterviewOutcome(Interview row, InterviewUpsert r)
    {
        row.Feedback = HrmValidate.Optional(r.Feedback, 4000, "Feedback");
        if (r.Score is < 0 or > 100) throw new BadRequestException("A score is from 0 to 100.");
        row.Score = r.Score;
        row.Result = HrmValidate.OneOf(r.Result ?? InterviewResults.Pending, InterviewResults.All, "result");
    }

    public async Task<InterviewModel> CreateInterviewAsync(HrmCaller caller, Guid applicationUuid, InterviewUpsert request, CancellationToken ct = default)
    {
        var app = await RequireApplicationAsync(caller, applicationUuid, ct);
        if (app.Stage is ApplicationStages.Hired or ApplicationStages.Rejected)
            throw HrmErrors.Conflict("APPLICATION_CLOSED", $"This application is {app.Stage}.");
        var row = new Interview { ApplicationId = app.Id };
        await ApplyInterviewAsync(row, request, caller, ct);
        Stamp(row, caller, isNew: true);
        Db.Interviews.Add(row);
        await SaveAsync(ct);
        return await ProjectInterviews(caller.OrganizationId).FirstAsync(i => i.Uuid == row.Uuid, ct);
    }

    public async Task<InterviewModel> UpdateInterviewAsync(HrmCaller caller, Guid uuid, InterviewUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Db.Interviews.IgnoreQueryFilters().Include(i => i.Application).FirstOrDefaultAsync(i => i.OrganizationId == org && i.Uuid == uuid, ct)
            ?? throw new NotFoundException("Interview", uuid);
        var scope = await _scope.GetAsync(caller, ct);
        var inScope = await Applications(caller, scope).AnyAsync(a => a.Id == row.ApplicationId, ct);
        var isInterviewer = scope.OwnEmployeeId is { } own && row.InterviewerEmployeeId == own;

        if (inScope && caller.Has(PermissionCodes.HRM_RECRUITMENT_MANAGE))
        {
            await ApplyInterviewAsync(row, request, caller, ct);
        }
        else if (isInterviewer && caller.HasAny(PermissionCodes.HRM_RECRUITMENT_VIEW, PermissionCodes.HRM_RECRUITMENT_MANAGE))
        {
            // D-TAL-11: the assigned interviewer records the outcome. The rest of the body is ignored: who and when stay HR's.
            ApplyInterviewOutcome(row, request);
        }
        else if (inScope)
        {
            throw new ForbiddenException("Only the assigned interviewer or someone who manages recruitment can change this interview.");
        }
        else
        {
            throw new NotFoundException("Interview", uuid);
        }
        Stamp(row, caller);
        await SaveAsync(ct, "This interview");
        return await ProjectInterviews(org).FirstAsync(i => i.Uuid == uuid, ct);
    }

    // ── Offers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static OfferModel MapOffer(JobOffer o, Guid applicationUuid, HrmCaller caller)
    {
        var canSee = caller.Has(PermissionCodes.HRM_SALARY_VIEW);
        return new OfferModel
        {
            Uuid = o.Uuid, ApplicationUuid = applicationUuid, OfferedSalary = canSee ? o.OfferedSalary : null, SalaryMasked = !canSee,
            ProposedJoinDate = o.ProposedJoinDate, Status = o.Status, LetterAttachmentUuid = o.LetterAttachmentUuid, Notes = o.Notes
        };
    }

    private static void ApplyOffer(JobOffer row, OfferUpsert r, HrmCaller caller)
    {
        if (r.OfferedSalary is { } salary)
        {
            caller.Require(PermissionCodes.HRM_SALARY_EDIT, "Setting the offered salary needs the permission to edit salaries.");
            if (salary < 0) throw new BadRequestException("The offered salary cannot be negative.");
            row.OfferedSalary = salary;
        }
        row.ProposedJoinDate = r.ProposedJoinDate;
        row.Notes = HrmValidate.Optional(r.Notes, 2000, "Notes");
    }

    public async Task<IReadOnlyList<OfferModel>> ListOffersAsync(HrmCaller caller, Guid applicationUuid, CancellationToken ct = default)
    {
        var app = await RequireApplicationAsync(caller, applicationUuid, ct);
        var org = caller.OrganizationId;
        var rows = await Db.JobOffers.IgnoreQueryFilters().AsNoTracking().Where(o => o.OrganizationId == org && o.ApplicationId == app.Id).OrderByDescending(o => o.Id).ToListAsync(ct);
        return rows.Select(o => MapOffer(o, applicationUuid, caller)).ToList();
    }

    private async Task<(JobOffer Offer, JobApplication Application)> RequireOfferAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var offer = await Db.JobOffers.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.OrganizationId == org && o.Uuid == uuid, ct) ?? throw new NotFoundException("Offer", uuid);
        var scope = await _scope.GetAsync(caller, ct);
        var app = await Applications(caller, scope).Include(a => a.Candidate).Include(a => a.Vacancy).FirstOrDefaultAsync(a => a.Id == offer.ApplicationId, ct)
            ?? throw new NotFoundException("Offer", uuid);
        return (offer, app);
    }

    public async Task<OfferModel> GetOfferAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (offer, app) = await RequireOfferAsync(caller, uuid, ct);
        return MapOffer(offer, app.Uuid, caller);
    }

    public async Task<OfferModel> CreateOfferAsync(HrmCaller caller, Guid applicationUuid, OfferUpsert request, CancellationToken ct = default)
    {
        var app = await RequireApplicationAsync(caller, applicationUuid, ct);
        if (app.Stage is ApplicationStages.Hired or ApplicationStages.Rejected)
            throw HrmErrors.Conflict("APPLICATION_CLOSED", $"This application is {app.Stage}.");
        var org = caller.OrganizationId;
        if (await Db.JobOffers.IgnoreQueryFilters().AnyAsync(o => o.OrganizationId == org && o.ApplicationId == app.Id && (o.Status == OfferStatuses.Draft || o.Status == OfferStatuses.Sent || o.Status == OfferStatuses.Accepted), ct))
            throw HrmErrors.Conflict("OFFER_ACTIVE_EXISTS", "This application already has a live offer.");
        var row = new JobOffer { ApplicationId = app.Id };
        ApplyOffer(row, request, caller);
        Stamp(row, caller, isNew: true);
        Db.JobOffers.Add(row);
        // Making an offer puts the application at the Offer stage (D-TAL-3); both go in one save.
        if (app.Stage != ApplicationStages.Offer)
        {
            app.Stage = ApplicationStages.Offer;
            Stamp(app, caller);
        }
        await SaveAsync(ct, "This application");
        return MapOffer(row, applicationUuid, caller);
    }

    public async Task<OfferModel> UpdateOfferAsync(HrmCaller caller, Guid uuid, OfferUpsert request, CancellationToken ct = default)
    {
        var (offer, app) = await RequireOfferAsync(caller, uuid, ct);
        if (offer.Status != OfferStatuses.Draft) throw HrmErrors.Conflict("OFFER_NOT_DRAFT", "Only a draft offer can be changed.");
        ApplyOffer(offer, request, caller);
        offer.LetterAttachmentUuid = null; // the old letter states the old terms
        Stamp(offer, caller);
        await SaveAsync(ct, "This offer");
        return MapOffer(offer, app.Uuid, caller);
    }

    internal static bool CanMoveOffer(string from, string to) => (from, to) switch
    {
        (OfferStatuses.Draft, OfferStatuses.Sent or OfferStatuses.Withdrawn) => true,
        (OfferStatuses.Sent, OfferStatuses.Accepted or OfferStatuses.Declined or OfferStatuses.Withdrawn) => true,
        (OfferStatuses.Accepted, OfferStatuses.Withdrawn) => true,
        _ => false
    };

    public async Task<OfferModel> SetOfferStatusAsync(HrmCaller caller, Guid uuid, string? status, CancellationToken ct = default)
    {
        var (offer, app) = await RequireOfferAsync(caller, uuid, ct);
        var next = HrmValidate.OneOf(status, OfferStatuses.All, "status");
        if (next == offer.Status) return MapOffer(offer, app.Uuid, caller);
        if (app.Stage == ApplicationStages.Hired) throw HrmErrors.Conflict("APPLICATION_ALREADY_HIRED", "This candidate has been hired: the offer can no longer change.");
        if (!CanMoveOffer(offer.Status, next)) throw HrmErrors.Conflict("OFFER_STATUS_TRANSITION", $"An offer cannot go from {offer.Status} to {next}.");
        if (next == OfferStatuses.Sent && offer.OfferedSalary <= 0) throw HrmErrors.Conflict("OFFER_SALARY_MISSING", "Set the offered salary before sending the offer.");
        offer.Status = next;
        Stamp(offer, caller);
        await SaveAsync(ct, "This offer");
        return MapOffer(offer, app.Uuid, caller);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    private static readonly HashSet<string> KnownPlaceholders = ["EmployeeName", "EmployeeNumber", "Designation", "Department", "JoinDate", "LeaveDate", "Salary", "OrganizationName", "Date"];

    /// <summary>Task 212. The stored PDF states the salary, so making and reading it both need HRM_SALARY_VIEW (D-TAL-4).</summary>
    public async Task<OfferModel> GenerateLetterAsync(HrmCaller caller, Guid uuid, OfferLetterRequest request, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.HRM_SALARY_VIEW, "An offer letter states the salary: it needs the permission to view salaries.");
        var (offer, app) = await RequireOfferAsync(caller, uuid, ct);
        if (offer.Status is not (OfferStatuses.Draft or OfferStatuses.Sent)) throw HrmErrors.Conflict("OFFER_STATUS_TRANSITION", $"A letter cannot be made for a {offer.Status} offer.");

        var org = caller.OrganizationId;
        await _seeder.EnsureAsync(org, ct);
        var templates = Db.LetterTemplates.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == org && t.Kind == LetterTemplateCodes.OfferLetter && t.IsActive);
        var template = request.TemplateUuid is { } templateUuid && templateUuid != Guid.Empty
            ? await templates.FirstOrDefaultAsync(t => t.Uuid == templateUuid, ct) ?? throw new NotFoundException("Offer letter template", templateUuid)
            : await templates.OrderByDescending(t => t.IsSystem).ThenBy(t => t.Id).FirstOrDefaultAsync(ct) ?? throw HrmErrors.Conflict("LETTER_TEMPLATE_MISSING", "There is no active offer letter template.");

        var values = await BuildValuesAsync(caller, offer, app, template.Subject + template.Body, request.OrganizationName, ct);
        var subject = Fill(template.Subject, values);
        var body = Fill(template.Body, values);
        var pdf = OfferLetterPdf.Render(subject, body);

        // A retried PUT keeps nothing half-done: the pointer moves only after the file is stored.
        offer.LetterAttachmentUuid = await _documents.StorePdfAsync(caller, HrmAttachmentCodes.Letter, offer.Uuid, $"Offer-{app.Vacancy!.VacancyNumber}.pdf", pdf, PermissionCodes.HRM_SALARY_VIEW, ct);
        Stamp(offer, caller);
        await SaveAsync(ct, "This offer");
        return MapOffer(offer, app.Uuid, caller);
    }

    private async Task<Dictionary<string, string>> BuildValuesAsync(HrmCaller caller, JobOffer offer, JobApplication app, string text, string? organizationName, CancellationToken ct)
    {
        var used = Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        var unknown = used.Where(p => !KnownPlaceholders.Contains(p)).ToList();
        if (unknown.Count > 0) throw HrmErrors.Bad("LETTER_PLACEHOLDER_UNKNOWN", $"Unknown placeholder: {string.Join(", ", unknown)}.");

        var org = caller.OrganizationId;
        var vacancy = app.Vacancy!;
        var values = new Dictionary<string, string>
        {
            ["EmployeeName"] = $"{app.Candidate!.FirstName} {app.Candidate.LastName}".Trim(),
            ["EmployeeNumber"] = "",
            ["LeaveDate"] = "",
            ["Date"] = HrmValidate.Today.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            ["Designation"] = await Db.Designations.IgnoreQueryFilters().Where(d => d.OrganizationId == org && d.Id == vacancy.DesignationId).Select(d => d.Name).FirstAsync(ct),
            ["Department"] = await Db.Departments.IgnoreQueryFilters().Where(d => d.OrganizationId == org && d.Id == vacancy.DepartmentId).Select(d => d.Name).FirstAsync(ct),
        };
        if (used.Contains("JoinDate"))
            values["JoinDate"] = (offer.ProposedJoinDate ?? throw HrmErrors.Conflict("OFFER_JOIN_DATE_MISSING", "Set the proposed join date first: the letter states it.")).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        if (used.Contains("Salary"))
        {
            if (offer.OfferedSalary <= 0) throw HrmErrors.Conflict("OFFER_SALARY_MISSING", "Set the offered salary first: the letter states it.");
            var code = await CurrencyCodeAsync(org, ct);
            values["Salary"] = (code is null ? "" : code + " ") + offer.OfferedSalary.ToString("N2", CultureInfo.InvariantCulture);
        }
        if (used.Contains("OrganizationName"))
        {
            var name = _names is null ? null : await _names.GetNameAsync(org, ct);
            if (string.IsNullOrWhiteSpace(name)) name = organizationName?.Trim();
            values["OrganizationName"] = string.IsNullOrWhiteSpace(name)
                ? throw HrmErrors.Bad("LETTER_ORGANIZATION_REQUIRED", "The organization's name could not be found: give organizationName.")
                : name;
        }
        return values;
    }

    private async Task<string?> CurrencyCodeAsync(Guid org, CancellationToken ct)
    {
        if (_currency is null || _currencies is null) return null;
        var id = await _currency.GetBaseCurrencyIdAsync(org);
        return id is { } currencyId ? (await _currencies.GetAsync(org, currencyId, ct))?.Code : null;
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    public async Task<OfferLetterFile> ReadLetterAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.HRM_SALARY_VIEW, "An offer letter states the salary: it needs the permission to view salaries.");
        var (offer, _) = await RequireOfferAsync(caller, uuid, ct);
        if (offer.LetterAttachmentUuid is not { } attachment) throw new NotFoundException("Offer letter", uuid);
        var content = await _documents.ReadAsync(attachment) ?? throw new NotFoundException("Offer letter", uuid);
        return new OfferLetterFile(content.Content, content.FileName, content.ContentType);
    }
}

internal static class OfferLetterPdf
{
    /// <summary>Through PdfRenderGate: parallel QuestPDF renders corrupt bold text.</summary>
    public static byte[] Render(string subject, string body) => PdfRenderGate.Run(() => Document.Create(document =>
    {
        document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(50);
            page.DefaultTextStyle(t => t.FontSize(11));
            page.Content().Column(column =>
            {
                column.Spacing(14);
                column.Item().Text(subject).Bold().FontSize(14);
                column.Item().Text(body);
            });
        });
    }).GeneratePdf());
}
